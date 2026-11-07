using System.Text.Json;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static partial class EncryptedFileEndpoints
{
    private static void MapAttachmentAndDeleteEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/encrypted").AddEndpointFilter(async (invocation, next) => {
            try { return await next(invocation); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid encrypted attachment metadata." }); }
            catch (BadHttpRequestException e) { return Results.Json(new { error = e.Message }, statusCode: e.StatusCode); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Encrypted record changed; reload before writing." }); }
        });
        group.MapPut("/attachments/{id:guid}", async (HttpContext http, Guid id, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var revision = http.Request.Headers.IfMatch.ToString(); if (revision.Length == 0) return Results.Json(new { error = "If-Match is required." }, statusCode: 428);
            var body = await EncryptedVaultEndpoints.ReadBodyAsync(http.Request, ct); if (body == null || SealedMaterialGuard.DeclaresPrivateMaterial(body)) return Results.BadRequest();
            using var json = StrictJson.Parse(body, 8 * 1024 * 1024); var write = json.RootElement.Deserialize<SealedAttachmentWrite>(Json);
            if (write?.Record == null || write.PayloadKeyReferences == null || write.Record.Id != id) return Results.BadRequest();
            if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, write.VaultId, db, access, true, ct) || !await EncryptedGraphEndpoints.OwnsPayloadAsync(db, write.VaultId, "attachment", id, ct)) return AuthorizationHelpers.Forbidden();
            var result = await service.UpdateAttachmentAsync(write, revision, ct);
            return result.Ok ? Results.Ok(new { id, ownerId = id, ownerKind = "attachment", vaultId = write.VaultId, revision = SelfHostedSealedStore.PayloadRevision(write.Record), record = write.Record, payloadKeyReferences = write.PayloadKeyReferences }) : StoreResult(result);
        });
        group.MapDelete("/attachments/{id:guid}", async (HttpContext http, Guid id, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var revision = http.Request.Headers.IfMatch.ToString(); if (revision.Length == 0) return Results.Json(new { error = "If-Match is required." }, statusCode: 428);
            var row = await db.ShSealedAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct); if (row == null) return Results.NotFound();
            Guid? knowledge = row.KnowledgeId;
            if (!knowledge.HasValue && row.CommentId.HasValue) knowledge = await db.ShSealedComments.Where(c => c.Id == row.CommentId).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(ct);
            var vaults = await db.KnowledgeVaults.Where(v => v.KnowledgeId == knowledge).Select(v => v.VaultId).ToListAsync(ct);
            var authorized = knowledge.HasValue && await db.KnowledgeItems.AnyAsync(k => k.Id == knowledge.Value, ct) && vaults.Count > 0;
            foreach (var vault in vaults)
                if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, vault, db, access, false, ct, requireDelete: true)) authorized = false;
            if (!authorized) return AuthorizationHelpers.Forbidden();
            return StoreResult(await EncryptedDeleteOperation.RunAsync(http, db, access, "attachment", id, token => service.DeleteAttachmentAsync(id, revision, token), ct));
        });
        group.MapDelete("/files/{id:guid}", async (HttpContext http, Guid id, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var revision = http.Request.Headers.IfMatch.ToString(); if (revision.Length == 0) return Results.Json(new { error = "If-Match is required." }, statusCode: 428);
            var file = await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
            var plain = file == null ? await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.EncryptedFieldsJson != null, ct) : null;
            if (file == null && plain == null) return Results.NotFound();
            var links = await db.ShSealedAttachments.Where(a => a.FileRecordId == id).ToListAsync(ct);
            var ordinaryLinks = await db.FileAttachments.Where(a => a.FileRecordId == id).ToListAsync(ct);
            var comments = links.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value).Concat(ordinaryLinks.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value)).Distinct().ToArray();
            var owners = links.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value)
                .Concat(ordinaryLinks.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value))
                .Concat(await db.Comments.Where(c => comments.Contains(c.Id)).Select(c => c.KnowledgeId).ToListAsync(ct))
                .Concat(await db.ShSealedComments.Where(c => comments.Contains(c.Id)).Select(c => c.KnowledgeId).ToListAsync(ct)).Concat(file?.KnowledgeId is Guid directOwner ? [directOwner] : []).Distinct().ToArray();
            var authorized = false;
            foreach (var vault in await db.KnowledgeVaults.Where(v => owners.Contains(v.KnowledgeId)).Select(v => v.VaultId).Distinct().ToListAsync(ct))
                if (await AuthorizeFileOwnersAsync(http, vault, id, db, access, ct, requireDelete: true)) { authorized = true; break; }
            if (!authorized && owners.Length == 0 && links.Count == 0) authorized = await LiveTenantAdminAsync(http, file?.TenantId ?? plain!.TenantId, db, ct);
            if (!authorized) return AuthorizationHelpers.Forbidden();
            return StoreResult(await EncryptedDeleteOperation.RunAsync(http, db, access, "file", id, token => service.DeleteFileAsync(id, revision, token), ct));
        });
    }
    private static IResult StoreResult(SealedStoreResult result) => result.Ok ? Results.Ok(new { result.Id, result.Written }) : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
    private static async Task<bool> LiveTenantAdminAsync(HttpContext http, Guid tenant, SelfHostedDbContext db, CancellationToken ct)
    {
        var id = AuthorizationHelpers.GetCallerId(http); if (!id.HasValue || AuthorizationHelpers.GetCallerTenantId(http) != tenant) return false;
        if (!await db.Tenants.AnyAsync(t => t.Id == tenant && t.IsActive, ct)) return false;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive, ct); if (user == null) return false;
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == id && m.TenantId == tenant, ct);
        return membership != null ? membership.IsActive && membership.Role >= UserRole.Admin : user.TenantId == tenant && user.Role >= UserRole.Admin;
    }
}
