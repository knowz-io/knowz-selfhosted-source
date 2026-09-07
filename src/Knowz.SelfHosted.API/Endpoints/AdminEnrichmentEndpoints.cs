using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>
/// SH_ENTERPRISE_RUNTIME_RESILIENCE §2.8: SuperAdmin-only admin view of the
/// enrichment outbox. Consumed by <c>post-deploy-smoke.sh</c> Step 7 to assert
/// <c>Status=Failed count == 0</c> without requiring a <c>sqlcmd</c> prereq on
/// the deploy host.
///
/// - Route: <c>GET /api/v1/admin/enrichment/outbox?status=Failed&amp;limit=50</c>
/// - Auth: SuperAdmin (LegacyApiKey explicitly denied — per SECURITY_HARDENING Rule 7)
/// - Response shape (exact): <c>{ totalCount, items: [{ id, knowledgeId, status,
///   attemptCount, lastError, createdAt }] }</c>.
/// - Tenant scope: EF query filter on <c>EnrichmentOutboxItem</c> — no manual where clause.
/// </summary>
public static class AdminEnrichmentEndpoints
{
    public static void MapAdminEnrichmentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/enrichment/outbox", async (
                HttpContext ctx,
                SelfHostedDbContext db,
                ITenantProvider tenantProvider,
                EnrichmentStatus? status,
                int? limit,
                CancellationToken ct) =>
            {
                if (!AuthorizationHelpers.IsSuperAdmin(ctx))
                {
                    return AuthorizationHelpers.Forbidden();
                }

                var effectiveLimit = Math.Clamp(limit ?? 50, 1, 500);
                var currentTenantId = tenantProvider.TenantId;

                // EnrichmentOutboxItem has no EF query filter (infrastructure entity),
                // so scope explicitly here to prevent cross-tenant leakage.
                IQueryable<EnrichmentOutboxItem> q = db.EnrichmentOutbox
                    .AsNoTracking()
                    .Where(x => x.TenantId == currentTenantId);
                if (status is not null)
                {
                    q = q.Where(x => x.Status == status.Value);
                }

                var totalCount = await q.CountAsync(ct);
                var items = await q
                    .OrderByDescending(x => x.CreatedAt)
                    .Take(effectiveLimit)
                    .Select(x => new
                    {
                        id = x.Id,
                        knowledgeId = x.KnowledgeId,
                        status = x.Status,
                        attemptCount = x.AiProcessingAttempts,
                        lastError = x.ErrorMessage,
                        createdAt = x.CreatedAt,
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { totalCount, items });
            })
            .RequireAuthorization()
            .Produces(200).Produces(401).Produces(403)
            .WithTags("Administration");
    }
}
