using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services.Shared;

/// <summary>
/// Shared helpers for writing KnowledgeRelationship edges and merging orphan file
/// path lists into PlatformData JSON. Extracted verbatim from VaultSyncOrchestrator
/// (previously private helpers) so additional services can reuse the exact same
/// idempotent-upsert + orphan-merge semantics without duplicating the implementation.
///
/// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
/// NodeID: SelfHostedCommitKnowledgeLinkage
/// </summary>
public static class KnowledgeRelationshipHelpers
{
    /// <summary>
    /// Idempotent upsert for knowledge relationships. Looks up by
    /// (TenantId, SourceKnowledgeId, TargetKnowledgeId, RelationshipType) and only
    /// creates a new row when none exists. Mirrors the canonical pattern in
    /// VaultSyncOrchestrator prior to extraction.
    /// </summary>
    public static async Task UpsertRelationshipAsync(
        SelfHostedDbContext db,
        Guid tenantId,
        Guid sourceId,
        Guid targetId,
        KnowledgeRelationshipType type,
        CancellationToken ct)
    {
        var exists = await db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId
                && r.SourceKnowledgeId == sourceId
                && r.TargetKnowledgeId == targetId
                && r.RelationshipType == type
                && !r.IsDeleted, ct);
        if (exists) return;

        db.KnowledgeRelationships.Add(new KnowledgeRelationship
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SourceKnowledgeId = sourceId,
            TargetKnowledgeId = targetId,
            RelationshipType = type,
            Confidence = 1.0,
            Weight = 1.0,
            IsAutoDetected = true,
            IsBidirectional = false
        });
    }

    /// <summary>
    /// Append orphan paths to the PlatformData.unlinkedFiles JSON array, preserving
    /// existing keys. Creates a minimal JSON object if PlatformData is empty.
    /// Deduplicates paths already present in the array.
    /// </summary>
    public static string MergeUnlinkedFiles(string? platformDataJson, IReadOnlyList<string> orphanPaths)
    {
        Dictionary<string, object?> dict;
        try
        {
            dict = string.IsNullOrEmpty(platformDataJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(platformDataJson)
                    ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            dict = new Dictionary<string, object?>();
        }

        var existing = new List<string>();
        if (dict.TryGetValue("unlinkedFiles", out var existingValue) && existingValue is not null)
        {
            try
            {
                var existingList = JsonSerializer.Deserialize<List<string>>(
                    JsonSerializer.Serialize(existingValue));
                if (existingList != null)
                {
                    existing.AddRange(existingList);
                }
            }
            catch (JsonException)
            {
                // Ignore malformed prior values — overwrite with current list.
            }
        }

        foreach (var path in orphanPaths)
        {
            if (!existing.Contains(path, StringComparer.Ordinal))
            {
                existing.Add(path);
            }
        }

        dict["unlinkedFiles"] = existing;
        return JsonSerializer.Serialize(dict);
    }
}
