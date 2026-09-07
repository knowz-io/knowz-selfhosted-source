using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.API.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/audit-logs").WithTags("Audit");

        group.MapGet("/", async (
            VersioningService versioningService,
            Guid? entityId = null,
            string? entityType = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var (items, totalCount) = await versioningService.GetAuditLogsAsync(
                entityId, entityType, page, pageSize, ct);

            var totalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0;

            var response = new AuditLogListResponse(
                items.Select(a => new AuditLogResponse(
                    a.Id, a.EntityType, a.EntityId, a.Action,
                    a.UserId, a.UserEmail, a.Timestamp, a.Details)).ToList(),
                page, pageSize, totalCount, totalPages);

            return Results.Ok(response);
        }).Produces<AuditLogListResponse>();
    }
}

public record AuditLogResponse(
    Guid Id, string EntityType, Guid EntityId, string Action,
    Guid? UserId, string? UserEmail, DateTime Timestamp, string? Details);

public record AuditLogListResponse(
    List<AuditLogResponse> Items,
    int Page, int PageSize, int TotalItems, int TotalPages);
