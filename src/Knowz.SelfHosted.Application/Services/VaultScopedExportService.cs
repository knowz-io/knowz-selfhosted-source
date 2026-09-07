namespace Knowz.SelfHosted.Application.Services;

using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Exports a single vault's entities as a delta package for sync.
/// Supports incremental export (entities changed since a cursor) and tombstone collection.
/// </summary>
public class VaultScopedExportService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<VaultScopedExportService> _logger;

    public VaultScopedExportService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<VaultScopedExportService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    /// <summary>
    /// Export a single knowledge item from the given local vault as a portable package
    /// (NodeID PlatformSyncItemOps). Throws <see cref="InvalidOperationException"/> when
    /// the knowledge item is not in the given vault. Excludes credential entities (V-SEC).
    /// </summary>
    public async Task<PortableExportPackage> ExportSingleItemAsync(
        Guid localVaultId, Guid localKnowledgeId, CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var serverTimestamp = DateTime.UtcNow;

        var isInVault = await _db.KnowledgeVaults
            .IgnoreQueryFilters()
            .AnyAsync(kv => kv.VaultId == localVaultId && kv.KnowledgeId == localKnowledgeId, ct);

        if (!isInVault)
            throw new InvalidOperationException(
                $"Knowledge {localKnowledgeId} is not a member of vault {localVaultId}");

        var knowledge = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId && k.Id == localKnowledgeId && !k.IsDeleted)
            .Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations)
            .Include(k => k.KnowledgeEvents)
            .Include(k => k.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (knowledge == null)
            throw new InvalidOperationException($"Knowledge {localKnowledgeId} not found or deleted");

        var topics = knowledge.TopicId.HasValue
            ? await _db.Topics.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && t.Id == knowledge.TopicId)
                .AsNoTracking().ToListAsync(ct)
            : new List<Topic>();

        var tagIds = knowledge.Tags.Select(t => t.Id).ToHashSet();
        var tags = tagIds.Count > 0
            ? await _db.Tags.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && tagIds.Contains(t.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Tag>();

        var personIds = knowledge.KnowledgePersons.Select(kp => kp.PersonId).ToHashSet();
        var persons = personIds.Count > 0
            ? await _db.Persons.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && personIds.Contains(p.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Person>();

        var locationIds = knowledge.KnowledgeLocations.Select(kl => kl.LocationId).ToHashSet();
        var locations = locationIds.Count > 0
            ? await _db.Locations.IgnoreQueryFilters()
                .Where(l => l.TenantId == tenantId && locationIds.Contains(l.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Location>();

        var eventIds = knowledge.KnowledgeEvents.Select(ke => ke.EventId).ToHashSet();
        var events = eventIds.Count > 0
            ? await _db.Events.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && eventIds.Contains(e.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Event>();

        var comments = await _db.Comments.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.KnowledgeId == localKnowledgeId)
            .AsNoTracking().ToListAsync(ct);

        // NODE-5: commit-history-scoped relationships for this single item.
        var singleRelationshipLookup = await BuildCommitRelationshipLookupAsync(
            tenantId, new[] { knowledge }, ct);

        var package = new PortableExportPackage
        {
            SchemaVersion = CoreSchema.Version,
            SourceEdition = "selfhosted",
            SourceTenantId = tenantId,
            ExportedAt = serverTimestamp,
            IsIncrementalSync = false,
            SyncCursor = serverTimestamp,
            Tombstones = null,
            Metadata = new PortableExportMetadata
            {
                TotalVaults = 0,
                TotalKnowledgeItems = 1,
                TotalTopics = topics.Count,
                TotalTags = tags.Count,
                TotalPersons = persons.Count,
                TotalLocations = locations.Count,
                TotalEvents = events.Count,
                TotalComments = comments.Count,
                TotalFileRecords = 0,
            },
            Data = new PortableExportData
            {
                Vaults = new(),
                KnowledgeItems = new List<PortableKnowledge> { MapKnowledge(knowledge, localVaultId, singleRelationshipLookup) },
                Topics = topics.Select(MapTopic).ToList(),
                Tags = tags.Select(MapTag).ToList(),
                Persons = persons.Select(MapPerson).ToList(),
                Locations = locations.Select(MapLocation).ToList(),
                Events = events.Select(MapEvent).ToList(),
                Comments = comments.Select(MapComment).ToList(),
                FileRecords = new(),
            },
        };

        _logger.LogInformation(
            "Single-item export for knowledge {KnowledgeId} in vault {VaultId}",
            localKnowledgeId, localVaultId);

        return package;
    }

    /// <summary>
    /// Export a vault-scoped delta package for push to platform.
    /// </summary>
    public async Task<PortableExportPackage> ExportDeltaAsync(
        Guid localVaultId, DateTime? since, VaultSyncLink? syncLink = null,
        CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var serverTimestamp = DateTime.UtcNow;
        var isIncremental = since.HasValue;

        // Step 1: Knowledge IDs in this vault (including soft-deleted for tombstones)
        var knowledgeIdsInVault = await _db.KnowledgeVaults
            .IgnoreQueryFilters()
            .Where(kv => kv.VaultId == localVaultId)
            .Select(kv => kv.KnowledgeId)
            .ToListAsync(ct);

        // Step 2: Knowledge items (including soft-deleted)
        var knowledgeQuery = _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId && knowledgeIdsInVault.Contains(k.Id));

        if (isIncremental)
            knowledgeQuery = knowledgeQuery.Where(k => k.UpdatedAt > since!.Value);

        var knowledgeItems = await knowledgeQuery
            .Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations)
            .Include(k => k.KnowledgeEvents)
            .Include(k => k.Tags)
            .OrderBy(k => k.UpdatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        var activeKnowledgeIds = knowledgeItems.Where(k => !k.IsDeleted).Select(k => k.Id).ToHashSet();

        // Step 3: Collect referenced entities
        var topicIds = knowledgeItems.Where(k => !k.IsDeleted && k.TopicId.HasValue)
            .Select(k => k.TopicId!.Value).Distinct().ToHashSet();
        var topics = topicIds.Count > 0
            ? await _db.Topics.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && topicIds.Contains(t.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Topic>();

        var tagIds = knowledgeItems.Where(k => !k.IsDeleted)
            .SelectMany(k => k.Tags).Select(t => t.Id).Distinct().ToHashSet();
        var tags = tagIds.Count > 0
            ? await _db.Tags.IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId && tagIds.Contains(t.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Tag>();

        var personIds = knowledgeItems.Where(k => !k.IsDeleted)
            .SelectMany(k => k.KnowledgePersons).Select(kp => kp.PersonId).Distinct().ToHashSet();
        var persons = personIds.Count > 0
            ? await _db.Persons.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && personIds.Contains(p.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Person>();

        var locationIds = knowledgeItems.Where(k => !k.IsDeleted)
            .SelectMany(k => k.KnowledgeLocations).Select(kl => kl.LocationId).Distinct().ToHashSet();
        var locations = locationIds.Count > 0
            ? await _db.Locations.IgnoreQueryFilters()
                .Where(l => l.TenantId == tenantId && locationIds.Contains(l.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Location>();

        var eventIds = knowledgeItems.Where(k => !k.IsDeleted)
            .SelectMany(k => k.KnowledgeEvents).Select(ke => ke.EventId).Distinct().ToHashSet();
        var events = eventIds.Count > 0
            ? await _db.Events.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && eventIds.Contains(e.Id))
                .AsNoTracking().ToListAsync(ct)
            : new List<Event>();

        // Comments
        var comments = activeKnowledgeIds.Count > 0
            ? await _db.Comments.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && activeKnowledgeIds.Contains(c.KnowledgeId))
                .AsNoTracking().ToListAsync(ct)
            : new List<KnowledgeComment>();

        // File records
        var fileAttachments = activeKnowledgeIds.Count > 0
            ? await _db.FileAttachments.IgnoreQueryFilters()
                .Where(fa => fa.KnowledgeId.HasValue && activeKnowledgeIds.Contains(fa.KnowledgeId.Value))
                .AsNoTracking().ToListAsync(ct)
            : new List<FileAttachment>();

        var fileRecordIds = fileAttachments.Select(fa => fa.FileRecordId).Distinct().ToHashSet();
        var fileRecords = fileRecordIds.Count > 0
            ? await _db.FileRecords.IgnoreQueryFilters()
                .Where(f => f.TenantId == tenantId && fileRecordIds.Contains(f.Id))
                .Include(f => f.Attachments)
                .AsNoTracking().ToListAsync(ct)
            : new List<FileRecord>();

        // Vault metadata
        var vault = await _db.Vaults.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId && v.Id == localVaultId)
            .Include(v => v.VaultPersons)
            .AsNoTracking().FirstOrDefaultAsync(ct);

        // NODE-5: Commit-history-scoped relationships. See helper for routing rules.
        var relationshipLookup = await BuildCommitRelationshipLookupAsync(
            tenantId, knowledgeItems.Where(k => !k.IsDeleted), ct);

        // Build tombstones from soft-deleted knowledge
        var tombstones = knowledgeItems
            .Where(k => k.IsDeleted)
            .Select(k => new SyncTombstoneDto
            {
                EntityType = "Knowledge",
                EntityId = k.Id,
                DeletedAt = k.UpdatedAt, // ISelfHostedEntity has no DeletedAt; UpdatedAt is set on soft-delete
            }).ToList();

        // Also collect unpropagated SyncTombstone records if we have a sync link
        if (syncLink != null)
        {
            var pendingTombstones = await _db.SyncTombstones
                .Where(st => st.VaultSyncLinkId == syncLink.Id && !st.Propagated)
                .AsNoTracking().ToListAsync(ct);

            foreach (var st in pendingTombstones)
            {
                if (!tombstones.Any(t => t.EntityId == st.LocalEntityId && t.EntityType == st.EntityType))
                {
                    tombstones.Add(new SyncTombstoneDto
                    {
                        EntityType = st.EntityType,
                        EntityId = st.LocalEntityId,
                        DeletedAt = st.DeletedAt,
                    });
                }
            }
        }

        // Map to portable DTOs
        var package = new PortableExportPackage
        {
            SchemaVersion = CoreSchema.Version,
            SourceEdition = "selfhosted",
            SourceTenantId = tenantId,
            ExportedAt = serverTimestamp,
            IsIncrementalSync = isIncremental,
            SyncCursor = serverTimestamp,
            Tombstones = tombstones.Count > 0 ? tombstones : null,
            Metadata = new PortableExportMetadata
            {
                TotalVaults = vault != null ? 1 : 0,
                TotalKnowledgeItems = knowledgeItems.Count(k => !k.IsDeleted),
                TotalTopics = topics.Count,
                TotalTags = tags.Count,
                TotalPersons = persons.Count,
                TotalLocations = locations.Count,
                TotalEvents = events.Count,
                TotalComments = comments.Count,
                TotalFileRecords = fileRecords.Count,
            },
            Data = new PortableExportData
            {
                Vaults = vault != null ? new List<PortableVault>
                {
                    MapVault(vault)
                } : new(),
                KnowledgeItems = knowledgeItems.Where(k => !k.IsDeleted).Select(k => MapKnowledge(k, localVaultId, relationshipLookup)).ToList(),
                Topics = topics.Select(MapTopic).ToList(),
                Tags = tags.Select(MapTag).ToList(),
                Persons = persons.Select(MapPerson).ToList(),
                Locations = locations.Select(MapLocation).ToList(),
                Events = events.Select(MapEvent).ToList(),
                Comments = comments.Select(MapComment).ToList(),
                FileRecords = fileRecords.Select(MapFileRecord).ToList(),
            }
        };

        _logger.LogInformation(
            "Vault-scoped export for vault {VaultId}: {KnowledgeCount} knowledge, {TombstoneCount} tombstones, incremental={IsIncremental}",
            localVaultId, package.Metadata.TotalKnowledgeItems, tombstones.Count, isIncremental);

        return package;
    }

    private static PortableVault MapVault(Vault v)
    {
        var dto = new PortableVault
        {
            Id = v.Id, Name = v.Name, Description = v.Description,
            VaultType = v.VaultType, IsDefault = v.IsDefault,
            ParentVaultId = v.ParentVaultId,
            CreatedAt = v.CreatedAt, UpdatedAt = v.UpdatedAt,
            PersonIds = v.VaultPersons.Select(vp => vp.PersonId).ToList(),
        };
        MergePlatformData(v.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableKnowledge MapKnowledge(
        Knowledge k,
        Guid vaultId,
        IReadOnlyDictionary<Guid, List<PortableKnowledgeRelationship>>? relationshipLookup = null)
    {
        var dto = new PortableKnowledge
        {
            Id = k.Id, Title = k.Title, Content = k.Content,
            Summary = k.Summary, BriefSummary = k.BriefSummary, Source = k.Source,
            Type = k.Type,
            FilePath = k.FilePath,
            IsIndexed = k.IsIndexed, IndexedAt = k.IndexedAt,
            CreatedAt = k.CreatedAt, UpdatedAt = k.UpdatedAt,
            TopicId = k.TopicId,
            VaultIds = new List<Guid> { vaultId },
            PrimaryVaultId = vaultId,
            TagIds = k.Tags.Select(t => t.Id).ToList(),
            PersonIds = k.KnowledgePersons.Select(kp => kp.PersonId).ToList(),
            LocationIds = k.KnowledgeLocations.Select(kl => kl.LocationId).ToList(),
            EventIds = k.KnowledgeEvents.Select(ke => ke.EventId).ToList(),
            PersonLinks = k.KnowledgePersons.Select(kp => new PortableEntityLink
            {
                EntityId = kp.PersonId,
                RelationshipContext = kp.RelationshipContext,
                Role = kp.Role,
                Mentions = kp.Mentions,
                ConfidenceScore = kp.ConfidenceScore,
            }).ToList(),
        };
        if (relationshipLookup != null
            && relationshipLookup.TryGetValue(k.Id, out var relList)
            && relList.Count > 0)
        {
            dto.Relationships = relList;
        }
        MergePlatformData(k.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableTopic MapTopic(Topic t)
    {
        var dto = new PortableTopic { Id = t.Id, Name = t.Name, Description = t.Description, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt };
        MergePlatformData(t.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableTag MapTag(Tag t)
    {
        var dto = new PortableTag { Id = t.Id, Name = t.Name, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt };
        MergePlatformData(t.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortablePerson MapPerson(Person p)
    {
        var dto = new PortablePerson { Id = p.Id, Name = p.Name, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt };
        MergePlatformData(p.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableLocation MapLocation(Location l)
    {
        var dto = new PortableLocation { Id = l.Id, Name = l.Name, CreatedAt = l.CreatedAt, UpdatedAt = l.UpdatedAt };
        MergePlatformData(l.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableEvent MapEvent(Event e)
    {
        var dto = new PortableEvent { Id = e.Id, Name = e.Name, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt };
        MergePlatformData(e.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableKnowledgeComment MapComment(KnowledgeComment c)
    {
        var dto = new PortableKnowledgeComment
        {
            Id = c.Id, KnowledgeId = c.KnowledgeId,
            ParentCommentId = c.ParentCommentId,
            AuthorName = c.AuthorName, Body = c.Body,
            IsAnswer = c.IsAnswer, Sentiment = c.Sentiment,
            CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt,
        };
        MergePlatformData(c.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static PortableFileRecord MapFileRecord(FileRecord f)
    {
        var dto = new PortableFileRecord
        {
            Id = f.Id, FileName = f.FileName, ContentType = f.ContentType,
            SizeBytes = f.SizeBytes, BlobUri = f.BlobUri,
            TranscriptionText = f.TranscriptionText,
            ExtractedText = f.ExtractedText,
            VisionDescription = f.VisionDescription,
            CreatedAt = f.CreatedAt, UpdatedAt = f.UpdatedAt,
            Attachments = f.Attachments?.Select(a => new PortableFileAttachmentLink
            {
                KnowledgeId = a.KnowledgeId,
                CommentId = a.CommentId,
            }).ToList() ?? new(),
        };
        MergePlatformData(f.PlatformData, d => dto.ExtensionData = d);
        return dto;
    }

    private static void MergePlatformData(string? platformData, Action<Dictionary<string, JsonElement>?> setter)
    {
        if (string.IsNullOrEmpty(platformData)) return;
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(platformData);
        if (dict is { Count: > 0 }) setter(dict);
    }

    /// <summary>
    /// NODE-5: Build a lookup from source commit/commit-history Knowledge Id → list of
    /// <see cref="PortableKnowledgeRelationship"/> payload entries. Only relationships
    /// originating from <c>Commit</c>/<c>CommitHistory</c> items with type
    /// <c>PartOf</c> or <c>References</c> are included. See
    /// <c>knowzcode/specs/SVC_PlatformSyncRelationshipPayload.md</c>.
    /// </summary>
    private async Task<Dictionary<Guid, List<PortableKnowledgeRelationship>>>
        BuildCommitRelationshipLookupAsync(
            Guid tenantId,
            IEnumerable<Knowledge> knowledgeItems,
            CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<PortableKnowledgeRelationship>>();

        var commitScoped = knowledgeItems
            .Where(k => k.Type == KnowledgeType.Commit || k.Type == KnowledgeType.CommitHistory)
            .ToDictionary(k => k.Id, k => k);

        if (commitScoped.Count == 0) return result;

        var scopedIds = commitScoped.Keys.ToHashSet();
        var rels = await _db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                && !r.IsDeleted
                && scopedIds.Contains(r.SourceKnowledgeId)
                && (r.RelationshipType == KnowledgeRelationshipType.PartOf
                    || r.RelationshipType == KnowledgeRelationshipType.References))
            .AsNoTracking()
            .ToListAsync(ct);

        if (rels.Count == 0) return result;

        var targetIds = rels.Select(r => r.TargetKnowledgeId).Distinct().ToHashSet();
        var targetInfo = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId && targetIds.Contains(k.Id))
            .Select(k => new { k.Id, k.FilePath, k.Source })
            .AsNoTracking()
            .ToListAsync(ct);
        var targetById = targetInfo.ToDictionary(t => t.Id);

        foreach (var rel in rels)
        {
            if (!commitScoped.TryGetValue(rel.SourceKnowledgeId, out var src))
            {
                continue;
            }

            var sha = ExtractCommitSha(src.Source);
            string? targetFilePath = null;
            if (targetById.TryGetValue(rel.TargetKnowledgeId, out var tgt))
            {
                targetFilePath = tgt.FilePath;
            }

            var payload = new PortableKnowledgeRelationship
            {
                SourceCommitSha = sha,
                SourceFilePath = null,
                TargetFilePath = targetFilePath,
                RelationshipType = rel.RelationshipType,
                Metadata = ParseRelationshipMetadata(rel.Metadata)
            };

            if (!result.TryGetValue(rel.SourceKnowledgeId, out var list))
            {
                list = new List<PortableKnowledgeRelationship>();
                result[rel.SourceKnowledgeId] = list;
            }
            list.Add(payload);
        }

        return result;
    }

    /// <summary>
    /// Extract the commit SHA from a commit Knowledge's Source string.
    /// Expected format: "{repoUrl}:{branch}:commit:{sha}" or
    /// "{repoUrl}:{branch}:commit-history" for parents (no SHA).
    /// </summary>
    internal static string? ExtractCommitSha(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        const string marker = ":commit:";
        var idx = source.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var sha = source[(idx + marker.Length)..];
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Parse the optional JSON metadata on a <see cref="KnowledgeRelationship"/>
    /// into the passthrough dictionary carried by the sync payload.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ParseRelationshipMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson);
            return dict is { Count: > 0 } ? dict : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
