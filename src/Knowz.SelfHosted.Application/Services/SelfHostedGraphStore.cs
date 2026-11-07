using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Destination-owned, complete graph persistence. Original crypto bindings never become ACL identities.</summary>
public sealed partial class SelfHostedGraphStore(
    SelfHostedDbContext db, ITenantProvider tenant, PortableImportService importer,
    PortableExportService exporter, IFileStorageProvider storage)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Guid DestinationId(Guid tenantId, string lineage, string kind, Guid sourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"knowz-graph-v1|{tenantId:D}|{lineage}|{kind}|{sourceId:D}"));
        hash[6] = (byte)((hash[6] & 15) | 0x80); hash[8] = (byte)((hash[8] & 63) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
    public Task<PortableImportResult> ImportAsync(PortableExportPackage source, Guid? targetVaultId,
        string lineage, ImportConflictStrategy strategy, Func<string, CancellationToken, Task<Stream>>? openBinary,
        CancellationToken ct, IReadOnlyDictionary<Guid, Guid>? vaultMappings = null, SelfHostedGraphStage? preparedStage = null, Guid? importPlanId = null)
        => ImportCoreAsync(source, targetVaultId, lineage, strategy, openBinary, ct, vaultMappings, preparedStage, importPlanId: importPlanId);

    private async Task<PortableImportResult> ImportCoreAsync(PortableExportPackage source, Guid? targetVaultId,
        string lineage, ImportConflictStrategy strategy, Func<string, CancellationToken, Task<Stream>>? openBinary,
        CancellationToken ct, IReadOnlyDictionary<Guid, Guid>? vaultMappings = null, SelfHostedGraphStage? preparedStage = null, bool preserveEntityIds = false, Guid? importPlanId = null, IReadOnlyDictionary<Guid, Guid>? preparedContextMappings = null)
    {
        var contextMappings = preparedContextMappings ?? (preserveEntityIds && targetVaultId.HasValue
            ? await ResolvePeerContextMappingsAsync(source, targetVaultId.Value, lineage, ct) : null);
        var error = await ValidateImportAsync(source, targetVaultId, lineage, vaultMappings, ct, contextMappings);
        if (error != null) return new() { Error = error };
        if (!Enum.IsDefined(strategy) || string.IsNullOrWhiteSpace(lineage)) return new() { Error = "Invalid graph import options." };
        if (targetVaultId.HasValue && !await db.Vaults.AnyAsync(v => v.Id == targetVaultId, ct)) return new() { Error = "Target vault is unavailable." };
        foreach (var target in (vaultMappings ?? new Dictionary<Guid, Guid>()).Values)
            if (!await db.Vaults.AnyAsync(v => v.Id == target, ct)) return new() { Error = "Mapped vault is unavailable." };
        // Mapping is part of copy identity: copying one source into a different destination vault does not overwrite the first copy.
        using var recoveryImport = SelfHostedInactiveShelf.Owners(source).Any() ? db.BeginInactiveRecoveryImport() : null;
        var scope = ImportScope(source, targetVaultId, lineage, vaultMappings);
        var p = Clone<PortableExportPackage>(source);
        foreach (var sealedItem in source.SealedItems ?? [])
        {
            if (p.Data.KnowledgeItems.Any(k => k.Id == sealedItem.Id)) continue;
            var metadata = Clone<PortableKnowledge>(sealedItem);
            metadata.Title = "Encrypted item"; metadata.Content = ""; metadata.Summary = null;
            p.Data.KnowledgeItems.Add(metadata);
        }
        p.Data.KnowledgeItems.AddRange(SelfHostedInactiveShelf.Placeholders(source));
        Guid Map(string kind, Guid id) => kind == "context" && contextMappings != null && contextMappings.TryGetValue(id, out var context) ? context
            : preserveEntityIds && SyncEntityKinds.Contains(kind) ? id : DestinationId(tenant.TenantId, scope, kind, id);
        Guid Vault(Guid id) => targetVaultId ?? (vaultMappings != null && vaultMappings.TryGetValue(id, out var mapped) ? mapped : Map("vault", id));
        RemapData(p.Data, Map, Vault);
        if (targetVaultId.HasValue || vaultMappings is { Count: > 0 })
            p.Data.Vaults.RemoveAll(v => (targetVaultId.HasValue && v.Id == targetVaultId) || vaultMappings!.Values.Contains(v.Id));
        var refOwnerIds = (source.PayloadKeyReferences ?? []).Select(r => Map(r.OwnerKind, r.OwnerId)).Distinct().ToArray();
        var existingRefOwners = (await db.ShPayloadKeyReferences.AsNoTracking().Where(r => refOwnerIds.Contains(r.OwnerId)).Select(r => new { r.OwnerKind, r.OwnerId }).ToListAsync(ct))
            .Select(r => $"{r.OwnerKind}:{r.OwnerId:D}").ToHashSet();
        var graphStage = preparedStage ?? new SelfHostedGraphStage { OpenBinary = openBinary };
        var stages = graphStage.Objects;
        var obsolete = graphStage.Obsolete;
        var stagedFiles = graphStage.Files;

        foreach (var k in p.Data.KnowledgeItems.Where(k => (source.SealedItems ?? []).Any(s => Map("knowledge", s.Id) == k.Id)))
        { k.Content = ""; k.Summary = null; k.BriefSummary = null; k.IsIndexed = false; k.IndexedAt = null; }
        try
        {
            if (preparedStage == null)
                await StageFilesAsync(source, targetVaultId, lineage, strategy, ct, vaultMappings, graphStage, preserveEntityIds, contextMappings);
            foreach (var f in p.Data.FileRecords)
            {
                if (stagedFiles.TryGetValue(f.Id, out var file)) f.BlobUri = file.Uri;
                f.BinaryContentBase64 = null; f.BinaryFilePath = null; f.BinaryEntryPath = null;
            }
            await using var transaction = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
            var plain = new PortableExportPackage { SourceEdition = "selfhosted", SourceTenantId = tenant.TenantId, SchemaVersion = CoreSchema.Version, Data = p.Data };
            // Sealed comment and file metadata live exclusively in typed sealed tables.
            plain.Data.Comments.RemoveAll(c => (source.SealedComments ?? []).Any(s => Map("comment", s.Id) == c.Id));
            plain.Data.FileRecords.RemoveAll(f => (source.SealedFiles ?? []).Any(s => Map("file", s.Id) == f.Id));
            var result = await importer.ImportMappedMetadataAsync(plain, strategy,
                vaultMappings?.Values.Distinct().ToArray() ?? [], ct, targetVaultId);
            if (!result.Success) throw new InvalidDataException(result.Error ?? "Plain graph import failed.");
            await RestorePlainHistoryAndRelationshipsAsync(source, Map, strategy, ct);
            foreach (var context in source.CryptoContexts ?? [])
            {
                var row = Clone<ShPortableCryptoContext>(context); row.Id = Map("context", context.ContextId); row.TenantId = tenant.TenantId;
                row.ProvenanceStatus = lineage.StartsWith("trusted:", StringComparison.Ordinal) ? "archive-trusted" : "archive-unverified";
                await PutAsync(db.ShPortableCryptoContexts, row.Id, row, false, ct);
            }
            foreach (var key in source.CryptoKeyCatalog ?? [])
            {
                var row = Clone<ShCryptoKeyIdentity>(key); row.Id = Map("key:" + key.Generation + ":" + key.Fingerprint, key.ContextId); row.TenantId = tenant.TenantId;
                row.PortableCryptoContextId = Map("context", key.ContextId); row.SourceLifecycle = key.SourceLifecycle ?? key.Lifecycle; row.Lifecycle = "decrypt-only";
                if (contextMappings != null)
                {
                    var canonical = await db.ShCryptoKeyIdentities.SingleOrDefaultAsync(k => k.TenantId == tenant.TenantId
                        && k.PortableCryptoContextId == row.PortableCryptoContextId && k.Generation == row.Generation, ct);
                    if (canonical != null) continue; // Canonical reuse retains local lifecycle and explicit primary-write selection.
                }
                var existing = await db.ShCryptoKeyIdentities.SingleOrDefaultAsync(k => k.Id == row.Id, ct);
                if (existing != null && row.SourceLifecycle is not ("active" or "Active"))
                {
                    existing.SourceLifecycle = row.SourceLifecycle; existing.Lifecycle = "decrypt-only";
                    var primary = await db.ShVaultCryptoContextLinks.Where(l => l.PortableCryptoContextId == row.PortableCryptoContextId && l.PrimaryWriteFingerprint == row.Fingerprint && (!l.PrimaryWriteGeneration.HasValue || l.PrimaryWriteGeneration == row.Generation)).ToListAsync(ct);
                    foreach (var link in primary) { link.IsPrimaryWriteContext = false; link.PrimaryWriteFingerprint = null; link.PrimaryWriteGeneration = null; }
                }
                await PutAsync(db.ShCryptoKeyIdentities, row.Id, row, false, ct);
            }
            foreach (var link in source.VaultCryptoContextLinks ?? [])
            {
                var row = new ShVaultCryptoContextLink { Id = Map("context-link:" + link.ContextId, link.VaultId), TenantId = tenant.TenantId,
                    DestinationVaultId = Vault(link.VaultId), PortableCryptoContextId = Map("context", link.ContextId), IsPrimaryWriteContext = false };
                if (!await db.ShVaultCryptoContextLinks.AnyAsync(x => x.DestinationVaultId == row.DestinationVaultId && x.PortableCryptoContextId == row.PortableCryptoContextId, ct))
                    await PutAsync(db.ShVaultCryptoContextLinks, row.Id, row, false, ct);
            }
            foreach (var item in source.SealedItems ?? [])
            {
                var id = Map("knowledge", item.Id);
                var previous = await db.ShSealedKnowledge.SingleOrDefaultAsync(x => x.KnowledgeId == id, ct);
                if (previous != null && strategy != ImportConflictStrategy.Overwrite) continue;
                var row = Clone<ShSealedKnowledge>(item); row.Id = previous?.Id ?? Map("sealed-row", item.Id); row.KnowledgeId = id; row.TenantId = tenant.TenantId; row.LineageScope = scope;
                row.LifecycleStatus = SelfHostedSealedStore.LockedStatus;
                await PutAsync(db.ShSealedKnowledge, row.Id, row, true, ct);
            }
            foreach (var comment in source.SealedComments ?? [])
            {
                var row = Clone<ShSealedComment>(comment); row.Id = Map("comment", comment.Id); row.TenantId = tenant.TenantId; row.LineageScope = scope;
                row.KnowledgeId = Map("knowledge", comment.KnowledgeId); row.ParentCommentId = comment.ParentCommentId.HasValue ? Map("comment", comment.ParentCommentId.Value) : null;
                await PutAsync(db.ShSealedComments, row.Id, row, strategy == ImportConflictStrategy.Overwrite, ct);
            }
            foreach (var version in source.SealedVersions ?? [])
            {
                var row = Clone<ShSealedVersion>(version); row.Id = Map("version", version.Id); row.TenantId = tenant.TenantId; row.KnowledgeId = Map("knowledge", version.KnowledgeId);
                await PutAsync(db.ShSealedVersions, row.Id, row, false, ct);
            }
            foreach (var file in source.SealedFiles ?? [])
            {
                var row = Clone<ShSealedFile>(file); row.Id = Map("file", file.Id); row.TenantId = tenant.TenantId;
                row.KnowledgeId = file.KnowledgeId.HasValue ? Map("knowledge", file.KnowledgeId.Value) : null;
                row.VaultId = file.VaultId.HasValue ? Vault(file.VaultId.Value) : null;
                row.RootFileRecordId = file.RootFileRecordId.HasValue ? Map("file", file.RootFileRecordId.Value) : null;
                row.PreviousFileRecordId = file.PreviousFileRecordId.HasValue ? Map("file", file.PreviousFileRecordId.Value) : null;
                if (stagedFiles.TryGetValue(row.Id, out var stage)) { row.BlobStorageId = stage.Blob; row.BinarySha256 = stage.Sha; row.BinaryContentBase64 = null; }
                await PutAsync(db.ShSealedFiles, row.Id, row, strategy == ImportConflictStrategy.Overwrite, ct);
            }
            foreach (var attachment in source.SealedAttachments ?? [])
            {
                var row = Clone<ShSealedAttachment>(attachment); row.Id = Map("attachment", attachment.Id); row.TenantId = tenant.TenantId;
                row.FileRecordId = Map("file", attachment.FileRecordId);
                row.KnowledgeId = attachment.KnowledgeId.HasValue ? Map("knowledge", attachment.KnowledgeId.Value) : null;
                row.CommentId = attachment.CommentId.HasValue ? Map("comment", attachment.CommentId.Value) : null;
                row.InboxId = attachment.InboxId.HasValue ? Map("inbox", attachment.InboxId.Value) : null;
                await PutAsync(db.ShSealedAttachments, row.Id, row, strategy == ImportConflictStrategy.Overwrite, ct);
            }
            foreach (var reference in source.PayloadKeyReferences ?? [])
            {
                if ((strategy != ImportConflictStrategy.Overwrite || reference.OwnerKind == "version")
                    && existingRefOwners.Contains($"{reference.OwnerKind}:{Map(reference.OwnerKind, reference.OwnerId):D}")) continue;
                var row = Clone<ShPayloadKeyReference>(reference); row.Id = Map("ref:" + reference.OwnerKind + ":" + reference.Role + ":" + reference.FieldName, reference.OwnerId); row.TenantId = tenant.TenantId;
                row.OwnerId = Map(reference.OwnerKind, reference.OwnerId); row.ContextId = Map("context", reference.ContextId);
                row.VersionId = reference.VersionId.HasValue ? Map("version", reference.VersionId.Value) : null;
                if (preparedContextMappings != null && strategy == ImportConflictStrategy.Overwrite && reference.OwnerKind != "version")
                {
                    var current = await db.ShPayloadKeyReferences.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId
                        && r.OwnerKind == row.OwnerKind && r.OwnerId == row.OwnerId && r.Role == row.Role && r.FieldName == row.FieldName, ct);
                    if (current != null) row.Id = current.Id;
                }
                await PutAsync(db.ShPayloadKeyReferences, row.Id, row, strategy == ImportConflictStrategy.Overwrite, ct);
            }
            foreach (var (id, stage) in stagedFiles.Where(x => source.Data.FileRecords.Any(f => Map("file", f.Id) == x.Key) && !(source.SealedFiles ?? []).Any(f => Map("file", f.Id) == x.Key)))
            {
                var existing = await db.ShFileObjectReferences.SingleOrDefaultAsync(x => x.FileRecordId == id, ct);
                if (existing == null) db.ShFileObjectReferences.Add(new() { FileRecordId = id, TenantId = tenant.TenantId, BlobStorageId = stage.Blob, Sha256 = stage.Sha });
                else { existing.BlobStorageId = stage.Blob; existing.Sha256 = stage.Sha; }
            }
            await SelfHostedInactiveShelf.PreserveAsync(db, tenant.TenantId, source, Map, Vault, strategy, importPlanId, ct);
            foreach (var old in obsolete) SelfHostedObjectCleanup.Queue(db, tenant.TenantId, Guid.Empty, old);
            await db.SaveChangesAsync(ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            // A failed cleanup leaves an inaccessible immutable old object, never a missing active object.
            foreach (var old in transaction != null ? obsolete : []) { try { await storage.DeleteAsync(tenant.TenantId, old, ct); } catch { /* journal reconciliation owns retry */ } }
            return result;
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            foreach (var stage in preparedStage == null ? stages : []) { try { await storage.DeleteAsync(tenant.TenantId, stage, CancellationToken.None); } catch { } }
            if (ex is OperationCanceledException || preparedStage != null) throw;
            return new() { Error = ex.Message, StrategyUsed = strategy };
        }
    }

    public async Task<string?> ValidateImportAsync(PortableExportPackage source, Guid? targetVaultId, string lineage, IReadOnlyDictionary<Guid, Guid>? mappings, CancellationToken ct,
        IReadOnlyDictionary<Guid, Guid>? contextMappings = null)
    {
        var error = EncryptedGraphProfile.Validate(source) ?? ValidateHistory(source);
        if (error != null) return error;
        // This public extension is a known logical reference; other opaque metadata is not remapped.
        var declaredVaults = source.Data.Vaults.Select(v => v.Id).ToHashSet();
        foreach (var file in source.Data.FileRecords)
            if (file.ExtensionData?.TryGetValue("vaultId", out var ownerVault) == true && ownerVault.ValueKind != JsonValueKind.Null
                && (ownerVault.ValueKind != JsonValueKind.String || !ownerVault.TryGetGuid(out var vaultId) || !declaredVaults.Contains(vaultId)))
                return "A file vault reference must identify a declared source vault.";
        if ((source.SealedFiles ?? []).Any(f => f.OriginalSizeBytes is < 0 or > EncryptedGraphProfile.MaxFileBytes))
            return "Original file size must be null or between zero and 64 MiB.";
        var scope = ImportScope(source, targetVaultId, lineage, mappings);
        Guid Map(string kind, Guid id) => kind == "context" && contextMappings != null && contextMappings.TryGetValue(id, out var context) ? context
            : DestinationId(tenant.TenantId, scope, kind, id);
        var recoveryError = await SelfHostedInactiveShelf.ValidateAsync(db, tenant.TenantId, source, Map, ct);
        if (recoveryError != null) return recoveryError;
        foreach (var context in source.CryptoContexts ?? [])
        {
            var mapped = Map("context", context.ContextId);
            var existing = await db.ShPortableCryptoContexts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == mapped, ct);
            if (existing != null && (existing.OriginalBindingId != context.OriginalBindingId || existing.BindingKind != context.BindingKind || existing.AlgorithmProfile != context.AlgorithmProfile))
                return "Original encryption context identity cannot be replaced.";
        }
        foreach (var key in source.CryptoKeyCatalog ?? [])
        {
            var mapped = Map("context", key.ContextId);
            var existing = await db.ShCryptoKeyIdentities.AsNoTracking().SingleOrDefaultAsync(k => k.PortableCryptoContextId == mapped && k.Generation == key.Generation, ct);
            if (existing != null && (existing.PublicKey != key.PublicKey || existing.Fingerprint != key.Fingerprint || existing.KeyType != key.KeyType || existing.Custody != key.Custody))
                return "Original key generation identity cannot be replaced.";
        }
        return null;
    }

    public static string ImportScope(PortableExportPackage source, Guid? targetVaultId, string lineage, IReadOnlyDictionary<Guid, Guid>? vaultMappings)
        => lineage + "|" + source.SourceTenantId.ToString("D") + "|" + (targetVaultId?.ToString("D") ?? "tenant")
            + "|" + string.Join(',', (vaultMappings ?? new Dictionary<Guid, Guid>()).OrderBy(p => p.Key).Select(p => $"{p.Key:D}:{p.Value:D}"));

    public async Task<SelfHostedGraphStage> StageFilesAsync(PortableExportPackage source, Guid? targetVaultId,
        string lineage, ImportConflictStrategy strategy, CancellationToken ct, IReadOnlyDictionary<Guid, Guid>? mappings = null,
        SelfHostedGraphStage? stage = null, bool preserveEntityIds = false, IReadOnlyDictionary<Guid, Guid>? contextMappings = null)
    {
        var error = await ValidateImportAsync(source, targetVaultId, lineage, mappings, ct, contextMappings);
        if (error != null) throw new InvalidDataException(error);
        stage ??= new();
        var scope = ImportScope(source, targetVaultId, lineage, mappings);
        Guid Map(Guid id) => preserveEntityIds ? id : DestinationId(tenant.TenantId, scope, "file", id);
        try
        {
            foreach (var file in source.SealedFiles ?? [])
            {
                var id = Map(file.Id);
                var existing = await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                if (existing != null && strategy != ImportConflictStrategy.Overwrite)
                {
                    if (existing.BinaryContentBase64 != null)
                    {
                        var bytes = Convert.FromBase64String(existing.BinaryContentBase64);
                        if (bytes.LongLength != existing.SizeBytes || existing.BinarySha256 != null && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != existing.BinarySha256) throw new InvalidDataException("Existing inline file integrity failed.");
                    }
                    else await RetainAsync(existing.BlobStorageId ?? id, existing.SizeBytes, existing.BinarySha256);
                    continue;
                }
                await StageAsync(id, file.BinaryEntryPath, file.BinaryContentBase64, file.SizeBytes, file.BinarySha256, "application/octet-stream");
                if (existing?.BlobStorageId is Guid old) stage.Obsolete.Add(old);
            }
            foreach (var file in source.Data.FileRecords)
            {
                if (source.SealedFiles?.Any(f => f.Id == file.Id) == true) continue;
                var id = Map(file.Id);
                var oldRecord = await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
                if (strategy != ImportConflictStrategy.Overwrite && oldRecord != null)
                {
                    var oldReference = await db.ShFileObjectReferences.AsNoTracking().SingleOrDefaultAsync(f => f.FileRecordId == id, ct);
                    await RetainAsync(oldReference?.BlobStorageId ?? id, oldRecord.SizeBytes, oldReference?.Sha256); continue;
                }
                await StageAsync(id, file.BinaryEntryPath ?? file.BinaryFilePath, file.BinaryContentBase64, file.SizeBytes, file.BinarySha256, file.ContentType ?? "application/octet-stream");
                var existing = await db.ShFileObjectReferences.AsNoTracking().SingleOrDefaultAsync(f => f.FileRecordId == id, ct);
                if (existing != null) stage.Obsolete.Add(existing.BlobStorageId);
            }
            return stage;
        }
        catch { await CleanupStageAsync(stage, CancellationToken.None); throw; }

        async Task RetainAsync(Guid blob, long length, string? digest)
        {
            await using var bytes = (await storage.DownloadAsync(tenant.TenantId, blob, ct)).stream;
            using var verified = new VerifiedBinaryStream(bytes, length, digest); await verified.CopyToAsync(Stream.Null, ct); verified.RequireComplete();
            stage.RetainedFiles.Add((blob, verified.Digest!, length));
        }

        async Task StageAsync(Guid id, string? path, string? inline, long length, string? digest, string contentType)
        {
            if (stage.Files.ContainsKey(id)) return;
            await using var bytes = path != null
                ? stage.OpenBinary == null ? throw new InvalidDataException("Archive binary resolver is required.") : await stage.OpenBinary(path, ct)
                : inline != null ? new MemoryStream(Convert.FromBase64String(inline)) : throw new InvalidDataException("Complete graph file bytes are missing.");
            var blob = Guid.NewGuid(); stage.Objects.Add(blob);
            if (stage.BeforeUpload != null) await stage.BeforeUpload(blob, ct);
            using var verified = new VerifiedBinaryStream(bytes, length, digest);
            var uri = await storage.UploadAsync(tenant.TenantId, blob, verified, contentType, ct);
            verified.RequireComplete(); stage.Files[id] = (blob, verified.Digest!, uri, length);
        }
    }
    public async Task VerifyStageAsync(PortableExportPackage source, SelfHostedGraphStage stage, CancellationToken ct)
    {
        foreach (var file in stage.Files.Values.Select(f => (f.Blob, f.Sha, f.Length)).Concat(stage.RetainedFiles))
        {
            await using var bytes = (await storage.DownloadAsync(tenant.TenantId, file.Blob, ct)).stream;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0; var buffer = new byte[81920];
            while (true) { var n = await bytes.ReadAsync(buffer, ct); if (n == 0) break; count += n; if (count > KnowzArchiveLimits.MaxArchiveBytes) throw new InvalidDataException("Staged object exceeds limits."); hash.AppendData(buffer.AsSpan(0, n)); }
            if (count != file.Length || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha) throw new InvalidDataException("Staged object integrity failed.");
        }
    }

    public async Task CleanupStageAsync(SelfHostedGraphStage stage, CancellationToken ct)
    {
        foreach (var blob in stage.Objects.ToArray())
        {
            await storage.DeleteAsync(tenant.TenantId, blob, ct);
            stage.Objects.Remove(blob);
        }
    }

    public async Task<PortableExportPackage> ExportAsync(ExportScope scope, bool allowPartial, CancellationToken ct)
    {
        var package = await exporter.ExportMetadataAsync(scope, true, ct);
        package.SchemaVersion = EncryptedGraphProfile.SchemaVersion; package.RequiredCapabilities = [EncryptedGraphProfile.Capability];
        var itemIds = package.Data.KnowledgeItems.Select(x => x.Id).ToHashSet();
        var vaultIds = package.Data.Vaults.Select(x => x.Id).ToHashSet();
        var items = await db.ShSealedKnowledge.AsNoTracking().Where(x => itemIds.Contains(x.KnowledgeId)).ToListAsync(ct);
        package.SealedItems = items.Select(row => {
            var metadata = package.Data.KnowledgeItems.Single(k => k.Id == row.KnowledgeId);
            var dto = Clone<SealedKnowledgeItem>(row); dto.Id = row.KnowledgeId;
            dto.VaultIds = metadata.VaultIds; dto.PrimaryVaultId = metadata.PrimaryVaultId;
            dto.TopicId = metadata.TopicId; dto.TagIds = metadata.TagIds; dto.PersonIds = metadata.PersonIds;
            dto.LocationIds = metadata.LocationIds; dto.EventIds = metadata.EventIds;
            dto.PersonLinks = metadata.PersonLinks; dto.LocationLinks = metadata.LocationLinks; dto.EventLinks = metadata.EventLinks;
            dto.Relationships = metadata.Relationships; dto.Type = metadata.Type;
            return dto;
        }).ToList();
        await ExportPlainHistoryAndRelationshipsAsync(package, itemIds, ct);
        package.Data.KnowledgeItems.RemoveAll(k => items.Any(s => s.KnowledgeId == k.Id));
        package.SealedComments = (await db.ShSealedComments.AsNoTracking().Where(x => itemIds.Contains(x.KnowledgeId)).ToListAsync(ct)).Select(Clone<SealedCommentRecord>).ToList();
        package.SealedVersions = (await db.ShSealedVersions.AsNoTracking().Where(x => itemIds.Contains(x.KnowledgeId)).ToListAsync(ct)).Select(Clone<SealedKnowledgeVersionRecord>).ToList();
        var commentIds = package.SealedComments.Select(x => x.Id).Concat(package.Data.Comments.Select(x => x.Id)).ToHashSet();
        var inboxIds = package.Data.InboxItems.Select(x => x.Id).ToHashSet();
        package.SealedAttachments = (await db.ShSealedAttachments.AsNoTracking().Where(x => (x.KnowledgeId.HasValue && itemIds.Contains(x.KnowledgeId.Value)) || (x.CommentId.HasValue && commentIds.Contains(x.CommentId.Value)) || (x.InboxId.HasValue && inboxIds.Contains(x.InboxId.Value))).ToListAsync(ct)).Select(Clone<SealedAttachmentRecord>).ToList();
        var fileIds = package.SealedAttachments.Select(x => x.FileRecordId).ToHashSet();
        var files = await db.ShSealedFiles.AsNoTracking().Where(x => scope.Type == ExportScopeType.Tenant || x.VaultId.HasValue && vaultIds.Contains(x.VaultId.Value) || fileIds.Contains(x.Id) || (x.KnowledgeId.HasValue && itemIds.Contains(x.KnowledgeId.Value))).ToListAsync(ct);
        var pendingAncestors = files.Where(f => f.PreviousFileRecordId.HasValue).Select(f => f.PreviousFileRecordId!.Value).Except(files.Select(f => f.Id)).ToList();
        while (pendingAncestors.Count > 0)
        {
            var ancestors = await db.ShSealedFiles.AsNoTracking().Where(f => pendingAncestors.Contains(f.Id)).ToListAsync(ct);
            files.AddRange(ancestors);
            pendingAncestors = ancestors.Where(f => f.PreviousFileRecordId.HasValue).Select(f => f.PreviousFileRecordId!.Value).Except(files.Select(f => f.Id)).ToList();
        }
        package.SealedFiles = files.Select(row => { var dto = Clone<SealedFileRecord>(row); dto.BinaryEntryPath = row.BlobStorageId.HasValue ? $"files/{row.Id:D}.bin" : null; if (dto.KnowledgeId.HasValue && !itemIds.Contains(dto.KnowledgeId.Value)) dto.KnowledgeId = null; if (dto.VaultId.HasValue && !vaultIds.Contains(dto.VaultId.Value)) dto.VaultId = null; return dto; }).ToList();
        var links = await db.ShVaultCryptoContextLinks.AsNoTracking().Where(x => vaultIds.Contains(x.DestinationVaultId)).ToListAsync(ct);
        package.VaultCryptoContextLinks = links.Select(x => new ExportedVaultCryptoContextLink { VaultId = x.DestinationVaultId, ContextId = x.PortableCryptoContextId, IsPrimaryWriteContext = x.IsPrimaryWriteContext, PrimaryWriteFingerprint = x.PrimaryWriteFingerprint, PrimaryWriteGeneration = x.PrimaryWriteGeneration }).ToList();
        var contexts = links.Select(x => x.PortableCryptoContextId).ToHashSet();
        package.CryptoContexts = (await db.ShPortableCryptoContexts.AsNoTracking().Where(x => contexts.Contains(x.Id)).ToListAsync(ct)).Select(x => { var dto = Clone<ExportedCryptoContext>(x); dto.ContextId = x.Id; return dto; }).ToList();
        package.CryptoKeyCatalog = (await db.ShCryptoKeyIdentities.AsNoTracking().Where(x => contexts.Contains(x.PortableCryptoContextId)).ToListAsync(ct)).Select(x => { var dto = Clone<ExportedCryptoKeyIdentity>(x); dto.ContextId = x.PortableCryptoContextId; return dto; }).ToList();
        var owners = package.SealedItems.Select(x => x.Id).Concat(package.SealedComments.Select(x => x.Id)).Concat(package.SealedVersions.Select(x => x.Id)).Concat(package.SealedFiles.Select(x => x.Id)).Concat(package.SealedAttachments.Select(x => x.Id)).Concat(package.Data.FileRecords.Select(x => x.Id)).Concat(package.Data.KnowledgeItems.Select(x => x.Id)).Concat((package.PlainVersions ?? []).Select(x => x.Id)).ToHashSet();
        package.PayloadKeyReferences = (await db.ShPayloadKeyReferences.AsNoTracking().Where(x => owners.Contains(x.OwnerId)).ToListAsync(ct)).Select(Clone<PortablePayloadKeyReference>).ToList();
        await SelfHostedInactiveShelf.AppendExportAsync(db, package, ct);
        package.Omissions = [];
        var error = EncryptedGraphProfile.Validate(package);
        if (error != null)
        {
            if (!allowPartial) throw new InvalidDataException(error);
            package.Omissions.Add(new() { OwnerKind = "graph", OwnerId = tenant.TenantId, Reason = error }); package.PreservationState = "partial";
        }
        return package;
    }

    public async Task<Stream> OpenFileAsync(Guid fileId, CancellationToken ct)
    {
        var row = await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fileId, ct);
        if (row != null)
        {
            if (row.BlobStorageId.HasValue) return (await storage.DownloadAsync(tenant.TenantId, row.BlobStorageId.Value, ct)).stream;
            return new MemoryStream(Convert.FromBase64String(row.BinaryContentBase64 ?? throw new InvalidDataException("Sealed file bytes unavailable.")));
        }
        var physicalId = await SelfHostedFileObjects.ResolveStorageIdAsync(db, tenant.TenantId, fileId, ct);
        return (await storage.DownloadAsync(tenant.TenantId, physicalId, ct)).stream;
    }
    private async Task PutAsync<T>(DbSet<T> set, Guid id, T row, bool overwrite, CancellationToken ct) where T : class
    {
        var existing = await set.FindAsync([id], ct);
        if (existing == null) set.Add(row);
        else
        {
            if ((Guid)typeof(T).GetProperty("TenantId")!.GetValue(existing)! != tenant.TenantId) throw new UnauthorizedAccessException("Destination object belongs to another tenant.");
            if (overwrite) db.Entry(existing).CurrentValues.SetValues(row);
        }
    }
    private static T Clone<T>(object value) => PortablePublicProjection.Project<T>(value) is T projected ? projected : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    internal static void RemapData(PortableExportData d, Func<string, Guid, Guid> map, Func<Guid, Guid> vault)
    {
        Guid? Optional(string kind, Guid? id) => id.HasValue ? map(kind, id.Value) : null;
        foreach (var v in d.Vaults) { v.Id = vault(v.Id); v.ParentVaultId = v.ParentVaultId.HasValue ? vault(v.ParentVaultId.Value) : null; v.PersonIds = v.PersonIds.Select(x => map("person", x)).ToList(); }
        foreach (var k in d.KnowledgeItems)
        {
            k.Id = map("knowledge", k.Id); k.TopicId = Optional("topic", k.TopicId); k.VaultIds = k.VaultIds.Select(vault).Distinct().ToList(); k.PrimaryVaultId = k.PrimaryVaultId.HasValue ? vault(k.PrimaryVaultId.Value) : null;
            k.TagIds = k.TagIds.Select(x => map("tag", x)).ToList(); k.PersonIds = k.PersonIds.Select(x => map("person", x)).ToList(); k.LocationIds = k.LocationIds.Select(x => map("location", x)).ToList(); k.EventIds = k.EventIds.Select(x => map("event", x)).ToList();
            foreach (var l in k.PersonLinks ?? []) l.EntityId = map("person", l.EntityId);
            foreach (var l in k.LocationLinks ?? []) l.EntityId = map("location", l.EntityId);
            foreach (var l in k.EventLinks ?? []) l.EntityId = map("event", l.EntityId);
        }
        foreach (var x in d.Topics) x.Id = map("topic", x.Id);
        foreach (var x in d.Tags) x.Id = map("tag", x.Id);
        foreach (var x in d.Persons) x.Id = map("person", x.Id);
        foreach (var x in d.Locations) x.Id = map("location", x.Id);
        foreach (var x in d.Events) x.Id = map("event", x.Id);
        foreach (var x in d.InboxItems) x.Id = map("inbox", x.Id);
        foreach (var c in d.Comments) { c.Id = map("comment", c.Id); c.KnowledgeId = map("knowledge", c.KnowledgeId); c.ParentCommentId = Optional("comment", c.ParentCommentId); }
        foreach (var f in d.FileRecords)
        {
            f.Id = map("file", f.Id); f.RootFileRecordId = Optional("file", f.RootFileRecordId); f.PreviousFileRecordId = Optional("file", f.PreviousFileRecordId);
            if (f.ExtensionData?.TryGetValue("vaultId", out var ownerVault) == true && ownerVault.ValueKind == JsonValueKind.String && ownerVault.TryGetGuid(out var vaultId))
                f.ExtensionData["vaultId"] = JsonSerializer.SerializeToElement(vault(vaultId));
            foreach (var a in f.Attachments) { a.KnowledgeId = Optional("knowledge", a.KnowledgeId); a.CommentId = Optional("comment", a.CommentId); }
        }
    }

    internal sealed class VerifiedBinaryStream(Stream inner, long expectedLength, string? expectedDigest) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _read;
        public string? Digest { get; private set; }
        private int Check(ReadOnlySpan<byte> bytes, int count)
        {
            if (count == 0) { Digest ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(); RequireComplete(); return 0; }
            _read += count; if (_read > expectedLength) throw new InvalidDataException("File exceeds its declared length.");
            _hash.AppendData(bytes[..count]); return count;
        }
        public void RequireComplete() { if (_read != expectedLength || Digest == null || expectedDigest != null && Digest != expectedDigest) throw new InvalidDataException("File integrity verification failed."); }
        public override int Read(byte[] buffer, int offset, int count) => Check(buffer.AsSpan(offset), inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); return Check(buffer.Span, n); }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => expectedLength; public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() {} public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class SelfHostedGraphStage
{
    public List<Guid> Objects { get; } = [];
    public List<Guid> Obsolete { get; } = [];
    public List<(Guid Blob, string Sha, long Length)> RetainedFiles { get; } = [];
    public Dictionary<Guid, (Guid Blob, string Sha, string Uri, long Length)> Files { get; } = [];
    public Func<string, CancellationToken, Task<Stream>>? OpenBinary { get; init; }
    public Func<Guid, CancellationToken, Task>? BeforeUpload { get; init; }
}
