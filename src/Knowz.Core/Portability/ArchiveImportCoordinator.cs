using System.Collections.Concurrent;
using System.Text.Json;

namespace Knowz.Core.Portability;

public sealed class ArchiveOperationException(int statusCode, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed record ArchiveActor(Guid TenantId, string ActorId);
public sealed class ArchiveImportOptions
{
    public string Strategy { get; init; } = "skip";
    public Guid? TargetVaultId { get; init; }
    public Dictionary<Guid, Guid> VaultMappings { get; init; } = [];
    public bool AllowPartial { get; init; }
    public string? ExpectedSignerFingerprint { get; init; }
    public string IdempotencyKey { get; init; } = "";
    public ArchiveSourceReceipt? SourceReceipt { get; init; }
}
public sealed class ArchiveConflict
{
    public string OwnerKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid? DestinationId { get; set; }
    public string Effect { get; set; } = "";
}
public sealed class ArchiveImportPlan
{
    public Guid PlanId { get; set; }
    public Guid ArchiveId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceSnapshot { get; set; } = "";
    public Guid DestinationTenantId { get; set; }
    public string MappingSha256 { get; set; } = "";
    public string SourceTrust { get; set; } = "unverified";
    public string PreservationState { get; set; } = "blocked";
    public DateTimeOffset ExpiresAt { get; set; }
    public long RequiredPayloadCount { get; set; }
    public List<PortableOmission> Omissions { get; set; } = [];
    public List<ArchiveConflict> Conflicts { get; set; } = [];
    public Dictionary<string, string> Mapping { get; set; } = [];
    public string IdempotencyKey { get; set; } = "";
}
public sealed class ArchiveCommitReceipt
{
    public Guid PlanId { get; set; }
    public Guid ArchiveId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceSnapshot { get; set; } = "";
    public Guid DestinationTenantId { get; set; }
    public string MappingSha256 { get; set; } = "";
    public string SourceTrust { get; set; } = "unverified";
    public string PreservationState { get; set; } = "blocked";
    public Guid CommitId { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public long RequiredPayloadCount { get; set; }
    public long PreservedPayloadCount { get; set; }
    public string ClientVerificationState { get; set; } = "not-performed";
    public List<PortableOmission> Omissions { get; set; } = [];
}
public sealed class ArchivePreparation
{
    public Dictionary<Guid, Guid> Mapping { get; init; } = [];
    public List<ArchiveConflict> Conflicts { get; init; } = [];
    public Dictionary<string, string> EntityMapping { get; init; } = [];
    public string PreservationState { get; init; } = "complete";
    public List<PortableOmission> AdditionalOmissions { get; init; } = [];
    public object? State { get; init; }
}
public sealed class ArchiveCommitResult
{
    public Guid CommitId { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public long PreservedPayloadCount { get; init; }
    public string PreservationState { get; init; } = "complete";
}

/// <summary>
/// Edition adapters enforce live ACLs and revisions and own transactional graph/object journals.
/// Prepare must clean its own stages if it throws before returning a preparation.
/// </summary>
public interface IArchiveImportAdapter
{
    Task AuthorizeAsync(ArchiveActor actor, ArchiveImportOptions options, ArchivePreparation? prepared, CancellationToken ct);
    Task<ArchivePreparation> PrepareAsync(ValidatedKnowzArchive archive, ArchiveActor actor, ArchiveImportOptions options, Guid planId, CancellationToken ct);
    Task ValidateRevisionsAsync(ArchivePreparation prepared, CancellationToken ct);
    Task<ArchiveCommitResult> CommitAsync(ValidatedKnowzArchive archive, ArchiveActor actor, ArchiveImportOptions options,
        ArchivePreparation prepared, ArchiveImportPlan plan, CancellationToken ct);
    Task CleanupAsync(ArchivePreparation prepared, CancellationToken ct);
}

/// <summary>Optional durable receipt recovery after a committed transaction's response was lost.</summary>
public interface IArchiveImportCommitRecovery
{
    Task<ArchiveCommitReceipt?> TryRecoverCommittedReceiptAsync(ValidatedKnowzArchive archive, ArchiveActor actor, ArchiveImportOptions options,
        ArchivePreparation prepared, ArchiveImportPlan plan, CancellationToken ct);
}

/// <summary>Common actor/digest/revision/idempotency protocol, with ephemeral protected plan storage.</summary>
public sealed class ArchiveImportCoordinator : IDisposable
{
    private sealed class Pending
    {
        public required ArchiveActor Actor;
        public required ArchiveImportOptions Options;
        public required string OptionsDigest;
        public required string IndexKey;
        public required ProtectedSpoolStream Spool;
        public required ValidatedKnowzArchive Archive;
        public required ArchivePreparation Prepared;
        public required ArchiveImportPlan Plan;
        public ArchiveCommitReceipt? Receipt;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
    private readonly ConcurrentDictionary<Guid, Pending> _plans = new();
    private readonly ConcurrentDictionary<string, Guid> _index = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;
    private readonly int _maxPlans;
    private bool _disposed;

    public ArchiveImportCoordinator(TimeProvider? clock = null, TimeSpan? lifetime = null, int maxPlans = 32)
    {
        _clock = clock ?? TimeProvider.System; _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
        if (_lifetime <= TimeSpan.Zero || _lifetime > TimeSpan.FromHours(1) || maxPlans is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        _maxPlans = maxPlans;
    }

    public async Task<ArchiveImportPlan> CreatePlanAsync(Stream input, ArchiveActor actor, ArchiveImportOptions options,
        IArchiveImportAdapter adapter, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (actor.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(actor.ActorId)) throw new ArchiveOperationException(403, "An authenticated destination actor is required.");
        var normalized = Normalize(options);
        await adapter.AuthorizeAsync(actor, normalized, null, ct);
        await _creation.WaitAsync(ct);
        ProtectedSpoolStream? spool = null; ValidatedKnowzArchive? archive = null; ArchivePreparation? prepared = null;
        try
        {
            await SweepExpiredAsync(actor, adapter, ct);
            var retryIndex = $"{actor.TenantId:D}|{actor.ActorId}|{normalized.IdempotencyKey}";
            if (!_index.TryGetValue(retryIndex, out var retryPlan) || !_plans.ContainsKey(retryPlan))
                await TrimCompletedReceiptsAsync(ct);
            if (_plans.Count >= _maxPlans && (!_index.TryGetValue(retryIndex, out var indexedPlan) || !_plans.ContainsKey(indexedPlan)))
                throw new ArchiveOperationException(429, "Too many active import plans; cancel or finish an existing plan.");
            spool = await ProtectedSpoolStream.CreateAsync(input, ct: ct);
            archive = await KnowzArchiveReader.OpenAsync(spool, new()
            {
                AllowPartial = normalized.AllowPartial, ExpectedSignerFingerprint = normalized.ExpectedSignerFingerprint, LeaveOpen = true,
            }, ct);
            if (normalized.SourceReceipt is { } receipt && (receipt.ArchiveId != archive.Manifest.ArchiveId
                || receipt.ManifestSha256 != archive.ManifestSha256 || receipt.SourceSnapshot != archive.Manifest.Inventory.SnapshotBoundary
                || receipt.SignerFingerprint != archive.SignerFingerprint || receipt.SignerPublicKey != archive.SignerPublicKey
                || receipt.EntryCount != archive.Manifest.Inventory.Parts.Count
                || receipt.PayloadCount != archive.Manifest.Inventory.PayloadCounts["required"]))
                throw new ArchiveOperationException(400, "Source receipt does not match the authenticated archive.");
            var key = $"{actor.TenantId:D}|{actor.ActorId}|{normalized.IdempotencyKey}";
            var optionsDigest = OptionsDigest(normalized);
            if (_index.TryGetValue(key, out var previousId) && _plans.TryGetValue(previousId, out var previous))
            {
                if (previous.Archive.ManifestSha256 != archive.ManifestSha256 || previous.OptionsDigest != optionsDigest)
                    throw new ArchiveOperationException(409, "Idempotency key is already bound to a different archive or import choice.");
                await adapter.AuthorizeAsync(actor, normalized, previous.Prepared, ct);
                return Clone(previous.Plan);
            }
            if (_plans.Count >= _maxPlans) throw new ArchiveOperationException(429, "Too many active import plans; cancel or finish an existing plan.");
            var planId = Guid.NewGuid();
            prepared = await adapter.PrepareAsync(archive, actor, normalized, planId, ct);
            await adapter.AuthorizeAsync(actor, normalized, prepared, ct);
            await adapter.ValidateRevisionsAsync(prepared, ct);
            var mapping = prepared.Mapping.OrderBy(p => p.Key).ToDictionary(p => "vault:" + p.Key.ToString("D"), p => p.Value.ToString("D"));
            foreach (var item in prepared.EntityMapping.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 240 || !Guid.TryParse(item.Value, out _)
                    || mapping.TryGetValue(item.Key, out var existing) && existing != item.Value)
                    throw new ArchiveOperationException(409, "Conflicting typed destination mappings.");
                mapping[item.Key] = item.Value;
            }
            mapping = mapping.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
            if (prepared.PreservationState is not ("complete" or "partial" or "blocked"))
                throw new ArchiveOperationException(409, "Invalid planned preservation outcome.");
            var state = prepared.PreservationState == "blocked" ? "blocked"
                : archive.Manifest.PreservationState == "partial" || prepared.PreservationState == "partial" || prepared.AdditionalOmissions.Count > 0 ? "partial" : "complete";
            var plan = new ArchiveImportPlan
            {
                PlanId = planId, ArchiveId = archive.Manifest.ArchiveId, ManifestSha256 = archive.ManifestSha256,
                SourceSnapshot = archive.Manifest.Inventory.SnapshotBoundary, DestinationTenantId = actor.TenantId,
                MappingSha256 = ArchiveJson.Digest(JsonSerializer.SerializeToUtf8Bytes(mapping, ArchiveJson.Options)), Mapping = mapping,
                SourceTrust = archive.SourceTrust, PreservationState = state,
                RequiredPayloadCount = archive.Manifest.Inventory.PayloadCounts["required"],
                Omissions = archive.Manifest.Inventory.Omissions.Concat(prepared.AdditionalOmissions).ToList(), Conflicts = prepared.Conflicts.ToList(),
                ExpiresAt = _clock.GetUtcNow() + _lifetime, IdempotencyKey = normalized.IdempotencyKey,
            };
            var pending = new Pending { Actor = actor, Options = normalized, OptionsDigest = optionsDigest, IndexKey = key,
                Spool = spool, Archive = archive, Prepared = prepared, Plan = plan };
            if (!_plans.TryAdd(planId, pending) || !_index.TryAdd(key, planId)) throw new ArchiveOperationException(409, "Import plan creation conflicted.");
            spool = null; archive = null; prepared = null;
            return Clone(plan);
        }
        catch
        {
            if (prepared != null) await adapter.CleanupAsync(prepared, CancellationToken.None);
            throw;
        }
        finally { archive?.Dispose(); spool?.Dispose(); _creation.Release(); }
    }

    public async Task<ArchiveCommitReceipt> CommitAsync(Guid planId, ArchiveActor actor, IArchiveImportAdapter adapter, CancellationToken ct = default)
    {
        var pending = Find(planId, actor); await pending.Gate.WaitAsync(ct);
        try
        {
            if (!_plans.TryGetValue(planId, out var live) || !ReferenceEquals(live, pending))
                throw new ArchiveOperationException(pending.Receipt != null ? 404 : 410,
                    pending.Receipt != null ? "The completed receipt left the temporary cache; recover its durable receipt."
                        : "Import plan expired or was cancelled; reupload the archive.");
            await adapter.AuthorizeAsync(actor, pending.Options, pending.Prepared, ct);
            if (await TryRecoverReceiptAsync(pending, actor, adapter, ct)) return Clone(pending.Receipt!);
            if (pending.Plan.ExpiresAt <= _clock.GetUtcNow())
            {
                await RemoveAsync(pending, adapter, ct);
                throw new ArchiveOperationException(410, "Import plan expired; reupload and review the archive.");
            }
            if (pending.Plan.PreservationState == "blocked" || pending.Plan.PreservationState == "partial" && !pending.Options.AllowPartial)
                throw new ArchiveOperationException(409, "Import has unresolved or skipped dependencies; change the conflict choice or explicitly allow a partial result.");
            await adapter.ValidateRevisionsAsync(pending.Prepared, ct);
            var result = await adapter.CommitAsync(pending.Archive, actor, pending.Options, pending.Prepared, Clone(pending.Plan), ct);
            if (result.CommitId == Guid.Empty || result.PreservationState is not ("complete" or "partial" or "blocked")
                || result.PreservedPayloadCount < 0 || result.PreservedPayloadCount > pending.Plan.RequiredPayloadCount
                || result.PreservationState == "complete" && (pending.Plan.PreservationState != "complete" || result.PreservedPayloadCount != pending.Plan.RequiredPayloadCount))
                throw new ArchiveOperationException(409, "Destination did not provide a complete, consistent preservation receipt.");
            pending.Receipt = new()
            {
                PlanId = pending.Plan.PlanId, ArchiveId = pending.Plan.ArchiveId, ManifestSha256 = pending.Plan.ManifestSha256,
                SourceSnapshot = pending.Plan.SourceSnapshot, DestinationTenantId = actor.TenantId,
                MappingSha256 = pending.Plan.MappingSha256, SourceTrust = pending.Plan.SourceTrust,
                PreservationState = result.PreservationState, CommitId = result.CommitId, CommittedAt = result.CommittedAt ?? _clock.GetUtcNow(),
                RequiredPayloadCount = pending.Plan.RequiredPayloadCount, PreservedPayloadCount = result.PreservedPayloadCount,
                ClientVerificationState = "not-performed", Omissions = pending.Plan.Omissions.ToList(),
            };
            pending.Archive.Dispose(); pending.Spool.Dispose();
            return Clone(pending.Receipt);
        }
        finally { pending.Gate.Release(); }
    }

    private async Task<bool> TryRecoverReceiptAsync(Pending pending, ArchiveActor actor, IArchiveImportAdapter adapter, CancellationToken ct)
    {
        if (pending.Receipt != null) return true;
        if (adapter is not IArchiveImportCommitRecovery recovery) return false;
        var receipt = await recovery.TryRecoverCommittedReceiptAsync(pending.Archive, actor, pending.Options, pending.Prepared, Clone(pending.Plan), ct);
        if (receipt == null) return false;
        if (receipt.PlanId != pending.Plan.PlanId || receipt.ArchiveId != pending.Plan.ArchiveId
            || receipt.DestinationTenantId != actor.TenantId || receipt.ManifestSha256 != pending.Plan.ManifestSha256
            || receipt.MappingSha256 != pending.Plan.MappingSha256 || receipt.SourceSnapshot != pending.Plan.SourceSnapshot
            || receipt.SourceTrust != pending.Plan.SourceTrust || receipt.RequiredPayloadCount != pending.Plan.RequiredPayloadCount
            || receipt.CommitId == Guid.Empty || receipt.PreservationState is not ("complete" or "partial" or "blocked")
            || receipt.PreservedPayloadCount < 0 || receipt.PreservedPayloadCount > pending.Plan.RequiredPayloadCount
            || receipt.PreservationState == "complete" && (pending.Plan.PreservationState != "complete" || receipt.PreservedPayloadCount != pending.Plan.RequiredPayloadCount)
            || receipt.ClientVerificationState != "not-performed"
            || JsonSerializer.Serialize(receipt.Omissions, ArchiveJson.Options) != JsonSerializer.Serialize(pending.Plan.Omissions, ArchiveJson.Options))
            throw new ArchiveOperationException(409, "Recovered receipt does not match this exact import plan.");
        pending.Receipt = Clone(receipt);
        pending.Archive.Dispose(); pending.Spool.Dispose();
        return true;
    }

    public async Task CancelAsync(Guid planId, ArchiveActor actor, IArchiveImportAdapter adapter, CancellationToken ct = default)
    {
        var pending = Find(planId, actor); await pending.Gate.WaitAsync(ct);
        try
        {
            if (!_plans.TryGetValue(planId, out var live) || !ReferenceEquals(live, pending))
                throw new ArchiveOperationException(410, "Import plan expired or was cancelled; reupload the archive.");
            await adapter.AuthorizeAsync(actor, pending.Options, pending.Prepared, ct);
            if (await TryRecoverReceiptAsync(pending, actor, adapter, ct)) throw new ArchiveOperationException(409, "A committed import cannot be cancelled.");
            await RemoveAsync(pending, adapter, ct);
        }
        finally { pending.Gate.Release(); }
    }

    /// <summary>
    /// A host janitor supplies a fresh owner-scoped cleanup delegate. It must delete only the
    /// preparation's journal-owned objects, never borrow an unrelated request's tenant context.
    /// </summary>
    public async Task ExpirePlansAsync(Func<ArchiveActor, ArchivePreparation, CancellationToken, Task> cleanup, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _creation.WaitAsync(ct);
        try
        {
            foreach (var pending in _plans.Values.Where(p => p.Plan.ExpiresAt <= _clock.GetUtcNow()))
            {
                await pending.Gate.WaitAsync(ct);
                try
                {
                    if (!_plans.TryGetValue(pending.Plan.PlanId, out var live) || !ReferenceEquals(live, pending)) continue;
                    if (pending.Receipt == null) await cleanup(pending.Actor, pending.Prepared, ct);
                    _plans.TryRemove(pending.Plan.PlanId, out _); _index.TryRemove(pending.IndexKey, out _);
                    pending.Archive.Dispose(); pending.Spool.Dispose();
                }
                finally { pending.Gate.Release(); }
            }
        }
        finally { _creation.Release(); }
    }

    private Pending Find(Guid id, ArchiveActor actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_plans.TryGetValue(id, out var result) || result.Actor != actor)
            throw new ArchiveOperationException(404, "Import plan is unavailable; restart requires archive reupload.");
        return result;
    }
    private async Task TrimCompletedReceiptsAsync(CancellationToken ct)
    {
        // The temporary cache shares the bounded plan budget. Durable edition journals
        // retain completed receipts; cached successes must not exhaust active import slots.
        foreach (var pending in _plans.Values.Where(p => p.Receipt != null)
            .OrderBy(p => p.Receipt!.CommittedAt).ThenBy(p => p.Plan.PlanId))
        {
            if (_plans.Count < _maxPlans) break;
            await pending.Gate.WaitAsync(ct);
            try
            {
                if (pending.Receipt == null || !_plans.TryGetValue(pending.Plan.PlanId, out var live) || !ReferenceEquals(live, pending)) continue;
                _plans.TryRemove(pending.Plan.PlanId, out _); _index.TryRemove(pending.IndexKey, out _);
                pending.Archive.Dispose(); pending.Spool.Dispose();
            }
            finally { pending.Gate.Release(); }
        }
    }
    private async Task SweepExpiredAsync(ArchiveActor actor, IArchiveImportAdapter adapter, CancellationToken ct)
    {
        foreach (var pending in _plans.Values.Where(p => p.Actor == actor && p.Plan.ExpiresAt <= _clock.GetUtcNow()))
        {
            await pending.Gate.WaitAsync(ct);
            try { await RemoveAsync(pending, adapter, ct); }
            finally { pending.Gate.Release(); }
        }
    }
    private async Task RemoveAsync(Pending pending, IArchiveImportAdapter adapter, CancellationToken ct)
    {
        if (pending.Receipt == null) await adapter.CleanupAsync(pending.Prepared, ct);
        _plans.TryRemove(pending.Plan.PlanId, out _); _index.TryRemove(pending.IndexKey, out _);
        pending.Archive.Dispose(); pending.Spool.Dispose();
    }
    private static ArchiveImportOptions Normalize(ArchiveImportOptions options)
    {
        if (options.Strategy is not ("skip" or "merge" or "overwrite") || options.IdempotencyKey.Length is < 1 or > 200
            || options.IdempotencyKey.Any(char.IsControl) || options.TargetVaultId == Guid.Empty
            || options.VaultMappings.Count > 10000 || options.VaultMappings.Any(p => p.Key == Guid.Empty || p.Value == Guid.Empty)
            || options.ExpectedSignerFingerprint != null && !EncryptedGraphProfile.IsSha256(options.ExpectedSignerFingerprint))
            throw new ArchiveOperationException(400, "Invalid import options.");
        return new() { Strategy = options.Strategy, TargetVaultId = options.TargetVaultId,
            VaultMappings = options.VaultMappings.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
            AllowPartial = options.AllowPartial, ExpectedSignerFingerprint = options.ExpectedSignerFingerprint, IdempotencyKey = options.IdempotencyKey, SourceReceipt = options.SourceReceipt == null ? null : Clone(options.SourceReceipt) };
    }
    private static string OptionsDigest(ArchiveImportOptions options) => ArchiveJson.Digest(JsonSerializer.SerializeToUtf8Bytes(options, ArchiveJson.Options));
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, ArchiveJson.Options), ArchiveJson.Options)!;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var p in _plans.Values) { p.Archive.Dispose(); p.Spool.Dispose(); }
        _plans.Clear(); _index.Clear(); _creation.Dispose();
    }
}
