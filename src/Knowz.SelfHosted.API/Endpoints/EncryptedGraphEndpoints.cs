using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class EncryptedGraphEndpoints
{
    public sealed record PrimarySelection(Guid ContextId, string Fingerprint, string ExpectedRevision, int? Generation = null);
    public static void MapEncryptedGraphEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/encrypted/items/{knowledgeId:guid}/recovery-state", async (HttpContext http, Guid knowledgeId, Guid vaultId, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) =>
        {
            var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, ct);
            if (actor == null || !await AuthorizedAsync(http, vaultId, db, access, false, ct)) return AuthorizationHelpers.Forbidden();
            try
            {
                var state = await SelfHostedInactiveShelf.ReadAsync(db, actor.Value.TenantId, actor.Value.Id, vaultId, knowledgeId, ct);
                http.Response.Headers.CacheControl = "no-store"; return Results.Ok(state);
            }
            catch (UnauthorizedAccessException) { return AuthorizationHelpers.Forbidden(); }
        });
        app.MapGet("/api/v1/vaults/{vaultId:guid}/encryption/contexts", async (HttpContext http, Guid vaultId, SelfHostedDbContext db, IVaultAccessService access, SelfHostedSealedStore store, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(http, vaultId, db, access, false, ct)) return AuthorizationHelpers.Forbidden();
            var catalog = await store.GetCatalogAsync(vaultId, ct);
            if (catalog == null) return Results.NotFound();
            var contextIds = catalog.Contexts.Select(c => c.ContextId).ToList();
            var anchors = await db.ShCryptoContextImportAnchors.AsNoTracking().Where(a => contextIds.Contains(a.ContextId) && db.ShArchiveImportJournals.Any(j => j.PlanId == a.PlanId && j.Status == "committed" && j.ArchiveId == a.ArchiveId && j.ManifestSha256 == a.ManifestSha256 && j.MappingSha256 == a.MappingSha256)).OrderBy(a => a.ContextId).ThenBy(a => a.PlanId)
                .Select(a => new { a.ContextId, a.PlanId, a.ArchiveId, a.SourceTrust, a.ManifestSha256, a.MappingSha256 }).ToListAsync(ct);
            return Results.Ok(new { vaultId, catalog.Revision, cryptoContexts = catalog.Contexts, cryptoKeyCatalog = catalog.Keys,
                vaultCryptoContextLinks = catalog.Links, acceptedCustodyKeys = catalog.AcceptedCustodyKeys, importAnchors = anchors });
        });
        app.MapPut("/api/v1/vaults/{vaultId:guid}/encryption/primary-context", async (HttpContext http, Guid vaultId, PrimarySelection input, SelfHostedDbContext db, IVaultAccessService access, SelfHostedSealedStore store, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(http, vaultId, db, access, true, ct)) return AuthorizationHelpers.Forbidden();
            if (string.IsNullOrEmpty(input.ExpectedRevision)) return Results.BadRequest(new { error = "expectedRevision is required." });
            var result = await store.SelectPrimaryAsync(vaultId, input.ContextId, input.Fingerprint, input.ExpectedRevision, ct, input.Generation);
            return result.Ok ? Results.Ok(new { result.Id, result.Written }) : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        });
        app.MapGet("/api/v1/vaults/{vaultId:guid}/encryption/payloads/{ownerKind}/{ownerId:guid}", async (HttpContext http, Guid vaultId, string ownerKind, Guid ownerId, SelfHostedDbContext db, IVaultAccessService access, SelfHostedSealedStore store, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(http, vaultId, db, access, false, ct)) return AuthorizationHelpers.Forbidden();
            if (!await OwnsPayloadAsync(db, vaultId, ownerKind, ownerId, ct)) return Results.NotFound();
            return await PayloadAsync(vaultId, ownerKind, ownerId, db, store, ct);
        });
        app.MapGet("/api/v1/vaults/{vaultId:guid}/encryption/files/{fileId:guid}", async (HttpContext http, Guid vaultId, Guid fileId, SelfHostedDbContext db, IVaultAccessService access, SelfHostedGraphStore graph, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(http, vaultId, db, access, false, ct)) return AuthorizationHelpers.Forbidden();
            if (!await OwnsPayloadAsync(db, vaultId, "file", fileId, ct)) return Results.NotFound();
            try { return Results.Stream(await graph.OpenFileAsync(fileId, ct), "application/octet-stream", $"{fileId:D}.bin"); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });
        app.MapGet("/api/v1/encrypted/items/{id:guid}/versions", async (HttpContext http, Guid id, SelfHostedSealedStore store, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) =>
        {
            if (!await AuthorizeRequestAsync(http, db, access, ct)) return AuthorizationHelpers.Forbidden();
            try { return Results.Ok(await store.GetVersionsAsync(id, ct)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KnowzArchiveException)
            { return Results.Json(new { error = "Historical snapshot is not recoverable as ordinary content." }, statusCode: 409); }
        });
        app.MapPost("/api/v1/encrypted/items/{id:guid}/versions/{versionId:guid}/restore", async (HttpContext http, Guid id, Guid versionId, SelfHostedSealedStore store, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) =>
        {
            if (!await AuthorizeRequestAsync(http, db, access, ct)) return AuthorizationHelpers.Forbidden();
            var expected = http.Request.Headers.IfMatch.ToString();
            if (string.IsNullOrEmpty(expected)) return Results.Json(new { error = "If-Match revision is required." }, statusCode: 428);
            var result = await EncryptedHistoryRestoreOperation.RunAsync(http, db, access, id, token => store.RestoreVersionAsync(id, versionId, expected, token), ct);
            return await EncryptedVaultEndpoints.ToWriteResultAsync(http, store, result, "knowledge", ct);
        });
    }
    internal static async Task<IResult> PayloadAsync(Guid vaultId, string ownerKind, Guid ownerId, SelfHostedDbContext db, SelfHostedSealedStore store, CancellationToken ct, int statusCode = 200)
    {
        try
        {
            object? record = ownerKind switch
            {
                "knowledge" => (await store.GetAsync(ownerId, ct))?.Envelope,
                "comment" => (await store.GetCommentAsync(ownerId, ct))?.Envelope,
                "file" => await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == ownerId, ct),
                "attachment" => await db.ShSealedAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == ownerId, ct),
                "version" => await db.ShSealedVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == ownerId, ct),
                _ => null
            };
            var plainVersion = false;
            if (record == null && ownerKind == "version")
            {
                var row = await db.KnowledgeVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == ownerId, ct);
                if (row != null)
                {
                    if (await db.ShPayloadKeyReferences.AnyAsync(r => r.OwnerKind == "version" && r.OwnerId == ownerId && r.Role == "body", ct))
                        throw new InvalidDataException("Encrypted body history cannot be read as ordinary history.");
                    record = SelfHostedSealedStore.ToPlainVersion(row); plainVersion = true;
                }
            }
            var plainFile = false;
            if (record == null && ownerKind == "file")
            {
                var row = await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == ownerId && f.EncryptedFieldsJson != null, ct);
                if (row != null) { record = await SelfHostedSealedFiles.GetProtectedFileAsync(db, ownerId, ct); plainFile = true; }
            }
            if (record == null && ownerKind == "knowledge") record = await store.GetPlainItemAsync(ownerId, ct);
            if (record == null) return Results.NotFound();
            // Physical blob keys and internal tenant/lineage fields are not public payload metadata.
            if (ownerKind == "file" && !plainFile) record = JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(record));
            if (ownerKind == "attachment") record = JsonSerializer.Deserialize<SealedAttachmentRecord>(JsonSerializer.Serialize(record));
            if (ownerKind == "version" && !plainVersion) record = JsonSerializer.Deserialize<SealedKnowledgeVersionRecord>(JsonSerializer.Serialize(record));
            var refs = (await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == ownerKind && r.OwnerId == ownerId).ToListAsync(ct))
                .Select(PortablePublicProjection.Reference).ToList();
            var catalog = await store.GetCatalogAsync(vaultId, ct);
            var sealedKnowledge = ownerKind == "knowledge" ? await store.GetAsync(ownerId, ct) : null;
            var revision = sealedKnowledge?.Revision ?? SelfHostedSealedStore.PayloadRevision(record!);
            object? related = null;
            if (ownerKind == "knowledge")
            {
                var sealedComments = await db.ShSealedComments.Where(c => c.KnowledgeId == ownerId).Select(c => c.Id).ToListAsync(ct);
                var comments = sealedComments.Concat(await db.Comments.Where(c => c.KnowledgeId == ownerId).Select(c => c.Id).ToListAsync(ct)).Distinct().ToList();
                var attachments = await db.ShSealedAttachments.Where(a => a.KnowledgeId == ownerId || (a.CommentId.HasValue && comments.Contains(a.CommentId.Value))).Select(a => new { a.Id, a.FileRecordId }).ToListAsync(ct);
                var files = await db.ShSealedFiles.Where(f => f.KnowledgeId == ownerId).Select(f => f.Id).ToListAsync(ct);
                var protectedFiles = db.FileRecords.Where(f => f.EncryptedFieldsJson != null).Select(f => f.Id);
                files.AddRange(await db.FileAttachments.Where(a => protectedFiles.Contains(a.FileRecordId) && (a.KnowledgeId == ownerId || a.CommentId.HasValue && comments.Contains(a.CommentId.Value))).Select(a => a.FileRecordId).ToListAsync(ct));
                files.AddRange(attachments.Select(a => a.FileRecordId));
                var roots = (await db.ShSealedFiles.Where(f => files.Contains(f.Id)).Select(f => f.RootFileRecordId ?? f.Id).ToListAsync(ct))
                    .Concat(await db.FileRecords.Where(f => files.Contains(f.Id)).Select(f => f.RootFileRecordId ?? f.Id).ToListAsync(ct)).Distinct().ToList();
                files.AddRange(await db.ShSealedFiles.Where(f => roots.Contains(f.Id) || f.RootFileRecordId.HasValue && roots.Contains(f.RootFileRecordId.Value)).Select(f => f.Id).ToListAsync(ct));
                files.AddRange(await db.FileRecords.Where(f => roots.Contains(f.Id) || f.RootFileRecordId.HasValue && roots.Contains(f.RootFileRecordId.Value)).Select(f => f.Id).ToListAsync(ct));
                related = new { comments = sealedComments, files = files.Distinct(), attachments = attachments.Select(a => a.Id),
                    versions = (await db.ShSealedVersions.Where(v => v.KnowledgeId == ownerId).Select(v => v.Id).ToListAsync(ct)).Concat(await db.KnowledgeVersions.Where(v => v.KnowledgeId == ownerId).Select(v => v.Id).ToListAsync(ct)) };
            }
            return Results.Json(new { id = ownerId, written = 1, vaultId, ownerKind, ownerId, revision, record, payloadKeyReferences = refs, cryptoContexts = catalog!.Contexts, cryptoKeyCatalog = catalog.Keys, related }, statusCode: statusCode);
    }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KnowzArchiveException)
        { return Results.Json(new { error = "Protected payload metadata is not recoverable." }, statusCode: 409); }
    }
    internal static async Task<bool> AuthorizeRequestAsync(HttpContext http, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct)
    {
        var userId = AuthorizationHelpers.GetCallerId(http); var tenantId = AuthorizationHelpers.GetCallerTenantId(http);
        if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(http) || !userId.HasValue || !tenantId.HasValue) return false;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user == null) return false;
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
        if (membership != null ? !membership.IsActive : user.TenantId != tenantId) return false;
        var delete = HttpMethods.IsDelete(http.Request.Method);
        var write = !HttpMethods.IsGet(http.Request.Method) && !delete;
        Guid? knowledgeId = null;
        if (Guid.TryParse(http.Request.RouteValues["knowledgeId"]?.ToString(), out var owner)) knowledgeId = owner;
        else if (Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out owner)) knowledgeId = owner;
        else if (Guid.TryParse(http.Request.RouteValues["commentId"]?.ToString(), out var comment)) knowledgeId = await db.ShSealedComments.Where(c => c.Id == comment).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(ct);
        if (knowledgeId.HasValue)
        {
            var vaults = await db.KnowledgeVaults.Where(v => v.KnowledgeId == knowledgeId).Select(v => v.VaultId).ToListAsync(ct);
            if (vaults.Count == 0) return false;
            if (delete)
            {
                if (!await db.KnowledgeItems.AnyAsync(k => k.Id == knowledgeId.Value, ct)) return false;
                foreach (var vault in vaults) if (!await AuthorizedAsync(http, vault, db, access, false, ct, requireDelete: true)) return false;
                return true;
            }
            foreach (var vault in vaults) if (await AuthorizedAsync(http, vault, db, access, write, ct)) return true;
            return false;
        }
        if (Guid.TryParse(http.Request.RouteValues["vaultId"]?.ToString(), out var routeVault)) return await AuthorizedAsync(http, routeVault, db, access, write, ct);
        if (http.Request.ContentLength != 0 && http.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            http.Request.EnableBuffering();
            var json = await EncryptedVaultEndpoints.ReadBodyAsync(http.Request, ct); http.Request.Body.Position = 0;
            if (json != null)
            {
                using var parsed = StrictJson.Parse(json, 8 * 1024 * 1024);
                if ((parsed.RootElement.TryGetProperty("vaultId", out var property) || parsed.RootElement.TryGetProperty("destinationVaultId", out property)) && property.TryGetGuid(out var vault)) return await AuthorizedAsync(http, vault, db, access, write, ct);
            }
        }
        return false;
    }
    internal static async Task<bool> AuthorizedAsync(HttpContext http, Guid vaultId, SelfHostedDbContext db, IVaultAccessService access, bool write, CancellationToken ct, bool requireDelete = false)
    {
        var userId = AuthorizationHelpers.GetCallerId(http); var tenantId = AuthorizationHelpers.GetCallerTenantId(http);
        if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(http) || !userId.HasValue || !tenantId.HasValue) return false;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user == null) return false;
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, ct);
        if (membership != null ? !membership.IsActive : user.TenantId != tenantId) return false;
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && t.IsActive, ct) || !await db.Vaults.AnyAsync(v => v.Id == vaultId && v.TenantId == tenantId, ct)) return false;
        return await access.HasVaultAccessAsync(userId.Value, vaultId, requireWrite: write, requireDelete: requireDelete, ct: ct);
    }
    internal static async Task<bool> OwnsPayloadAsync(SelfHostedDbContext db, Guid vault, string kind, Guid id, CancellationToken ct)
    {
        var knowledgeIds = db.KnowledgeVaults.Where(v => v.VaultId == vault).Select(v => v.KnowledgeId);
        if (kind == "knowledge") return await db.KnowledgeItems.AnyAsync(k => k.Id == id && knowledgeIds.Contains(k.Id), ct);
        if (kind == "comment") return await db.ShSealedComments.AnyAsync(c => c.Id == id && knowledgeIds.Contains(c.KnowledgeId), ct);
        if (kind == "version") return await db.ShSealedVersions.AnyAsync(v => v.Id == id && knowledgeIds.Contains(v.KnowledgeId), ct)
            || await db.KnowledgeVersions.AnyAsync(v => v.Id == id && knowledgeIds.Contains(v.KnowledgeId), ct);
        var comments = db.ShSealedComments.Where(c => knowledgeIds.Contains(c.KnowledgeId)).Select(c => c.Id).Union(db.Comments.Where(c => knowledgeIds.Contains(c.KnowledgeId)).Select(c => c.Id));
        var attachments = db.ShSealedAttachments.Where(a => (a.KnowledgeId.HasValue && knowledgeIds.Contains(a.KnowledgeId.Value)) || (a.CommentId.HasValue && comments.Contains(a.CommentId.Value)));
        if (kind == "attachment") return await attachments.AnyAsync(a => a.Id == id, ct);
        if (kind == "file")
        {
            // Historical bytes inherit current live ownership through logical lineage, never through original crypto binding.
            var sealedFile = await db.ShSealedFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct);
            if (sealedFile is { IsCurrentVersion: false })
            {
                var root = sealedFile.RootFileRecordId ?? sealedFile.Id;
                var heads = await db.ShSealedFiles.Where(f => f.IsCurrentVersion && f.RootFileRecordId == root).Select(f => f.Id).Take(2).ToListAsync(ct);
                if (heads.Count != 1) return false; id = heads[0];
            }
            var plainFile = sealedFile == null ? await db.FileRecords.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.EncryptedFieldsJson != null, ct) : null;
            if (plainFile is { IsCurrentVersion: false })
            {
                var root = plainFile.RootFileRecordId ?? plainFile.Id;
                var heads = await db.FileRecords.Where(f => f.IsCurrentVersion && f.RootFileRecordId == root).Select(f => f.Id).Take(2).ToListAsync(ct);
                if (heads.Count != 1) return false; id = heads[0];
            }
            return await db.FileAttachments.AnyAsync(a => a.FileRecordId == id && db.FileRecords.Any(f => f.Id == id && !f.IsDeleted && f.EncryptedFieldsJson != null) &&
            (a.KnowledgeId.HasValue && knowledgeIds.Contains(a.KnowledgeId.Value) || a.CommentId.HasValue && comments.Contains(a.CommentId.Value)), ct) || await attachments.AnyAsync(a => a.FileRecordId == id && !a.IsDeleted, ct) || await db.ShSealedFiles.AnyAsync(f => f.Id == id && !f.IsDeleted && (f.VaultId == vault || f.KnowledgeId.HasValue && knowledgeIds.Contains(f.KnowledgeId.Value)), ct);
        }
        return false;
    }
}
