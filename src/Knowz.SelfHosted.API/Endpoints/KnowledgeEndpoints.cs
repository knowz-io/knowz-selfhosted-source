using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/knowledge").WithTags("Knowledge");

        group.MapGet("/", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            int page = 1,
            int pageSize = 20,
            string sort = "created",
            string sortDir = "desc",
            string? type = null,
            string? title = null,
            string? fileName = null,
            string? startDate = null,
            string? endDate = null,
            string? vaultId = null,
            string? createdByUserId = null,
            string? tag = null,
            CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);

            Guid? filterVaultId = !string.IsNullOrEmpty(vaultId) && Guid.TryParse(vaultId, out var vid) ? vid : null;
            Guid? filterCreatedByUserId = !string.IsNullOrEmpty(createdByUserId) && Guid.TryParse(createdByUserId, out var cid) ? cid : null;

            var result = await svc.ListKnowledgeItemsAsync(
                page, pageSize, sort, sortDir, type, title, fileName, startDate, endDate, ct,
                accessibleVaultIds, filterVaultId, filterCreatedByUserId, tag);
            return Results.Ok(result);
        }).Produces<KnowledgeListResponse>();

        group.MapGet("/creators", async (
            KnowledgeService svc,
            CancellationToken ct) =>
        {
            var creators = await svc.GetKnowledgeCreatorsAsync(ct);
            return Results.Ok(creators);
        }).Produces<List<CreatorRef>>();

        group.MapGet("/{id:guid}", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct) =>
        {
            // Check if user has access to this knowledge item's vaults
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            if (accessibleVaultIds != null)
            {
                var vaultIds = await svc.GetKnowledgeVaultIdsAsync(id, ct);
                if (vaultIds.Count > 0 && !vaultIds.Any(v => accessibleVaultIds.Contains(v)))
                    return Results.Json(new { error = "Access denied to this knowledge item." }, statusCode: 403);
            }

            var result = await svc.GetKnowledgeItemAsync(id, ct);
            return result is null
                ? Results.NotFound(new { error = "Knowledge item not found" })
                : Results.Ok(result);
        }).Produces<KnowledgeItemResponse>().Produces(404);

        group.MapPost("/", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            FileStorageService fileSvc,
            HttpContext context,
            CreateKnowledgeRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "content is required" });

            if (req.Content.Length > 1_048_576)
                return Results.BadRequest(new { error = "Content exceeds maximum allowed size of 1MB" });

            // Check write access to target vault
            if (!string.IsNullOrWhiteSpace(req.VaultId) && Guid.TryParse(req.VaultId, out var targetVaultId))
            {
                var hasAccess = await VaultEndpoints.HasVaultAccessAsync(
                    context, vaultAccessService, targetVaultId, requireWrite: true, ct: ct);
                if (!hasAccess)
                    return Results.Json(new { error = "Access denied to the target vault." }, statusCode: 403);
            }

            var userId = VaultEndpoints.GetUserIdFromContext(context);

            var result = await svc.CreateKnowledgeAsync(
                req.Content,
                // Leave as placeholder when caller didn't supply one — EnrichmentBackgroundService
                // regenerates via GenerateTitleAsync from combined content + attachments.
                req.Title ?? "Untitled",
                req.Type ?? "Note",
                req.VaultId,
                req.Tags ?? new List<string>(),
                req.Source,
                ct,
                userId);

            // Attach pre-uploaded files to the newly created knowledge item
            if (req.AttachmentFileRecordIds?.Count > 0)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("KnowledgeEndpoints");
                foreach (var fileRecordId in req.AttachmentFileRecordIds)
                {
                    try
                    {
                        await fileSvc.AttachToKnowledgeAsync(fileRecordId, result.Id, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to attach file {FileRecordId} to knowledge {KnowledgeId}", fileRecordId, result.Id);
                    }
                }
            }

            return Results.Created($"/api/v1/knowledge/{result.Id}", result);
        }).Produces<CreateKnowledgeResult>(201).Produces(400);

        group.MapPut("/{id:guid}", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            UpdateKnowledgeRequest req,
            CancellationToken ct) =>
        {
            if (req.Content is not null && req.Content.Length > 1_048_576)
                return Results.BadRequest(new { error = "Content exceeds maximum allowed size of 1MB" });

            // Check write access to this knowledge item's vaults
            var vaultIds = await svc.GetKnowledgeVaultIdsAsync(id, ct);
            if (vaultIds.Count > 0)
            {
                var hasWriteAccess = false;
                foreach (var vaultId in vaultIds)
                {
                    if (await VaultEndpoints.HasVaultAccessAsync(context, vaultAccessService, vaultId, requireWrite: true, ct: ct))
                    {
                        hasWriteAccess = true;
                        break;
                    }
                }
                if (!hasWriteAccess)
                    return Results.Json(new { error = "Access denied. Write permission required." }, statusCode: 403);
            }

            // Check write access to destination vault if moving to a different vault
            if (!string.IsNullOrWhiteSpace(req.VaultId) && Guid.TryParse(req.VaultId, out var destVaultId))
            {
                var hasDestAccess = await VaultEndpoints.HasVaultAccessAsync(context, vaultAccessService, destVaultId, requireWrite: true, ct: ct);
                if (!hasDestAccess)
                    return Results.Json(new { error = "Access denied: no write permission to destination vault" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await svc.UpdateKnowledgeAsync(
                id, req.Title, req.Content, req.Source, req.Tags, req.VaultId, ct,
                summaryRefinementGuidance: req.SummaryRefinementGuidance);
            return result is null
                ? Results.NotFound(new { error = "Knowledge item not found" })
                : Results.Ok(result);
        }).Produces<UpdateKnowledgeResult>().Produces(404).Produces(400);

        group.MapPost("/{id:guid}/amend", async (
            KnowledgeService svc,
            IContentAmendmentService amendmentService,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            AmendKnowledgeRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });

            // Check write access to this knowledge item's vaults
            var vaultIds = await svc.GetKnowledgeVaultIdsAsync(id, ct);
            if (vaultIds.Count > 0)
            {
                var hasWriteAccess = false;
                foreach (var vaultId in vaultIds)
                {
                    if (await VaultEndpoints.HasVaultAccessAsync(context, vaultAccessService, vaultId, requireWrite: true, ct: ct))
                    {
                        hasWriteAccess = true;
                        break;
                    }
                }
                if (!hasWriteAccess)
                    return Results.Json(new { error = "Access denied. Write permission required." }, statusCode: 403);
            }

            // Get the existing knowledge item
            var item = await svc.GetKnowledgeItemAsync(id, ct);
            if (item is null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            if (string.IsNullOrWhiteSpace(item.Content))
                return Results.BadRequest(new { error = "Knowledge item has no content to amend" });

            try
            {
                var amendedContent = await amendmentService.ApplyContentUpdateAsync(item.Content, req.Instruction, ct);
                var result = await svc.UpdateKnowledgeAsync(id, null, amendedContent, null, null, null, ct);
                return result is null
                    ? Results.NotFound(new { error = "Knowledge item not found" })
                    : Results.Ok(new { status = "amended", id = result.Id, title = result.Title });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not configured"))
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).Produces<object>().Produces(400).Produces(404);

        group.MapDelete("/{id:guid}", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct) =>
        {
            // Check delete access to this knowledge item's vaults
            var vaultIds = await svc.GetKnowledgeVaultIdsAsync(id, ct);
            if (vaultIds.Count > 0)
            {
                var hasDeleteAccess = false;
                foreach (var vaultId in vaultIds)
                {
                    if (await VaultEndpoints.HasVaultAccessAsync(context, vaultAccessService, vaultId, requireDelete: true, ct: ct))
                    {
                        hasDeleteAccess = true;
                        break;
                    }
                }
                if (!hasDeleteAccess)
                    return Results.Json(new { error = "Access denied. Delete permission required." }, statusCode: 403);
            }

            var result = await svc.DeleteKnowledgeAsync(id, ct);
            return result is null
                ? Results.NotFound(new { error = "Knowledge item not found" })
                : Results.Ok(result);
        }).Produces<DeleteResult>().Produces(404);

        // Fix F: Re-add batch-move endpoint with vault access enforcement
        group.MapPost("/batch-move", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            BatchMoveKnowledgeRequest req,
            CancellationToken ct) =>
        {
            if (req.KnowledgeIds == null || req.KnowledgeIds.Count == 0)
                return Results.BadRequest(new { error = "knowledgeIds is required and must not be empty" });

            // Require write access to the target vault
            var hasAccess = await VaultEndpoints.HasVaultAccessAsync(
                context, vaultAccessService, req.TargetVaultId, requireWrite: true, ct: ct);
            if (!hasAccess)
                return Results.Json(new { error = "Access denied to the target vault." }, statusCode: 403);

            // Require write access to all source vaults containing the knowledge items
            var sourceVaultIds = new HashSet<Guid>();
            foreach (var knowledgeId in req.KnowledgeIds)
            {
                var knowledgeVaultIds = await svc.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
                if (knowledgeVaultIds is { Count: > 0 })
                {
                    foreach (var vaultId in knowledgeVaultIds)
                    {
                        sourceVaultIds.Add(vaultId);
                    }
                }
            }

            if (sourceVaultIds.Count > 0)
            {
                foreach (var vaultId in sourceVaultIds)
                {
                    var hasSourceAccess = await VaultEndpoints.HasVaultAccessAsync(
                        context, vaultAccessService, vaultId, requireWrite: true, ct: ct);
                    if (!hasSourceAccess)
                    {
                        return Results.Json(
                            new { error = "Access denied to one or more source vaults." },
                            statusCode: 403);
                    }
                }
            }

            var result = await svc.BatchMoveToVaultAsync(req.KnowledgeIds, req.TargetVaultId, ct);
            return Results.Ok(result);
        }).Produces<BatchMoveResult>().Produces(400);

        group.MapPost("/{id:guid}/reprocess", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid id,
            CancellationToken ct) =>
        {
            // Check access to this knowledge item's vaults
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            if (accessibleVaultIds != null)
            {
                var vaultIds = await svc.GetKnowledgeVaultIdsAsync(id, ct);
                if (vaultIds.Count > 0 && !vaultIds.Any(v => accessibleVaultIds.Contains(v)))
                    return Results.Json(new { error = "Access denied to this knowledge item." }, statusCode: 403);
            }

            var result = await svc.ReprocessKnowledgeAsync(id, ct);
            return result is null
                ? Results.NotFound(new { error = "Knowledge item not found" })
                : Results.Ok(result);
        }).Produces<ReprocessResult>().Produces(404);

        group.MapGet("/stats", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CancellationToken ct) =>
        {
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            return Results.Ok(await svc.GetStatisticsAsync(ct, accessibleVaultIds));
        }).Produces<KnowledgeStatsResponse>();

        // CLI-compatible statistics endpoint: returns shape expected by KnowzApiClient
        group.MapGet("/statistics", async (
            KnowledgeService svc,
            VaultService vaultSvc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CancellationToken ct) =>
        {
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            var stats = await svc.GetStatisticsAsync(ct, accessibleVaultIds);
            var vaults = await vaultSvc.ListVaultsAsync(false, ct);

            // Return in the shape the CLI's KnowzApiClient expects (wrapped in ApiResponse)
            var totalVaults = accessibleVaultIds != null
                ? vaults.Vaults.Count(v => accessibleVaultIds.Contains(v.Id))
                : vaults.Vaults.Count;

            var typeCounts = stats.ByType.ToDictionary(t => t.Type, t => t.Count);

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    totalKnowledge = stats.TotalKnowledgeItems,
                    totalVaults = totalVaults,
                    totalPersons = 0,
                    totalLocations = 0,
                    totalEvents = 0,
                    storageUsed = (long?)null,
                    typeCounts = typeCounts
                }
            });
        });

        // Quick knowledge creation: simplified endpoint for CLI
        group.MapPost("/quick", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            CreateKnowledgeRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "content is required" });

            if (req.Content.Length > 1_048_576)
                return Results.BadRequest(new { error = "Content exceeds maximum allowed size of 1MB" });

            // Check write access to target vault
            if (!string.IsNullOrWhiteSpace(req.VaultId) && Guid.TryParse(req.VaultId, out var targetVaultId))
            {
                var hasAccess = await VaultEndpoints.HasVaultAccessAsync(
                    context, vaultAccessService, targetVaultId, requireWrite: true, ct: ct);
                if (!hasAccess)
                    return Results.Json(new { error = "Access denied to the target vault." }, statusCode: 403);
            }

            var userId = VaultEndpoints.GetUserIdFromContext(context);

            // Use placeholder when caller didn't supply a title — EnrichmentBackgroundService
            // regenerates via GenerateTitleAsync from combined content + attachments.
            var title = req.Title ?? "Untitled";

            var result = await svc.CreateKnowledgeAsync(
                req.Content,
                title,
                req.Type ?? "Note",
                req.VaultId,
                req.Tags ?? new List<string>(),
                req.Source,
                ct,
                userId);

            // Return in ApiResponse<KnowledgeDto> shape for CLI compatibility
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    id = result.Id,
                    title = title,
                    content = req.Content,
                    type = req.Type ?? "Note",
                    vaultId = req.VaultId,
                    tags = string.Join(", ", req.Tags ?? new List<string>()),
                    createdAt = DateTime.UtcNow,
                    summary = (string?)null
                }
            });
        }).Produces<object>(200).Produces(400);

        group.MapGet("/{id:guid}/enrichment-status", async (
            SelfHostedDbContext db,
            ITenantProvider tenantProvider,
            Guid id,
            CancellationToken ct) =>
        {
            var callerTenantId = tenantProvider.TenantId;

            // Check knowledge item exists (query filter already scopes by tenant)
            var knowledge = await db.KnowledgeItems
                .AsNoTracking()
                .Where(k => k.Id == id)
                .Select(k => new { k.IsIndexed, k.IndexedAt, k.UpdatedAt })
                .FirstOrDefaultAsync(ct);

            if (knowledge == null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            // Check enrichment outbox for the latest status (no query filter — scope by tenant explicitly)
            var outboxItem = await db.EnrichmentOutbox
                .AsNoTracking()
                .Where(e => e.KnowledgeId == id && e.TenantId == callerTenantId)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync(ct);

            string status;
            if (outboxItem != null)
            {
                status = outboxItem.Status switch
                {
                    EnrichmentStatus.Pending => "pending",
                    EnrichmentStatus.Processing => "processing",
                    EnrichmentStatus.Completed => "completed",
                    EnrichmentStatus.Failed => "failed",
                    _ => "pending"
                };
            }
            else
            {
                // No outbox entry: infer from knowledge item state
                status = knowledge.IsIndexed ? "completed" : "pending";
            }

            return Results.Ok(new
            {
                status,
                isIndexed = knowledge.IsIndexed,
                updatedAt = knowledge.UpdatedAt != default ? knowledge.UpdatedAt.ToString("o") : knowledge.IndexedAt?.ToString("o")
            });
        }).Produces<object>().Produces(404);

        // Re-enrich a single knowledge item (triggers re-summarization, re-indexing, BriefSummary generation)
        // WorkGroupID: kc-feat-selfhosted-retrieval-policy-20260413-030000
        group.MapPost("/{id:guid}/re-enrich", async (
            SelfHostedDbContext db,
            ITenantProvider tenantProvider,
            IEnrichmentOutboxWriter enrichmentWriter,
            Guid id,
            CancellationToken ct) =>
        {
            var knowledge = await db.KnowledgeItems
                .Where(k => k.Id == id)
                .Select(k => new { k.Id, k.TenantId })
                .FirstOrDefaultAsync(ct);

            if (knowledge == null)
                return Results.NotFound(new { error = "Knowledge item not found" });

            await enrichmentWriter.EnqueueAsync(knowledge.Id, knowledge.TenantId, ct);
            return Results.Accepted(value: new { message = "Re-enrichment queued", knowledgeId = id });
        }).Produces<object>(202).Produces(404);

        // Bulk re-enrich all items missing BriefSummary or that haven't been indexed
        // WorkGroupID: kc-feat-selfhosted-retrieval-policy-20260413-030000
        group.MapPost("/re-enrich-all", async (
            SelfHostedDbContext db,
            ITenantProvider tenantProvider,
            IEnrichmentOutboxWriter enrichmentWriter,
            CancellationToken ct) =>
        {
            var callerTenantId = tenantProvider.TenantId;

            // Find items missing BriefSummary or not indexed
            var itemsToReEnrich = await db.KnowledgeItems
                .Where(k => k.BriefSummary == null || !k.IsIndexed)
                .Select(k => new { k.Id, k.TenantId })
                .Take(200) // Safety cap
                .ToListAsync(ct);

            var enqueued = 0;
            foreach (var item in itemsToReEnrich)
            {
                await enrichmentWriter.EnqueueAsync(item.Id, item.TenantId, ct);
                enqueued++;
            }

            return Results.Ok(new
            {
                message = $"Re-enrichment queued for {enqueued} items",
                totalFound = itemsToReEnrich.Count,
                enqueued
            });
        }).Produces<object>(200);

        // --- Relationship endpoints ---

        group.MapGet("/{id:guid}/relationships", async (
            SelfHostedDbContext db,
            Guid id,
            CancellationToken ct) =>
        {
            // Verify the knowledge item exists (query filter scopes by tenant)
            var exists = await db.KnowledgeItems.AnyAsync(k => k.Id == id, ct);
            if (!exists)
                return Results.NotFound(new { error = "Knowledge item not found" });

            var relationships = await db.KnowledgeRelationships
                .AsNoTracking()
                .Where(r => r.SourceKnowledgeId == id || r.TargetKnowledgeId == id)
                .Select(r => new
                {
                    r.Id,
                    r.SourceKnowledgeId,
                    r.TargetKnowledgeId,
                    RelationshipType = r.RelationshipType.ToString(),
                    r.Confidence,
                    r.Weight,
                    r.IsBidirectional,
                    r.IsAutoDetected,
                    r.Metadata,
                    r.CreatedAt,
                    r.UpdatedAt,
                    SourceTitle = r.SourceKnowledge != null ? r.SourceKnowledge.Title : null,
                    TargetTitle = r.TargetKnowledge != null ? r.TargetKnowledge.Title : null
                })
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(new { data = relationships, total = relationships.Count });
        }).Produces<object>().Produces(404);

        group.MapPost("/{id:guid}/relationships", async (
            SelfHostedDbContext db,
            ITenantProvider tenantProvider,
            Guid id,
            CreateRelationshipRequest req,
            CancellationToken ct) =>
        {
            // Verify source knowledge exists
            var sourceExists = await db.KnowledgeItems.AnyAsync(k => k.Id == id, ct);
            if (!sourceExists)
                return Results.NotFound(new { error = "Source knowledge item not found" });

            // Verify target knowledge exists
            var targetExists = await db.KnowledgeItems.AnyAsync(k => k.Id == req.TargetKnowledgeId, ct);
            if (!targetExists)
                return Results.NotFound(new { error = "Target knowledge item not found" });

            if (id == req.TargetKnowledgeId)
                return Results.BadRequest(new { error = "Cannot create a relationship to itself" });

            // Parse relationship type
            var relType = KnowledgeRelationshipType.RelatedTo;
            if (!string.IsNullOrWhiteSpace(req.RelationshipType) &&
                !Enum.TryParse<KnowledgeRelationshipType>(req.RelationshipType, ignoreCase: true, out relType))
            {
                return Results.BadRequest(new { error = $"Invalid relationship type: {req.RelationshipType}" });
            }

            // Symmetric normalization: always store smaller GUID as source for bidirectional
            var isBidirectional = req.IsBidirectional ?? true;
            var sourceId = id;
            var targetId = req.TargetKnowledgeId;
            if (isBidirectional && sourceId.CompareTo(targetId) > 0)
            {
                (sourceId, targetId) = (targetId, sourceId);
            }

            // Idempotent upsert: check if relationship already exists
            var existing = await db.KnowledgeRelationships
                .FirstOrDefaultAsync(r => r.SourceKnowledgeId == sourceId && r.TargetKnowledgeId == targetId, ct);

            if (existing != null)
            {
                // Update existing relationship
                existing.RelationshipType = relType;
                existing.Confidence = req.Confidence ?? existing.Confidence;
                existing.Weight = req.Weight ?? existing.Weight;
                existing.IsBidirectional = isBidirectional;
                existing.Metadata = req.Metadata ?? existing.Metadata;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    id = existing.Id,
                    sourceKnowledgeId = existing.SourceKnowledgeId,
                    targetKnowledgeId = existing.TargetKnowledgeId,
                    relationshipType = existing.RelationshipType.ToString(),
                    existing.Confidence,
                    existing.Weight,
                    existing.IsBidirectional,
                    existing.IsAutoDetected,
                    existing.Metadata,
                    existing.CreatedAt,
                    existing.UpdatedAt
                });
            }

            var relationship = new KnowledgeRelationship
            {
                TenantId = tenantProvider.TenantId,
                SourceKnowledgeId = sourceId,
                TargetKnowledgeId = targetId,
                RelationshipType = relType,
                Confidence = req.Confidence ?? 1.0,
                Weight = req.Weight ?? 1.0,
                IsBidirectional = isBidirectional,
                Metadata = req.Metadata
            };

            db.KnowledgeRelationships.Add(relationship);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/knowledge/{id}/relationships", new
            {
                relationship.Id,
                relationship.SourceKnowledgeId,
                relationship.TargetKnowledgeId,
                RelationshipType = relationship.RelationshipType.ToString(),
                relationship.Confidence,
                relationship.Weight,
                relationship.IsBidirectional,
                relationship.IsAutoDetected,
                relationship.Metadata,
                relationship.CreatedAt,
                relationship.UpdatedAt
            });
        }).Produces<object>(201).Produces(400).Produces(404);

        group.MapDelete("/relationships/{relationshipId:guid}", async (
            SelfHostedDbContext db,
            Guid relationshipId,
            CancellationToken ct) =>
        {
            var relationship = await db.KnowledgeRelationships
                .FirstOrDefaultAsync(r => r.Id == relationshipId, ct);

            if (relationship is null)
                return Results.NotFound(new { error = "Relationship not found" });

            relationship.IsDeleted = true;
            relationship.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { deleted = true, id = relationshipId });
        }).Produces<object>().Produces(404);
    }

    /// <summary>
    /// Maps the knowledge-item-types endpoint at the root API group level.
    /// Call this from Program.cs: app.MapKnowledgeItemTypesEndpoints();
    /// </summary>
    public static void MapKnowledgeItemTypesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Knowledge");

        group.MapGet("/knowledge-item-types", () =>
        {
            var types = Enum.GetValues<KnowledgeType>()
                .Select(t => new { name = t.ToString(), value = (int)t })
                .ToList();

            return Results.Ok(new { success = true, data = types });
        });
    }

    /// <summary>
    /// Maps the per-item commit-history query endpoint under the vault-scoped
    /// URL space. Call this from Program.cs: app.MapVaultKnowledgeCommitHistoryEndpoints().
    ///
    /// Route: GET /api/v1/vaults/{vaultId:guid}/knowledge/{knowledgeId:guid}/commit-history
    ///
    /// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
    /// NodeID: SelfHostedKnowledgeCommitHistoryQuery
    /// </summary>
    public static void MapVaultKnowledgeCommitHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/vaults").WithTags("Knowledge");

        group.MapGet("/{vaultId:guid}/knowledge/{knowledgeId:guid}/commit-history", async (
            KnowledgeService svc,
            IVaultAccessService vaultAccessService,
            HttpContext context,
            Guid vaultId,
            Guid knowledgeId,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);

            // Auth step 1: caller must have access to the vault in the URL path.
            var accessibleVaultIds = await VaultEndpoints.ResolveAccessibleVaultIdsAsync(context, vaultAccessService, ct);
            if (accessibleVaultIds != null && !accessibleVaultIds.Contains(vaultId))
            {
                return Results.Json(new { error = "Access denied to this vault." }, statusCode: 403);
            }

            // Auth step 2: knowledge item must belong to the vault in the URL path.
            var vaultIds = await svc.GetKnowledgeVaultIdsAsync(knowledgeId, ct);
            if (!vaultIds.Contains(vaultId))
            {
                return Results.NotFound(new { error = "Knowledge item not found in this vault." });
            }

            var (items, total) = await svc.GetCommitHistoryForItemAsync(knowledgeId, page, pageSize, ct);
            return Results.Ok(new CommitHistoryResponse(items, total, page, pageSize));
        }).Produces<CommitHistoryResponse>().Produces(403).Produces(404);
    }
}
