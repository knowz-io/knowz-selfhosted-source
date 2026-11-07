using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Inert recovery evidence. It deliberately has no release scheduler, credentials, or private-key custody.</summary>
public static class SelfHostedInactiveShelf
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string ReadOnlyMessage = "This imported recovery record is suspended and read-only; restore its dependency through an explicitly supported recovery operation.";
    public static IEnumerable<Guid> Owners(PortableExportPackage p) => (p.SuspendedReleasePolicies ?? []).Select(x => x.KnowledgeId).Concat((p.DependencyBoundItems ?? []).Select(x => x.Id)).Distinct();
    public static IEnumerable<PortableKnowledge> Placeholders(PortableExportPackage p) => (p.DependencyBoundItems ?? []).Select(x => {
        var k = x.PublicRecord == null ? new() : Copy<PortableKnowledge>(x.PublicRecord);
        k.Id = x.Id; k.VaultIds = x.VaultIds; k.PrimaryVaultId = x.PrimaryVaultId; k.Title = "Imported encrypted item — source provider required";
        k.Content = ""; k.Summary = null; k.BriefSummary = null; k.IsIndexed = false; k.CreatedAt = x.CreatedAt; k.UpdatedAt = x.UpdatedAt; return k;
    });
    public static async Task<string?> ValidateAsync(SelfHostedDbContext db, Guid tenant, PortableExportPackage source, Func<string, Guid, Guid> map, CancellationToken ct)
    {
        var owners = Owners(source).Select(id => map("knowledge", id)).ToArray();
        if (owners.Length > 0 && db.PortableActorId is not { } actor) return "Inactive recovery requires an authenticated local owner; background and service fallback cannot activate it.";
        if (owners.Length > 0 && await db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(k => owners.Contains(k.Id) && (k.TenantId != tenant || k.IsDeleted || k.CreatedByUserId != db.PortableActorId), ct))
            return "Inactive recovery belongs to another local owner.";
        var all = source.Data.KnowledgeItems.Select(k => k.Id).Concat((source.SealedItems ?? []).Select(k => k.Id)).Concat((source.DependencyBoundItems ?? []).Select(k => k.Id)).Select(id => map("knowledge", id)).ToArray();
        if (await db.KnowledgeItems.IgnoreQueryFilters().AnyAsync(k => all.Contains(k.Id) && k.PortableRecoveryState != null && (k.TenantId != tenant || k.CreatedByUserId != db.PortableActorId || !owners.Contains(k.Id)), ct))
            return "An inactive recovery owner cannot be replaced or activated by an ordinary import.";
        return null;
    }
    public static async Task PreserveAsync(SelfHostedDbContext db, Guid tenant, PortableExportPackage source, Func<string, Guid, Guid> map, Func<Guid, Guid> vault,
        ImportConflictStrategy strategy, Guid? planId, CancellationToken ct)
    {
        foreach (var sourceId in Owners(source))
        {
            var id = map("knowledge", sourceId); var actor = db.PortableActorId ?? throw new UnauthorizedAccessException("A local owner is required.");
            var item = await db.KnowledgeItems.IgnoreQueryFilters().SingleAsync(k => k.Id == id && k.TenantId == tenant, ct);
            var row = await db.Set<ShInactiveRecoveryRecord>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row != null && (row.TenantId != tenant || row.OwnerUserId != actor)) throw new UnauthorizedAccessException("Recovery belongs to another owner.");
            if (row != null && strategy != ImportConflictStrategy.Overwrite) continue;
            if (row == null) { row = new() { Id = id, TenantId = tenant, OwnerUserId = actor }; db.Add(row); }
            row.ImportPlanId = planId; row.Revision++;
            var (policies, dependency) = Remap(source, sourceId, id, map, vault);
            row.PoliciesJson = JsonSerializer.Serialize(policies, Json);
            row.DependencyJson = dependency == null ? null : JsonSerializer.Serialize(dependency, Json);
            row.RecoveryState = dependency == null ? "suspended-release" : "source-provider-required";
            item.PortableRecoveryState = row.RecoveryState; item.CreatedByUserId = actor; item.IsIndexed = false; item.IndexedAt = null;
            if (dependency != null) { item.Content = ""; item.Summary = null; item.BriefSummary = null; }
            db.ContentChunks.RemoveRange(await db.ContentChunks.IgnoreQueryFilters().Where(c => c.TenantId == tenant && c.KnowledgeId == id).ToListAsync(ct));
        }
    }
    private static (List<PortableSuspendedReleasePolicy> Policies, PortableDependencyBoundKnowledge? Dependency) Remap(
        PortableExportPackage source, Guid sourceId, Guid id, Func<string, Guid, Guid> map, Func<Guid, Guid> vault)
    {
            var policies = (source.SuspendedReleasePolicies ?? []).Where(p => p.KnowledgeId == sourceId).Select(Copy<PortableSuspendedReleasePolicy>).ToList();
            foreach (var policy in policies) policy.KnowledgeId = id;
            var original = source.DependencyBoundItems?.SingleOrDefault(p => p.Id == sourceId);
            var dependency = original == null ? null : Copy<PortableDependencyBoundKnowledge>(original);
            if (dependency != null)
            {
                dependency.Id = id; dependency.VaultIds = dependency.VaultIds.Select(vault).Distinct().ToList(); dependency.PrimaryVaultId = dependency.PrimaryVaultId.HasValue ? vault(dependency.PrimaryVaultId.Value) : null;
                foreach (var version in dependency.Versions) { version.Id = map("version", version.Id); version.KnowledgeId = id; }
                if (dependency.PublicRecord != null)
                {
                    dependency.PublicRecord = Copy<PortableKnowledge>(source.Data.KnowledgeItems.SingleOrDefault(k => k.Id == sourceId) ?? original!.PublicRecord!);
                    SelfHostedGraphStore.RemapData(new PortableExportData { KnowledgeItems = [dependency.PublicRecord] }, map, vault);
                    foreach (var relation in dependency.PublicRecord.Relationships ?? []) { relation.Id = map("relationship", relation.Id); relation.SourceKnowledgeId = relation.SourceKnowledgeId.HasValue ? map("knowledge", relation.SourceKnowledgeId.Value) : null; relation.TargetKnowledgeId = relation.TargetKnowledgeId.HasValue ? map("knowledge", relation.TargetKnowledgeId.Value) : null; }
                }
            }
        return (policies, dependency);
    }
    internal static async Task<List<PortableOmission>> ConflictOmissionsAsync(SelfHostedDbContext db, PortableExportPackage source,
        Func<string, Guid, Guid> map, Func<Guid, Guid> vault, CancellationToken ct)
    {
        var missing = new List<PortableOmission>();
        foreach (var sourceId in Owners(source))
        {
            var id = map("knowledge", sourceId); var row = await db.Set<ShInactiveRecoveryRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
            if (row == null) continue;
            var (policies, dependency) = Remap(source, sourceId, id, map, vault);
            if (!Equal(JsonSerializer.SerializeToNode(policies, Json), System.Text.Json.Nodes.JsonNode.Parse(row.PoliciesJson)))
                missing.Add(new() { OwnerKind = "release-policy", OwnerId = sourceId, Reason = "Destination conflict preserves different suspended release policy evidence." });
            if (dependency == null) continue;
            var current = row.DependencyJson == null ? null : JsonSerializer.Deserialize<PortableDependencyBoundKnowledge>(row.DependencyJson, Json);
            foreach (var version in dependency.Versions)
                if (current == null || !Equal(JsonSerializer.SerializeToNode(version, Json), JsonSerializer.SerializeToNode(current.Versions.SingleOrDefault(v => v.Id == version.Id), Json)))
                    missing.Add(new() { OwnerKind = "version", OwnerId = source.DependencyBoundItems!.Single(i => i.Id == sourceId).Versions.Single(v => map("version", v.Id) == version.Id).Id, Reason = "Destination conflict preserves different provider-dependent history." });
            dependency.Versions = []; if (current != null) current.Versions = [];
            if (!Equal(JsonSerializer.SerializeToNode(dependency, Json), JsonSerializer.SerializeToNode(current, Json)))
                missing.Add(new() { OwnerKind = "knowledge", OwnerId = sourceId, Reason = "Destination conflict preserves different provider-dependent encrypted body or metadata." });
        }
        return missing;
        static bool Equal(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b) => System.Text.Json.Nodes.JsonNode.DeepEquals(a, b);
    }
    public static async Task<ShPortableRecoveryState> ReadAsync(SelfHostedDbContext db, Guid tenant, Guid actor, Guid vault, Guid item, CancellationToken ct)
    {
        if (actor == Guid.Empty || db.PortableActorId != actor) throw new UnauthorizedAccessException("Recovery record is unavailable.");
        var row = await db.Set<ShInactiveRecoveryRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.Id == item && r.OwnerUserId == actor, ct)
            ?? throw new UnauthorizedAccessException("Recovery record is unavailable.");
        if (!await db.KnowledgeItems.AnyAsync(k => k.Id == item && k.CreatedByUserId == actor && k.KnowledgeVaults.Any(v => v.VaultId == vault && v.TenantId == tenant), ct))
            throw new UnauthorizedAccessException("Recovery record is unavailable in this vault.");
        return new(item, row.RecoveryState, row.Revision, JsonSerializer.Deserialize<List<PortableSuspendedReleasePolicy>>(row.PoliciesJson, Json)!,
            row.DependencyJson == null ? null : JsonSerializer.Deserialize<PortableDependencyBoundKnowledge>(row.DependencyJson, Json));
    }
    public static Task<bool> IsInactiveAsync(SelfHostedDbContext db, Guid item, CancellationToken ct) => db.KnowledgeItems.AnyAsync(k => k.Id == item && k.PortableRecoveryState != null, ct);
    public static async Task<bool> IsOwnerInactiveAsync(SelfHostedDbContext db, Guid? item, Guid? comment, CancellationToken ct)
    {
        if (item.HasValue && await IsInactiveAsync(db, item.Value, ct)) return true;
        if (!comment.HasValue) return false;
        var owner = await db.Comments.Where(c => c.Id == comment).Select(c => (Guid?)c.KnowledgeId).FirstOrDefaultAsync(ct)
            ?? await db.ShSealedComments.Where(c => c.Id == comment).Select(c => (Guid?)c.KnowledgeId).FirstOrDefaultAsync(ct);
        return owner.HasValue && await IsInactiveAsync(db, owner.Value, ct);
    }
    public static async Task<bool> IsFileInactiveAsync(SelfHostedDbContext db, Guid id, CancellationToken ct)
    {
        var root = await db.ShSealedFiles.Where(f => f.Id == id).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).FirstOrDefaultAsync(ct)
            ?? await db.FileRecords.Where(f => f.Id == id).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).FirstOrDefaultAsync(ct) ?? id;
        var ids = (await db.ShSealedFiles.Where(f => f.Id == root || f.RootFileRecordId == root).Select(f => f.Id).ToListAsync(ct))
            .Concat(await db.FileRecords.Where(f => f.Id == root || f.RootFileRecordId == root).Select(f => f.Id).ToListAsync(ct)).ToArray();
        foreach (var owner in await db.ShSealedFiles.Where(f => ids.Contains(f.Id)).Select(f => f.KnowledgeId).ToListAsync(ct)) if (await IsOwnerInactiveAsync(db, owner, null, ct)) return true;
        foreach (var link in await db.ShSealedAttachments.Where(a => ids.Contains(a.FileRecordId)).Select(a => new { a.KnowledgeId, a.CommentId }).ToListAsync(ct)) if (await IsOwnerInactiveAsync(db, link.KnowledgeId, link.CommentId, ct)) return true;
        foreach (var link in await db.FileAttachments.Where(a => ids.Contains(a.FileRecordId)).Select(a => new { a.KnowledgeId, a.CommentId }).ToListAsync(ct)) if (await IsOwnerInactiveAsync(db, link.KnowledgeId, link.CommentId, ct)) return true;
        return false;
    }
    public static async Task RequireMutableAsync(SelfHostedDbContext db, Guid id, CancellationToken ct)
    {
        if (await db.KnowledgeItems.AnyAsync(k => k.Id == id && k.PortableRecoveryState != null, ct)) throw new InvalidOperationException(ReadOnlyMessage);
    }
    public static async Task AppendExportAsync(SelfHostedDbContext db, PortableExportPackage p, CancellationToken ct)
    {
        var ids = p.Data.KnowledgeItems.Select(k => k.Id).Concat((p.SealedItems ?? []).Select(k => k.Id)).ToArray();
        foreach (var row in await db.Set<ShInactiveRecoveryRecord>().AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct))
        {
            (p.SuspendedReleasePolicies ??= []).AddRange(JsonSerializer.Deserialize<List<PortableSuspendedReleasePolicy>>(row.PoliciesJson, Json)!);
            if (row.DependencyJson != null)
            {
                (p.DependencyBoundItems ??= []).Add(JsonSerializer.Deserialize<PortableDependencyBoundKnowledge>(row.DependencyJson, Json)!);
                p.Data.KnowledgeItems.RemoveAll(k => k.Id == row.Id); p.SealedItems?.RemoveAll(k => k.Id == row.Id);
            }
        }
        if (Owners(p).Any() && !(p.RequiredCapabilities ??= []).Contains(SuspendedReleaseProfile.Capability)) p.RequiredCapabilities.Add(SuspendedReleaseProfile.Capability);
    }
    private static T Copy<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
public sealed record ShPortableRecoveryState(Guid KnowledgeId, string RecoveryState,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Revision,
    List<PortableSuspendedReleasePolicy> SuspendedReleasePolicies, PortableDependencyBoundKnowledge? DependencyBoundItem)
{
    public bool CanEdit => false;
    public bool CanProcessWithAi => false;
    public string ActivationState => "suspended";
}
