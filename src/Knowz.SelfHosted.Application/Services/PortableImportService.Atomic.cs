using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public partial class PortableImportService
{
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web);
    private sealed class LegacyImportState
    {
        internal Guid OperationId = Guid.NewGuid();
        internal Guid TenantId;
        internal Guid ActorId;
        internal Guid? TargetVaultId;
        internal string InputHash = "";
        internal string RevisionHash = "";
        internal string OptionsJson = "";
        internal string? AuthorityBinding;
        internal Guid[] KnowledgeIds = [];
        internal Guid[] FileIds = [];
        internal ImportConflictStrategy Strategy;
        internal Dictionary<Guid, LegacyBinary> Files = new();
        internal Func<CancellationToken, Task>? RequireAuthority;
        internal string ActorNamespace => "legacy-import:" + OperationId.ToString("D");
    }
    private sealed record LegacyBinary(Guid LogicalId, Guid PhysicalId, string Uri, string Sha256, long Length, Guid? PreviousPhysicalId);

    private Task<PortableImportResult> ImportCoreAsync(PortableExportPackage package, ImportConflictStrategy strategy,
        CancellationToken ct, Func<PortableFileRecord, Stream?>? openBinary, Guid? targetVaultId = null,
        IReadOnlyCollection<Guid>? existingVaults = null, Func<CancellationToken, Task>? requireAuthority = null, Guid? trustedActorId = null, string? authorityBinding = null)
    {
        // Existing graph adapters own their transaction and durable media journal. Their explicit
        // internal metadata entry point does not enter this direct-import operation.
        if (_db.Database.CurrentTransaction != null)
            return Task.FromResult(new PortableImportResult { StrategyUsed = strategy,
                Error = "Direct import requires its own atomic operation; use the staged archive metadata path inside an existing transaction." });
        var state = new LegacyImportState { TenantId = _tenantProvider.TenantId, ActorId = trustedActorId ?? _actorProvider?.ActorId ?? Guid.Empty, AuthorityBinding = authorityBinding,
            TargetVaultId = targetVaultId, RequireAuthority = requireAuthority, Strategy = strategy };
        // The configured strategy establishes a legal provider transaction scope. Every failure
        // inside this operation is caught here, so physical uploads and ambiguous COMMITs are never replayed.
        return _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            try
            {
                var input = JsonSerializer.Serialize(package, LegacyJson);
                var frozen = JsonSerializer.Deserialize<PortableExportPackage>(input, LegacyJson)!;
                state.InputHash = LegacyHash(JsonSerializer.Serialize(new { input, strategy, targetVaultId,
                    existingVaults = (existingVaults ?? []).Order().ToArray(), state.TenantId, state.ActorId, state.AuthorityBinding }, LegacyJson));
                return await ImportAttemptAsync(frozen, strategy, ct, openBinary, targetVaultId, existingVaults, state);
            }
            catch (OperationCanceledException ex)
            { await RecoverLegacyImportAsync(state, ex, CancellationToken.None); throw; }
            catch (Exception ex)
            { return await RecoverLegacyImportAsync(state, ex, CancellationToken.None); }
        });
    }

    internal Task<PortableImportResult> ImportStreamAsync(PortableExportPackage package, ImportConflictStrategy strategy,
        Func<PortableFileRecord, Stream?> openBinary, Func<CancellationToken, Task> requireAuthority, Guid actorId,
        string authorityBinding, CancellationToken ct)
        => ImportCoreAsync(package, strategy, ct, openBinary, requireAuthority: requireAuthority,
            trustedActorId: actorId, authorityBinding: authorityBinding);

    private async Task PrepareLegacyImportAsync(LegacyImportState state, PortableExportPackage package,
        ImportConflictStrategy strategy, Func<string, Guid, Guid> resolveId,
        Func<PortableFileRecord, Stream?>? openBinary, Guid? targetVault, CancellationToken ct)
    {
        await RequireLegacyAuthorityAsync(state, ct);
        state.KnowledgeIds = package.Data.KnowledgeItems.Select(k => resolveId("Knowledge", k.Id)).Distinct().Order().ToArray();
        state.FileIds = package.Data.FileRecords.Select(f => resolveId("FileRecord", f.Id)).Distinct().Order().ToArray();
        state.RevisionHash = await LegacyRevisionHashAsync(state, ct);
        var journal = new ShArchiveImportJournal { PlanId = state.OperationId, ArchiveId = state.OperationId,
            TenantId = state.TenantId, ActorId = state.ActorId, ActorNamespace = state.ActorNamespace,
            ManifestSha256 = state.InputHash, MappingSha256 = state.RevisionHash, Status = "preparing" };
        _db.ShArchiveImportJournals.Add(journal); await _db.SaveChangesAsync(ct);
        foreach (var source in package.Data.FileRecords)
        {
            if (string.IsNullOrEmpty(source.BinaryContentBase64) && string.IsNullOrEmpty(source.BinaryFilePath)) continue;
            var id = resolveId("FileRecord", source.Id);
            var previous = await _db.FileRecords.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
            if (previous != null && (previous.TenantId != state.TenantId || previous.IsDeleted)) throw new InvalidDataException("An imported file identity is unavailable in this tenant.");
            if (previous != null && (strategy == ImportConflictStrategy.Skip ||
                strategy == ImportConflictStrategy.Merge && !previous.BlobMigrationPending && !string.IsNullOrEmpty(previous.BlobUri))) continue;
            Guid? oldPhysical = previous == null ? null : await SelfHostedFileObjects.ResolveStorageIdAsync(_db, state.TenantId, id, ct);
            var physical = Guid.NewGuid();
            var owned = JsonSerializer.Deserialize<List<Guid>>(journal.StagedObjectIdsJson)!; owned.Add(physical);
            journal.StagedObjectIdsJson = JsonSerializer.Serialize(owned); journal.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct); // Durable ownership precedes the first byte written.
            await using var input = !string.IsNullOrEmpty(source.BinaryContentBase64)
                ? new MemoryStream(Convert.FromBase64String(source.BinaryContentBase64))
                : openBinary?.Invoke(source) ?? throw new InvalidDataException("Declared import file bytes are unavailable.");
            using var verified = new SelfHostedGraphStore.VerifiedBinaryStream(input, source.SizeBytes, source.BinarySha256);
            var uri = await _storageProvider.UploadAsync(state.TenantId, physical, verified, source.ContentType ?? "application/octet-stream", ct);
            verified.RequireComplete();
            state.Files[id] = new(id, physical, uri, verified.Digest!, source.SizeBytes, oldPhysical);
            await RequireLegacyAuthorityAsync(state, ct);
        }
        state.OptionsJson = JsonSerializer.Serialize(new { format = "knowz-legacy-import-journal/v1", state.OperationId,
            state.TenantId, state.ActorId, state.AuthorityBinding, state.InputHash, state.RevisionHash, targetVault, strategy,
            files = state.Files.Values.OrderBy(f => f.LogicalId).ToArray() }, LegacyJson);
        journal.ImportOptionsJson = state.OptionsJson; journal.Status = "prepared"; journal.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ValidateLegacyActivationAsync(LegacyImportState state, CancellationToken ct)
    {
        await LockLegacyRowsAsync(state, ct);
        await RequireLegacyAuthorityAsync(state, ct);
        if (await LegacyRevisionHashAsync(state, ct) != state.RevisionHash)
            throw new DbUpdateConcurrencyException("Import destination changed while file bytes were staged; review and import again.");
        var journal = await _db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == state.OperationId, ct);
        if (journal.Status != "prepared" || journal.ImportOptionsJson != state.OptionsJson || journal.ManifestSha256 != state.InputHash)
            throw new DbUpdateConcurrencyException("Import staging changed or expired.");
        foreach (var file in state.Files.Values)
        {
            await using var bytes = (await _storageProvider.DownloadAsync(state.TenantId, file.PhysicalId, ct)).stream;
            using var verified = new SelfHostedGraphStore.VerifiedBinaryStream(bytes, file.Length, file.Sha256);
            await verified.CopyToAsync(Stream.Null, ct); verified.RequireComplete();
        }
    }

    private async Task ActivateLegacyBinaryAsync(LegacyImportState state, FileRecord destination, CancellationToken ct)
    {
        if (!state.Files.TryGetValue(destination.Id, out var staged)) throw new InvalidDataException("Import binary was not verified and staged.");
        var pointer = await _db.ShFileObjectReferences.SingleOrDefaultAsync(f => f.FileRecordId == destination.Id, ct);
        if (pointer == null) _db.ShFileObjectReferences.Add(new() { FileRecordId = destination.Id, TenantId = state.TenantId,
            BlobStorageId = staged.PhysicalId, Sha256 = staged.Sha256 });
        else { pointer.BlobStorageId = staged.PhysicalId; pointer.Sha256 = staged.Sha256; }
        destination.BlobUri = staged.Uri; destination.BlobMigrationPending = false;
        if (staged.PreviousPhysicalId.HasValue && staged.PreviousPhysicalId != staged.PhysicalId)
            SelfHostedObjectCleanup.Queue(_db, state.TenantId, destination.Id, staged.PreviousPhysicalId.Value);
    }

    private async Task MarkLegacyCommittedAsync(LegacyImportState state, PortableImportResult result, CancellationToken ct)
    {
        await RequireLegacyAuthorityAsync(state, ct);
        var journal = await _db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == state.OperationId, ct);
        if (journal.Status != "prepared" || journal.ImportOptionsJson != state.OptionsJson || journal.ManifestSha256 != state.InputHash)
            throw new DbUpdateConcurrencyException("Import staging changed or expired.");
        journal.Status = "committed"; journal.CommitId = Guid.NewGuid();
        journal.CommitReceiptJson = JsonSerializer.Serialize(result, LegacyJson); journal.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<PortableImportResult> RecoverLegacyImportAsync(LegacyImportState state, Exception failure, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        try
        {
            var journal = await _db.ShArchiveImportJournals.AsNoTracking().SingleOrDefaultAsync(j => j.PlanId == state.OperationId &&
                j.TenantId == state.TenantId && j.ActorId == state.ActorId && j.ActorNamespace == state.ActorNamespace, ct);
            if (journal?.Status == "committed")
            {
                await RequireLegacyAuthorityAsync(state, ct);
                if (journal.ManifestSha256 != state.InputHash || journal.MappingSha256 != state.RevisionHash ||
                    journal.ImportOptionsJson != state.OptionsJson || journal.CommitReceiptJson == null)
                    throw new InvalidDataException("The committed import identity could not be verified.");
                foreach (var file in state.Files.Values)
                    if (!await _db.ShFileObjectReferences.AsNoTracking().AnyAsync(r => r.TenantId == state.TenantId && r.FileRecordId == file.LogicalId && r.BlobStorageId == file.PhysicalId && r.Sha256 == file.Sha256, ct))
                        throw new InvalidDataException("Committed import file state changed; inspect the destination.");
                return JsonSerializer.Deserialize<PortableImportResult>(journal.CommitReceiptJson, LegacyJson)!;
            }
            if (journal != null)
            {
                // Only this operation's durable owned IDs are eligible. The shared reconciler
                // independently checks live/historical references before each physical deletion.
                var pending = await _db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == state.OperationId, ct);
                pending.Status = "cancelled"; pending.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
                foreach (var physical in JsonSerializer.Deserialize<List<Guid>>(pending.StagedObjectIdsJson) ?? [])
                    if (!await _db.ShFileObjectReferences.IgnoreQueryFilters().AsNoTracking().AnyAsync(f => f.TenantId == state.TenantId && f.BlobStorageId == physical, ct) &&
                        !await _db.ShSealedFiles.IgnoreQueryFilters().AsNoTracking().AnyAsync(f => f.TenantId == state.TenantId && f.BlobStorageId == physical, ct))
                        await _storageProvider.DeleteAsync(state.TenantId, physical, ct);
                pending.StagedObjectIdsJson = "[]"; await _db.SaveChangesAsync(ct);
            }
            return new() { StrategyUsed = state.Strategy, Error = "Import failed; no imported metadata was activated. " + failure.Message };
        }
        catch (Exception recoveryError)
        {
            _db.ChangeTracker.Clear(); _logger.LogWarning(recoveryError, "Legacy import outcome could not be safely recovered; staged and committed objects are retained");
            return new() { StrategyUsed = state.Strategy, Error = "Import outcome could not be confirmed. Inspect the destination before retrying; committed files and recovery staging have been retained." };
        }
    }

    private async Task RequireLegacyAuthorityAsync(LegacyImportState state, CancellationToken ct)
    {
        if (_tenantProvider.TenantId != state.TenantId) throw new UnauthorizedAccessException("Import tenant changed.");
        if (state.RequireAuthority != null) await state.RequireAuthority(ct);
        if (_actorProvider == null) return; // Trusted internal service caller; public requests always inject the scoped actor provider.
        var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == state.ActorId && u.IsActive, ct);
        var membership = await _db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m =>
            m.UserId == state.ActorId && m.TenantId == state.TenantId, ct);
        if (_actorProvider.ActorId != state.ActorId || state.ActorId == Guid.Empty || user == null ||
            (membership != null ? !membership.IsActive || membership.Role != UserRole.SuperAdmin
                : user.TenantId != state.TenantId || user.Role != UserRole.SuperAdmin) ||
            !await _db.Tenants.AsNoTracking().AnyAsync(t => t.Id == state.TenantId && t.IsActive, ct))
            throw new UnauthorizedAccessException("Current administrator authority is required for direct import.");
        if (state.TargetVaultId.HasValue && !await _db.Vaults.AsNoTracking().AnyAsync(v => v.Id == state.TargetVaultId, ct))
            throw new UnauthorizedAccessException("Import destination is no longer available.");
    }

    private async Task<string> LegacyRevisionHashAsync(LegacyImportState state, CancellationToken ct)
    {
        var knowledge = await _db.KnowledgeItems.IgnoreQueryFilters().AsNoTracking().Where(k => state.KnowledgeIds.Contains(k.Id))
            .Select(k => new { k.Id, k.TenantId, k.IsDeleted, k.UpdatedAt, k.Content, k.Title, k.Summary, k.BriefSummary, k.EncryptedFieldsJson, k.OriginalItemId,
                VaultIds = k.KnowledgeVaults.Select(v => v.VaultId).Order().ToList() }).OrderBy(k => k.Id).ToListAsync(ct);
        var files = await _db.FileRecords.IgnoreQueryFilters().AsNoTracking().Where(f => state.FileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.TenantId, f.IsDeleted, f.UpdatedAt, f.FileName, f.ContentType, f.SizeBytes, f.BlobUri, f.BlobMigrationPending,
                f.EncryptedFieldsJson, f.OriginalFileId, f.FileVersion, f.RootFileRecordId, f.PreviousFileRecordId, f.IsCurrentVersion,
                Attachments = f.Attachments.Select(a => new { a.KnowledgeId, a.CommentId }).OrderBy(a => a.KnowledgeId).ThenBy(a => a.CommentId).ToList() }).OrderBy(f => f.Id).ToListAsync(ct);
        var pointers = await _db.ShFileObjectReferences.IgnoreQueryFilters().AsNoTracking().Where(f => state.FileIds.Contains(f.FileRecordId)).OrderBy(f => f.FileRecordId).ToListAsync(ct);
        if (knowledge.Any(k => k.TenantId != state.TenantId || k.IsDeleted) || files.Any(f => f.TenantId != state.TenantId || f.IsDeleted) || pointers.Any(p => p.TenantId != state.TenantId))
            throw new InvalidDataException("An import identity is unavailable in this tenant.");
        return LegacyHash(JsonSerializer.Serialize(new { knowledge, files, pointers }, LegacyJson));
    }

    private async Task LockLegacyRowsAsync(LegacyImportState state, CancellationToken ct)
    {
        if (_db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
        foreach (var (table, ids) in new[] { ("KnowledgeItems", state.KnowledgeIds), ("FileRecords", state.FileIds) })
        {
            if (ids.Length == 0) continue;
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"SELECT \"Id\" FROM \"{table}\" WHERE \"TenantId\" = @tenant AND \"Id\" = ANY(@ids) FOR UPDATE";
            var tenant = command.CreateParameter(); tenant.ParameterName = "tenant"; tenant.Value = state.TenantId; command.Parameters.Add(tenant);
            var identities = command.CreateParameter(); identities.ParameterName = "ids"; identities.Value = ids; command.Parameters.Add(identities);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) { }
        }
    }
    private static string LegacyHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
