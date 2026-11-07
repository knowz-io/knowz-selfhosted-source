using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public sealed partial class SelfHostedGraphStore
{
    private static string? ValidateHistory(PortableExportPackage source)
    {
        foreach (var version in source.PlainVersions ?? [])
            if (version.VersionNumber is < 1 or > int.MaxValue || version.StorageStrategy != "full" || version.DeltaContent != null)
                return "Plain history must contain materialized full snapshots with supported version numbers.";
        return null;
    }

    private async Task RestorePlainHistoryAndRelationshipsAsync(PortableExportPackage source,
        Func<string, Guid, Guid> map, ImportConflictStrategy strategy, CancellationToken ct)
    {
        foreach (var version in source.PlainVersions ?? [])
        {
            var portable = Clone<PortablePlainVersionRecord>(version);
            portable.BaseVersionId = version.BaseVersionId.HasValue ? map("version", version.BaseVersionId.Value) : null;
            portable.PreviousVersionId = version.PreviousVersionId.HasValue ? map("version", version.PreviousVersionId.Value) : null;
            var row = new KnowledgeVersion { Id = map("version", version.Id), TenantId = tenant.TenantId,
                KnowledgeId = map("knowledge", version.KnowledgeId), VersionNumber = checked((int)version.VersionNumber),
                Title = version.Title, Content = version.Content, CreatedAt = version.CreatedAt, UpdatedAt = version.UpdatedAt,
                PlatformData = JsonSerializer.Serialize(portable, Json) };
            // History is immutable; conflicts are surfaced by the archive preview.
            await PutAsync(db.KnowledgeVersions, row.Id, row, false, ct);
        }
        var relationships = source.Data.KnowledgeItems.SelectMany(k => k.Relationships ?? [])
            .Concat((source.SealedItems ?? []).SelectMany(k => k.Relationships ?? []))
            .Where(r => r.Id != Guid.Empty && r.SourceKnowledgeId.HasValue && r.TargetKnowledgeId.HasValue).DistinctBy(r => r.Id);
        foreach (var relationship in relationships)
        {
            var row = new KnowledgeRelationship { Id = map("relationship", relationship.Id), TenantId = tenant.TenantId,
                SourceKnowledgeId = map("knowledge", relationship.SourceKnowledgeId!.Value), TargetKnowledgeId = map("knowledge", relationship.TargetKnowledgeId!.Value),
                RelationshipType = relationship.RelationshipType, Confidence = relationship.Confidence ?? 1,
                Metadata = relationship.PortableMetadataJson, CreatedAt = relationship.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = relationship.UpdatedAt ?? DateTime.UtcNow, PlatformData = JsonSerializer.Serialize(relationship, Json) };
            await PutAsync(db.KnowledgeRelationships, row.Id, row, strategy == ImportConflictStrategy.Overwrite, ct);
        }
    }

    private async Task ExportPlainHistoryAndRelationshipsAsync(PortableExportPackage package, HashSet<Guid> itemIds, CancellationToken ct)
    {
        package.PlainVersions = (await db.KnowledgeVersions.AsNoTracking().Where(v => itemIds.Contains(v.KnowledgeId)).ToListAsync(ct))
            .Select(SelfHostedSealedStore.ToPlainVersion).ToList();
        var relationships = await db.KnowledgeRelationships.AsNoTracking().Where(r => itemIds.Contains(r.SourceKnowledgeId) && itemIds.Contains(r.TargetKnowledgeId)).ToListAsync(ct);
        foreach (var group in relationships.GroupBy(r => r.SourceKnowledgeId))
        {
            var values = group.Select(row => {
                var dto = row.PlatformData != null ? JsonSerializer.Deserialize<PortableKnowledgeRelationship>(row.PlatformData, Json)! : new PortableKnowledgeRelationship { OriginalRelationshipId = row.Id };
                dto.Id = row.Id; dto.SourceKnowledgeId = row.SourceKnowledgeId; dto.TargetKnowledgeId = row.TargetKnowledgeId;
                dto.RelationshipType = row.RelationshipType; dto.Confidence = row.Confidence; dto.PortableMetadataJson = row.Metadata;
                dto.CreatedAt = row.CreatedAt; dto.UpdatedAt = row.UpdatedAt; return dto;
            }).ToList();
            var plain = package.Data.KnowledgeItems.SingleOrDefault(k => k.Id == group.Key);
            if (plain != null) plain.Relationships = values;
            var sealedItem = package.SealedItems?.SingleOrDefault(k => k.Id == group.Key);
            if (sealedItem != null) sealedItem.Relationships = values;
        }
    }
}
