using System.Text.Json;
using Knowz.Core.Portability;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedArchiveImportAdapter : IArchiveImportCommitRecovery
{
    public async Task<ArchiveCommitReceipt?> TryRecoverCommittedReceiptAsync(ValidatedKnowzArchive archive, ArchiveActor actor,
        ArchiveImportOptions options, ArchivePreparation prepared, ArchiveImportPlan plan, CancellationToken ct)
    {
        var state = RequireState(prepared);
        if (actor.TenantId != tenant.TenantId || state.ActorNamespace != actor.ActorId || state.PlanId != plan.PlanId
            || state.PeerBindingSha256 != peer?.Digest || plan.DestinationTenantId != actor.TenantId)
            throw new ArchiveOperationException(403, "Committed import recovery belongs to another operation or actor.");
        if (state.SourceArchiveId != archive.Manifest.ArchiveId || state.SourceArchiveId != plan.ArchiveId
            || state.ManifestSha256 != archive.ManifestSha256 || state.ManifestSha256 != plan.ManifestSha256
            || state.SourceTrust != archive.SourceTrust || state.SourceTrust != plan.SourceTrust
            || state.SignerFingerprint != archive.SignerFingerprint || state.SourceSnapshot != plan.SourceSnapshot
            || state.SourceSnapshot != archive.Manifest.Inventory.SnapshotBoundary
            || plan.MappingSha256 != ArchivePeerProof.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(plan.Mapping, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            throw new ArchiveOperationException(409, "Committed import recovery does not match the validated source and mapping.");
        var journal = await db.ShArchiveImportJournals.AsNoTracking().SingleOrDefaultAsync(j => j.PlanId == state.PlanId && j.TenantId == actor.TenantId, ct);
        if (journal == null || journal.Status != "committed") return null;
        if (journal.ActorId != (peer?.ActorId ?? Guid.Parse(actor.ActorId)) || journal.ActorNamespace != actor.ActorId
            || journal.PeerBindingSha256 != state.PeerBindingSha256 || journal.ArchiveId != plan.ArchiveId
            || journal.ManifestSha256 != plan.ManifestSha256 || journal.MappingSha256 != plan.MappingSha256
            || journal.ImportOptionsJson != JsonSerializer.Serialize(options) || journal.CommitReceiptJson == null
            || journal.CommitId is null || journal.CommitId == Guid.Empty)
            throw new ArchiveOperationException(409, "The committed journal does not match this exact import operation.");
        ArchiveCommitReceipt receipt;
        try { receipt = JsonSerializer.Deserialize<ArchiveCommitReceipt>(journal.CommitReceiptJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new ArchiveOperationException(409, "The committed import receipt is invalid."); }
        if (receipt.PlanId != plan.PlanId || receipt.ArchiveId != plan.ArchiveId || receipt.DestinationTenantId != actor.TenantId
            || receipt.ManifestSha256 != plan.ManifestSha256 || receipt.MappingSha256 != plan.MappingSha256
            || receipt.SourceSnapshot != plan.SourceSnapshot || receipt.SourceTrust != plan.SourceTrust
            || receipt.CommitId != journal.CommitId || receipt.CommittedAt != new DateTimeOffset(journal.UpdatedAt, TimeSpan.Zero)
            || receipt.PreservationState != plan.PreservationState || receipt.RequiredPayloadCount != plan.RequiredPayloadCount
            || receipt.PreservedPayloadCount != journal.PreservedPayloadCount || receipt.PreservedPayloadCount != plan.RequiredPayloadCount - state.Unpreserved
            || receipt.ClientVerificationState != "not-performed" || JsonSerializer.Serialize(receipt.Omissions) != JsonSerializer.Serialize(plan.Omissions))
            throw new ArchiveOperationException(409, "The committed receipt does not match the exact preservation outcome.");
        await AuthorizeAsync(actor, options, prepared, ct);
        return receipt;
    }
}
