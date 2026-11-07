using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedArchiveImportAdapter(SelfHostedDbContext db, ITenantProvider tenant,
    SelfHostedGraphStore graph, IVaultAccessService access, SelfHostedArchivePeerService? peers = null) : IArchiveImportAdapter
{
    private sealed class State
    {
        public Guid PlanId { get; init; }
        public PortableExportPackage Package { get; init; } = null!;
        public string Lineage { get; init; } = "";
        public string Revision { get; set; } = "";
        public HashSet<Guid> AffectedIds { get; } = [];
        public List<SelfHostedGraphStage> Stages { get; } = [];
        public Dictionary<string, string> Mapping { get; } = [];
        public long Unpreserved { get; set; }
        public string? PeerBindingSha256 { get; init; }
        public string ActorNamespace { get; init; } = "";
        public Guid SourceArchiveId { get; init; }
        public string ManifestSha256 { get; init; } = "";
        public string SourceTrust { get; init; } = "";
        public string SignerFingerprint { get; init; } = "";
        public string SourceSnapshot { get; init; } = "";
    }
    public async Task AuthorizeAsync(ArchiveActor actor, ArchiveImportOptions options, ArchivePreparation? prepared, CancellationToken ct)
    {
        if (peer != null) { await AuthorizePeerAsync(actor, options, prepared, ct); return; }
        if (actor.TenantId != tenant.TenantId || !Guid.TryParse(actor.ActorId, out var id)) throw new ArchiveOperationException(403, "Invalid destination actor.");
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == actor.TenantId && t.IsActive, ct))
            throw new ArchiveOperationException(403, "Destination tenant access is unavailable.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
        if (user == null) throw new ArchiveOperationException(403, "Destination access has been revoked.");
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == id && m.TenantId == actor.TenantId, ct);
        if (membership != null ? !membership.IsActive : user.TenantId != actor.TenantId) throw new ArchiveOperationException(403, "Destination tenant access is unavailable.");
        var role = membership?.Role ?? user.Role;
        if (!options.TargetVaultId.HasValue && options.VaultMappings.Count == 0 && role < UserRole.Admin)
            throw new ArchiveOperationException(403, "Tenant archive imports require destination administration access.");
        foreach (var vault in options.VaultMappings.Values.Concat(options.TargetVaultId.HasValue ? [options.TargetVaultId.Value] : []).Distinct())
            if (!await db.Vaults.AnyAsync(v => v.Id == vault, ct) || !await access.HasVaultAccessAsync(id, vault, requireWrite: true, ct: ct)) throw new ArchiveOperationException(403, "Destination vault write access is unavailable.");
    }
    public async Task<ArchivePreparation> PrepareAsync(ValidatedKnowzArchive archive, ArchiveActor actor, ArchiveImportOptions options, Guid planId, CancellationToken ct)
    {
        await ValidatePeerArchiveAsync(archive, ct);
        var state = new State { PlanId = planId, Package = PortableGraphPackages.Combine(archive.Packages), PeerBindingSha256 = peer?.Digest,
            ActorNamespace = actor.ActorId, SourceArchiveId = archive.Manifest.ArchiveId, ManifestSha256 = archive.ManifestSha256,
            SourceTrust = archive.SourceTrust, SignerFingerprint = archive.SignerFingerprint, SourceSnapshot = archive.Manifest.Inventory.SnapshotBoundary,
            Lineage = archive.SourceTrust == "trusted" ? "trusted:" + options.ExpectedSignerFingerprint : "unverified:" + archive.ManifestSha256 };
        var contextMappings = peer != null
            ? await graph.ResolvePeerContextMappingsAsync(state.Package, peer.VaultId, state.Lineage, ct) : null;
        foreach (var package in new[] { state.Package })
        {
            var error = await graph.ValidateImportAsync(package, options.TargetVaultId, state.Lineage, options.VaultMappings, ct, contextMappings);
            if (error != null) throw new ArchiveOperationException(422, error);
        }
        var sourceVaults = new[] { state.Package }.SelectMany(p => p.Data.Vaults).Select(v => v.Id).ToHashSet();
        if (options.VaultMappings.Keys.Any(id => !sourceVaults.Contains(id))) throw new ArchiveOperationException(400, "A mapped source vault is absent from this archive.");
        var conflicts = new List<ArchiveConflict>();
        foreach (var package in new[] { state.Package })
        {
            var scope = SelfHostedGraphStore.ImportScope(package, options.TargetVaultId, state.Lineage, options.VaultMappings);
            foreach (var (kind, source) in SourceIds(package))
            {
                var mapped = kind == "vault" ? options.TargetVaultId ?? (options.VaultMappings.TryGetValue(source, out var explicitVault) ? explicitVault : SelfHostedGraphStore.DestinationId(actor.TenantId, scope, kind, source))
                    : kind == "context" && contextMappings != null && contextMappings.TryGetValue(source, out var canonical) ? canonical
                    : peer != null && kind != "context" ? source : SelfHostedGraphStore.DestinationId(actor.TenantId, scope, kind, source);
                state.Mapping[$"{kind}:{source:D}"] = mapped.ToString("D"); state.AffectedIds.Add(mapped);
                if (await ExistsAsync(kind, mapped, ct)) conflicts.Add(new() { OwnerKind = kind, SourceId = source, DestinationId = mapped, Effect = options.Strategy });
            }
        }
        foreach (var mapped in options.VaultMappings.Values) state.AffectedIds.Add(mapped);
        if (options.TargetVaultId.HasValue) state.AffectedIds.Add(options.TargetVaultId.Value);
        var omissions = new List<PortableOmission>();
        foreach (var package in new[] { state.Package })
            omissions.AddRange(await UnpreservedPayloadsAsync(package, state, options, ct));
        state.Unpreserved = omissions.Count(o => o.OwnerKind != "release-policy");
        state.Revision = await RevisionAsync(state, ct);
        var journal = new ShArchiveImportJournal { PlanId = planId, TenantId = actor.TenantId, ActorId = peer?.ActorId ?? Guid.Parse(actor.ActorId), ActorNamespace = actor.ActorId, PeerBindingSha256 = peer?.Digest, ArchiveId = archive.Manifest.ArchiveId, ManifestSha256 = archive.ManifestSha256, ImportOptionsJson = JsonSerializer.Serialize(options) };
        db.ShArchiveImportJournals.Add(journal); await db.SaveChangesAsync(ct);
        try
        {
            foreach (var package in new[] { state.Package })
            {
                var stage = new SelfHostedGraphStage { OpenBinary = archive.OpenBinaryAsync, BeforeUpload = async (blob, token) => {
                    journal.StagedObjectIdsJson = JsonSerializer.Serialize(state.Stages.SelectMany(s => s.Objects).Distinct());
                    journal.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(token);
                } };
                state.Stages.Add(stage);
                await graph.StageFilesAsync(package, options.TargetVaultId, state.Lineage, Strategy(options), ct, options.VaultMappings, stage, preserveEntityIds: peer != null, contextMappings: contextMappings);
            }
            journal.Status = "prepared"; journal.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct);
            return new() { State = state, Conflicts = conflicts, EntityMapping = state.Mapping, AdditionalOmissions = omissions, PreservationState = omissions.Count > 0 ? "partial" : "complete" };
        }
        catch
        {
            await CleanupAsync(new() { State = state }, CancellationToken.None); throw;
        }
    }
    private async Task<IReadOnlyDictionary<Guid, Guid>> RequirePlannedPeerContextsAsync(State state, CancellationToken ct, bool requirePersisted = false)
    {
        IReadOnlyDictionary<Guid, Guid> current;
        try { current = await graph.ResolvePeerContextMappingsAsync(state.Package, peer!.VaultId, state.Lineage, ct, requirePersisted); }
        catch (InvalidDataException) { throw new ArchiveOperationException(409, "The original encryption context or key catalog changed after review."); }
        if (current.Any(pair => !state.Mapping.TryGetValue($"context:{pair.Key:D}", out var expected) || !Guid.TryParse(expected, out var planned) || planned != pair.Value))
            throw new ArchiveOperationException(409, "The original encryption context mapping changed after review.");
        return current;
    }
    public async Task ValidateRevisionsAsync(ArchivePreparation prepared, CancellationToken ct)
    {
        var state = RequireState(prepared);
        if (state.Revision != await RevisionAsync(state, ct)) throw new ArchiveOperationException(409, "Destination changed after preview; reupload and review the import.");
    }
    public async Task<ArchiveCommitResult> CommitAsync(ValidatedKnowzArchive archive, ArchiveActor actor, ArchiveImportOptions options,
        ArchivePreparation prepared, ArchiveImportPlan plan, CancellationToken ct)
    {
        var state = RequireState(prepared);
        try
        {
            // Each retry recreates the transaction. A committed journal is checked before
            // source-revision comparison so a lost COMMIT response cannot duplicate activation.
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<ArchiveCommitResult>(async () =>
            {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            await AuthorizeAsync(actor, options, prepared, ct);
            var journal = await db.ShArchiveImportJournals.SingleAsync(j => j.PlanId == state.PlanId, ct);
            if (journal.Status == "committed") return new() { CommitId = journal.CommitId!.Value, CommittedAt = journal.CommitReceiptJson == null ? new DateTimeOffset(journal.UpdatedAt, TimeSpan.Zero) : JsonSerializer.Deserialize<ArchiveCommitReceipt>(journal.CommitReceiptJson)!.CommittedAt, PreservedPayloadCount = journal.PreservedPayloadCount, PreservationState = prepared.PreservationState == "partial" ? "partial" : archive.Manifest.PreservationState };
            await ValidateRevisionsAsync(prepared, ct);
            if (journal.Status != "prepared") throw new ArchiveOperationException(410, "Import stage is unavailable; reupload the archive.");
            var contextMappings = peer != null ? await RequirePlannedPeerContextsAsync(state, ct) : null;
            await graph.VerifyStageAsync(state.Package, state.Stages.Single(), ct);
            await AuthorizeAsync(actor, options, prepared, ct);
            foreach (var package in new[] { state.Package })
            {
                var result = peer != null
                    ? await graph.ImportPeerArchiveAsync(package, peer.VaultId, state.Lineage, Strategy(options), archive.OpenBinaryAsync, state.Stages.Single(), ct, contextMappings)
                    : await graph.ImportAsync(package, options.TargetVaultId, state.Lineage, Strategy(options), archive.OpenBinaryAsync, ct, options.VaultMappings, state.Stages.Single(), plan.PlanId);
                if (!result.Success) throw new ArchiveOperationException(422, result.Error ?? "Archive graph could not be committed.");
            }
            var contexts = new[] { state.Package }.SelectMany(p => p.CryptoContexts ?? []).DistinctBy(c => c.ContextId)
                .Select(c => { var mapped = JsonSerializer.Deserialize<ExportedCryptoContext>(JsonSerializer.Serialize(c))!; mapped.ContextId = Guid.Parse(state.Mapping[$"context:{c.ContextId:D}"]); return mapped; }).ToList();
            var keys = new[] { state.Package }.SelectMany(p => p.CryptoKeyCatalog ?? []).DistinctBy(k => (k.ContextId, k.Generation))
                .Select(k => { var mapped = JsonSerializer.Deserialize<ExportedCryptoKeyIdentity>(JsonSerializer.Serialize(k))!; mapped.ContextId = Guid.Parse(state.Mapping[$"context:{k.ContextId:D}"]); return mapped; }).ToList();
            var publicGraphDigest = ArchivePeerProof.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(new { archive.ManifestSha256, plan.MappingSha256, contexts, keys }));
            await SelfHostedRecoveredCustodyService.RecordImportAnchorsAsync(db, actor.TenantId, plan.PlanId, archive.Manifest.ArchiveId,
                archive.ManifestSha256, plan.MappingSha256, publicGraphDigest, archive.SourceTrust, contexts, keys, ct);
            journal.Status = "committed"; journal.MappingSha256 = plan.MappingSha256; journal.CommitId = Guid.NewGuid();
            journal.PreservedPayloadCount = plan.RequiredPayloadCount - state.Unpreserved; journal.UpdatedAt = DateTime.UtcNow;
            journal.CommitReceiptJson = JsonSerializer.Serialize(new ArchiveCommitReceipt {
                PlanId = plan.PlanId, ArchiveId = plan.ArchiveId, ManifestSha256 = plan.ManifestSha256, SourceSnapshot = plan.SourceSnapshot,
                DestinationTenantId = actor.TenantId, MappingSha256 = plan.MappingSha256, SourceTrust = plan.SourceTrust,
                PreservationState = prepared.PreservationState == "partial" ? "partial" : archive.Manifest.PreservationState,
                CommitId = journal.CommitId.Value, CommittedAt = new DateTimeOffset(journal.UpdatedAt, TimeSpan.Zero),
                RequiredPayloadCount = plan.RequiredPayloadCount, PreservedPayloadCount = journal.PreservedPayloadCount, Omissions = plan.Omissions.ToList() });
            await db.SaveChangesAsync(ct);
            if (peer != null) await RequirePlannedPeerContextsAsync(state, ct, requirePersisted: true);
            await AuthorizeAsync(actor, options, prepared, ct);
            if (transaction != null) await transaction.CommitAsync(ct);
            return new() { CommitId = journal.CommitId.Value, CommittedAt = new DateTimeOffset(journal.UpdatedAt, TimeSpan.Zero), PreservedPayloadCount = journal.PreservedPayloadCount,
                PreservationState = prepared.PreservationState == "partial" ? "partial" : archive.Manifest.PreservationState };
            });
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            await CleanupAsync(prepared, CancellationToken.None);
            if (ex is ArchiveOperationException or OperationCanceledException) throw;
            throw new ArchiveOperationException(422, "Verified archive stage is unavailable or changed; reupload the archive.");
        }
    }

    /// <summary>Only completed activation survives process loss. Pending plans still require a fresh upload.</summary>
    public async Task<ArchiveCommitReceipt?> GetCommittedReceiptAsync(Guid planId, ArchiveActor actor, CancellationToken ct)
    {
        if (actor.TenantId != tenant.TenantId) return null;
        var journal = await db.ShArchiveImportJournals.AsNoTracking().SingleOrDefaultAsync(j => j.PlanId == planId && j.ActorNamespace == actor.ActorId && j.Status == "committed", ct);
        if (journal?.CommitReceiptJson == null || journal.ImportOptionsJson == null || journal.PeerBindingSha256 != peer?.Digest) return null;
        var options = JsonSerializer.Deserialize<ArchiveImportOptions>(journal.ImportOptionsJson) ?? throw new ArchiveOperationException(409, "Stored import authority is unavailable.");
        await AuthorizeAsync(actor, options, null, ct);
        var receipt = JsonSerializer.Deserialize<ArchiveCommitReceipt>(journal.CommitReceiptJson) ?? throw new ArchiveOperationException(409, "Stored receipt is unavailable.");
        if (receipt.PlanId != planId || receipt.ArchiveId != journal.ArchiveId || receipt.DestinationTenantId != actor.TenantId || receipt.ManifestSha256 != journal.ManifestSha256
            || receipt.MappingSha256 != journal.MappingSha256 || receipt.CommitId != journal.CommitId || receipt.PreservedPayloadCount != journal.PreservedPayloadCount)
            throw new ArchiveOperationException(409, "Stored receipt does not match its committed import.");
        return receipt;
    }

    public async Task CleanupAsync(ArchivePreparation prepared, CancellationToken ct)
    {
        var state = RequireState(prepared);
        var journal = await db.ShArchiveImportJournals.SingleOrDefaultAsync(j => j.PlanId == state.PlanId, ct);
        if (journal?.Status == "committed") return;
        foreach (var stage in state.Stages) await graph.CleanupStageAsync(stage, ct);
        if (journal != null) { journal.Status = "cancelled"; journal.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
    }
    private async Task<List<PortableOmission>> UnpreservedPayloadsAsync(PortableExportPackage package, State state, ArchiveImportOptions options, CancellationToken ct)
    {
        var missing = new List<PortableOmission>();
        Guid Map(string kind, Guid id) => Guid.Parse(state.Mapping[$"{kind}:{id:D}"]);
        foreach (var reference in package.PayloadKeyReferences ?? [])
        {
            if (options.Strategy == "overwrite" && reference.OwnerKind != "version") continue;
            var id = Map(reference.OwnerKind, reference.OwnerId);
            object? existing = reference.OwnerKind switch
            {
                "knowledge" => (object?)await db.ShSealedKnowledge.AsNoTracking().SingleOrDefaultAsync(x => x.KnowledgeId == id, ct) ?? await db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct),
                "comment" => await db.ShSealedComments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct),
                "version" => (object?)await db.ShSealedVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? await db.KnowledgeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct),
                "file" => (object?)await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct),
                "attachment" => await db.ShSealedAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct), _ => null
            };
            if (existing == null) continue;
            object source = reference.OwnerKind switch
            {
                "knowledge" => (object?)package.SealedItems?.SingleOrDefault(x => x.Id == reference.OwnerId) ?? package.Data.KnowledgeItems.Single(x => x.Id == reference.OwnerId), "comment" => package.SealedComments!.Single(x => x.Id == reference.OwnerId),
                "version" => (object?)package.SealedVersions?.SingleOrDefault(x => x.Id == reference.OwnerId) ?? package.PlainVersions!.Single(x => x.Id == reference.OwnerId), "file" => (object?)package.SealedFiles?.SingleOrDefault(x => x.Id == reference.OwnerId) ?? package.Data.FileRecords.Single(x => x.Id == reference.OwnerId),
                "attachment" => package.SealedAttachments!.Single(x => x.Id == reference.OwnerId), _ => throw new InvalidDataException("Unknown payload owner.")
            };
            var storedRef = await db.ShPayloadKeyReferences.AsNoTracking().SingleOrDefaultAsync(r => r.OwnerKind == reference.OwnerKind && r.OwnerId == id && r.Role == reference.Role && r.FieldName == reference.FieldName, ct);
            var sameRef = storedRef != null && storedRef.ContextId == Map("context", reference.ContextId) && storedRef.Fingerprint == reference.Fingerprint && storedRef.Generation == reference.Generation
                && storedRef.OriginalBindingId == reference.OriginalBindingId && storedRef.OriginalItemId == reference.OriginalItemId && storedRef.EnvelopeVersion == reference.EnvelopeVersion;
            bool samePayload;
            if (reference.Role == "field") samePayload = Field(source, reference.FieldName) == Field(existing, reference.FieldName);
            else if (reference.Role == "binary") samePayload = Read(source, "BinarySha256") == Read(existing, "BinarySha256");
            else samePayload = new[] { "Ciphertext", "EnvelopeVersion", "EncryptedDek", "EncryptedDekNonce", "EphemeralPublicKey", "ContentNonce", "CryptoBindingId", "EnvelopeMetadata" }.All(name => Read(source, name) == Read(existing, name));
            if (!sameRef || !samePayload) missing.Add(new() { OwnerKind = reference.OwnerKind, OwnerId = reference.OwnerId, Reason = $"{options.Strategy} preserves different destination bytes for {reference.Role}; this incoming payload is not restored." });
        }
        foreach (var version in package.PlainVersions ?? [])
        {
            var id = Map("version", version.Id);
            var existing = await db.KnowledgeVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, ct);
            if (existing == null) continue;
            var stored = existing.PlatformData == null ? null : JsonSerializer.Deserialize<PortablePlainVersionRecord>(existing.PlatformData, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (existing.Content != version.Content || existing.Title != version.Title || existing.VersionNumber != version.VersionNumber
                || stored?.Summary != version.Summary || stored?.Tags != version.Tags || stored?.SnapshotJson != version.SnapshotJson)
                missing.Add(new() { OwnerKind = "version", OwnerId = version.Id, Reason = "Immutable destination history differs; incoming version was not restored." });
        }
        if (options.Strategy != "overwrite")
        {
            missing.AddRange(await SelfHostedInactiveShelf.ConflictOmissionsAsync(db, package, Map, id => Map("vault", id), ct));
            foreach (var item in package.Data.KnowledgeItems)
            {
                var id = Map("knowledge", item.Id); var existing = await db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                if (existing != null && (existing.Content != item.Content || existing.Title != item.Title || existing.Summary != item.Summary))
                    missing.Add(new() { OwnerKind = "knowledge", OwnerId = item.Id, Reason = "Destination conflict preserves different plaintext." });
            }
            foreach (var item in package.Data.Comments)
            {
                var id = Map("comment", item.Id); var existing = await db.Comments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                if (existing != null && existing.Body != item.Body) missing.Add(new() { OwnerKind = "comment", OwnerId = item.Id, Reason = "Destination conflict preserves different plaintext." });
            }
            foreach (var item in package.Data.FileRecords)
            {
                var id = Map("file", item.Id); var existing = await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                var reference = await db.ShFileObjectReferences.AsNoTracking().SingleOrDefaultAsync(x => x.FileRecordId == id, ct);
                if (existing != null && (reference?.Sha256 != item.BinarySha256 || existing.FileName != item.FileName))
                    missing.Add(new() { OwnerKind = "file", OwnerId = item.Id, Reason = "Destination conflict preserves different file bytes or metadata." });
            }
        }
        return missing;
        static string? Read(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value)?.ToString();
        static string? Field(object value, string? name)
        {
            var fields = Read(value, "EncryptedFieldsJson");
            if (value is PortablePlainVersionRecord plain) fields = PlainVersionSnapshot.ReadEncryptedFields(plain.SnapshotJson);
            if (value is Knowz.Core.Entities.KnowledgeVersion stored && stored.PlatformData != null)
                fields = PlainVersionSnapshot.ReadEncryptedFields(JsonSerializer.Deserialize<PortablePlainVersionRecord>(stored.PlatformData, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.SnapshotJson);
            if (fields == null && Read(value, "EnvelopeMetadata") is string snapshot)
            {
                using var doc = JsonDocument.Parse(snapshot);
                if (doc.RootElement.TryGetProperty("encryptedFieldsJson", out var node)) fields = node.GetString();
            }
            if (fields == null || name == null) return null;
            using var parsed = JsonDocument.Parse(fields);
            return parsed.RootElement.TryGetProperty("fields", out var entries) && entries.TryGetProperty(name, out var field) ? field.GetRawText() : null;
        }
    }

    private static State RequireState(ArchivePreparation prepared) => prepared.State as State ?? throw new ArchiveOperationException(409, "Import state is unavailable; reupload the archive.");
    private static ImportConflictStrategy Strategy(ArchiveImportOptions options) => Enum.Parse<ImportConflictStrategy>(options.Strategy, true);
    private async Task<bool> ExistsAsync(string kind, Guid id, CancellationToken ct) => kind switch
    {
        "knowledge" => await db.KnowledgeItems.AnyAsync(x => x.Id == id, ct), "vault" => await db.Vaults.AnyAsync(x => x.Id == id, ct),
        "comment" => await db.ShSealedComments.AnyAsync(x => x.Id == id, ct) || await db.Comments.AnyAsync(x => x.Id == id, ct),
        "file" => await db.ShSealedFiles.AnyAsync(x => x.Id == id, ct) || await db.FileRecords.AnyAsync(x => x.Id == id, ct),
        "context" => await db.ShPortableCryptoContexts.AnyAsync(x => x.Id == id, ct),
        "version" => await db.KnowledgeVersions.AnyAsync(x => x.Id == id, ct) || await db.ShSealedVersions.AnyAsync(x => x.Id == id, ct),
        "relationship" => await db.KnowledgeRelationships.AnyAsync(x => x.Id == id, ct), _ => false
    };
    private async Task<string> RevisionAsync(State state, CancellationToken ct)
    {
        var ids = state.AffectedIds.ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        async Task Add<T>(IQueryable<T> query) where T : class { var rows = await query.AsNoTracking().ToListAsync(ct); foreach (var row in rows.Select(x => JsonSerializer.Serialize(x)).Order(StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes(row)); }
        await Add(db.Set<ShInactiveRecoveryRecord>().Where(r => ids.Contains(r.Id)));
        await Add(db.Vaults.Where(v => ids.Contains(v.Id))); await Add(db.KnowledgeItems.Where(v => ids.Contains(v.Id)));
        await Add(db.KnowledgeVaults.Where(v => ids.Contains(v.KnowledgeId) || ids.Contains(v.VaultId)));
        await Add(db.ShSealedKnowledge.Where(v => ids.Contains(v.KnowledgeId))); await Add(db.ShSealedComments.Where(v => ids.Contains(v.Id) || ids.Contains(v.KnowledgeId)));
        await Add(db.ShSealedVersions.Where(v => ids.Contains(v.KnowledgeId))); await Add(db.ShSealedFiles.Where(v => ids.Contains(v.Id)));
        await Add(db.ShSealedAttachments.Where(v => ids.Contains(v.Id) || v.KnowledgeId.HasValue && ids.Contains(v.KnowledgeId.Value)));
        await Add(db.ShPayloadKeyReferences.Where(v => ids.Contains(v.OwnerId))); await Add(db.ShPortableCryptoContexts.Where(v => ids.Contains(v.Id)));
        await Add(db.ShCryptoKeyIdentities.Where(v => ids.Contains(v.PortableCryptoContextId))); await Add(db.ShVaultCryptoContextLinks.Where(v => ids.Contains(v.DestinationVaultId)));
        await Add(db.Comments.Where(v => ids.Contains(v.Id))); await Add(db.FileRecords.Where(v => ids.Contains(v.Id)));
        await Add(db.KnowledgeVersions.Where(v => ids.Contains(v.Id) || ids.Contains(v.KnowledgeId)));
        await Add(db.KnowledgeRelationships.Where(r => ids.Contains(r.Id) || ids.Contains(r.SourceKnowledgeId) || ids.Contains(r.TargetKnowledgeId)));
        await Add(db.Topics.Where(v => ids.Contains(v.Id))); await Add(db.Tags.Where(v => ids.Contains(v.Id)));
        await Add(db.Persons.Where(v => ids.Contains(v.Id))); await Add(db.Locations.Where(v => ids.Contains(v.Id))); await Add(db.Events.Where(v => ids.Contains(v.Id)));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static IEnumerable<(string Kind, Guid Id)> SourceIds(PortableExportPackage p)
    {
        foreach (var x in p.Data.Vaults) yield return ("vault", x.Id);
        foreach (var x in p.Data.KnowledgeItems.Select(k => k.Id).Concat((p.SealedItems ?? []).Select(k => k.Id)).Concat((p.DependencyBoundItems ?? []).Select(k => k.Id)).Distinct()) yield return ("knowledge", x);
        foreach (var x in p.Data.Comments.Select(k => k.Id).Concat((p.SealedComments ?? []).Select(k => k.Id)).Distinct()) yield return ("comment", x);
        foreach (var x in p.Data.FileRecords.Select(k => k.Id).Concat((p.SealedFiles ?? []).Select(k => k.Id)).Distinct()) yield return ("file", x);
        foreach (var x in p.CryptoContexts ?? []) yield return ("context", x.ContextId);
        foreach (var x in p.SealedVersions ?? []) yield return ("version", x.Id);
        foreach (var x in p.PlainVersions ?? []) yield return ("version", x.Id);
        foreach (var x in (p.DependencyBoundItems ?? []).SelectMany(i => i.Versions)) yield return ("version", x.Id);
        foreach (var x in p.Data.KnowledgeItems.SelectMany(k => k.Relationships ?? []).Concat((p.SealedItems ?? []).SelectMany(k => k.Relationships ?? [])).Where(r => r.Id != Guid.Empty).DistinctBy(r => r.Id)) yield return ("relationship", x.Id);
        foreach (var x in p.SealedAttachments ?? []) yield return ("attachment", x.Id);
        foreach (var x in p.Data.Topics) yield return ("topic", x.Id);
        foreach (var x in p.Data.Tags) yield return ("tag", x.Id);
        foreach (var x in p.Data.Persons) yield return ("person", x.Id);
        foreach (var x in p.Data.Locations) yield return ("location", x.Id);
        foreach (var x in p.Data.Events) yield return ("event", x.Id);
        foreach (var x in p.Data.InboxItems) yield return ("inbox", x.Id);
    }
}
