using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace Knowz.SelfHosted.API.Endpoints;

public static partial class EncryptedFileEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void MapEncryptedFileEndpoints(this WebApplication app)
    {
        MapAttachmentAndDeleteEndpoints(app);
        var group = app.MapGroup("/api/v1/encrypted/files").AddEndpointFilter(async (invocation, next) => {
            try { return await next(invocation); }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid encrypted file metadata." }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid encrypted file metadata or content." }); }
            catch (BadHttpRequestException e) { return Results.Json(new { error = e.Message }, statusCode: e.StatusCode); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "File changed; reload before writing." }); }
        });
        group.MapPost("", async (HttpContext http, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var body = await EncryptedVaultEndpoints.ReadBodyAsync(http.Request, ct);
            if (body == null || SealedMaterialGuard.DeclaresPrivateMaterial(body)) return Results.BadRequest(new { error = "Encrypted metadata is required; private material remains local." });
            using var document = StrictJson.Parse(body, 8 * 1024 * 1024);
            var write = document.RootElement.Deserialize<SealedFileWrite>(Json);
            if (write == null) return Results.BadRequest();
            if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, write.VaultId, db, access, true, ct)) return AuthorizationHelpers.Forbidden();
            var revision = http.Request.Headers.IfMatch.ToString(); var key = http.Request.Headers["Idempotency-Key"].ToString();
            if (revision.Length == 0 || key.Length == 0) return Results.Json(new { error = "If-Match and Idempotency-Key are required." }, statusCode: 428);
            return Result(await service.StageAsync(AuthorizationHelpers.GetCallerId(http)!.Value, write, revision, key, ct));
        });
        group.MapPut("/{fileId:guid}/ciphertext", async (HttpContext http, Guid fileId, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var actor = AuthorizationHelpers.GetCallerId(http); if (!actor.HasValue) return AuthorizationHelpers.Forbidden();
            var revision = http.Request.Headers.IfMatch.ToString(); var digest = http.Request.Headers["X-Knowz-Content-SHA256"].ToString();
            if (revision.Length == 0 || digest.Length != 64) return Results.Json(new { error = "If-Match and ciphertext SHA256 are required." }, statusCode: 428);
            var stage = await db.ShSealedFileStages.AsNoTracking().SingleOrDefaultAsync(s => s.FileId == fileId && s.ActorId == actor && s.Revision == revision.Trim('"'), ct);
            if (stage == null) return Results.NotFound();
            async Task<bool> Authorized(CancellationToken token) => await EncryptedGraphEndpoints.AuthorizedAsync(http, stage.VaultId, db, access, true, token);
            if (!await Authorized(ct)) return AuthorizationHelpers.Forbidden();
            if (http.Request.ContentLength > EncryptedGraphProfile.MaxFileBytes) return Results.Json(new { error = "Encrypted file exceeds 64 MiB." }, statusCode: 413);
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (size is { IsReadOnly: false }) size.MaxRequestBodySize = EncryptedGraphProfile.MaxFileBytes;
            return Result(await service.UploadAsync(actor.Value, fileId, http.Request.Body, revision, digest, ct, Authorized));
        });
        group.MapPut("/{fileId:guid}", async (HttpContext http, Guid fileId, SelfHostedSealedFiles service, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct) => {
            var revision = http.Request.Headers.IfMatch.ToString(); if (revision.Length == 0) return Results.Json(new { error = "If-Match is required." }, statusCode: 428);
            var maximumBinaryBytes = EncryptedGraphProfile.MaxFileBytes;
            if (http.Request.HasFormContentType)
            {
                var tenantId = AuthorizationHelpers.GetCallerTenantId(http);
                if (!tenantId.HasValue) return AuthorizationHelpers.Forbidden();
                var sealedOwner = await db.ShSealedFiles.AsNoTracking().AnyAsync(f => f.TenantId == tenantId && f.Id == fileId && !f.IsDeleted && f.IsCurrentVersion, ct);
                var ordinaryOwner = await db.FileRecords.AsNoTracking().AnyAsync(f => f.TenantId == tenantId && f.Id == fileId && !f.IsDeleted && f.IsCurrentVersion && f.EncryptedFieldsJson != null, ct);
                // Ambiguous, missing and foreign owners cannot select a larger unauthenticated spool budget.
                if (sealedOwner == ordinaryOwner || !await AuthorizeFileOwnersAsync(http, null, fileId, db, access, ct)) return AuthorizationHelpers.Forbidden();
                if (ordinaryOwner) maximumBinaryBytes = KnowzArchiveLimits.MaxOrdinaryFileBytes;
            }
            var limit = maximumBinaryBytes + ProtectedFileMultipartReader.MaximumMetadataBytes + 128 * 1024;
            if (http.Request.ContentLength > limit) return Results.Json(new { error = "Encrypted upload is too large." }, statusCode: 413);
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (size is { IsReadOnly: false }) size.MaxRequestBodySize = limit;
            SealedFileWrite? write; Stream? binary = null;
            ProtectedFileMultipart? captured = null;
            if (http.Request.HasFormContentType)
            {
                try { captured = await ProtectedFileMultipartReader.ReadAsync(http.Request, ct, maximumBinaryBytes: maximumBinaryBytes); }
                catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
            }
            await using var ownedMultipart = captured;
            if (captured != null)
            {
                var vault = captured.VaultId;
                if (SealedMaterialGuard.DeclaresPrivateMaterial(captured.Record) || SealedMaterialGuard.DeclaresPrivateMaterial(captured.PayloadKeyReferences))
                    return Results.BadRequest(new { error = "Private material remains local." });
                using var record = StrictJson.Parse(captured.Record, 8 * 1024 * 1024); using var references = StrictJson.Parse(captured.PayloadKeyReferences, 8 * 1024 * 1024);
                if (await db.FileRecords.AnyAsync(f => f.Id == fileId && f.EncryptedFieldsJson != null, ct))
                {
                    var protectedWrite = new ProtectedFileWrite { VaultId = vault, Record = record.RootElement.Deserialize<PortableFileRecord>(Json)!, PayloadKeyReferences = references.RootElement.Deserialize<List<PortablePayloadKeyReference>>(Json)! };
                    if (protectedWrite.Record == null || protectedWrite.PayloadKeyReferences == null || protectedWrite.Record.Id != fileId || SealedMaterialGuard.DeclaresPrivateMaterial(JsonSerializer.Serialize(protectedWrite, Json))) return Results.BadRequest(new { error = "Invalid protected file metadata." });
                    async Task<bool> AuthorizedPlain(CancellationToken token) => await AuthorizeFileOwnersAsync(http, vault, fileId, db, access, token);
                    if (!await AuthorizedPlain(ct)) return AuthorizationHelpers.Forbidden();
                    var result = await service.UpdateProtectedFileAsync(AuthorizationHelpers.GetCallerId(http)!.Value, protectedWrite, revision, captured.Binary, ct, AuthorizedPlain);
                    return result.Ok ? await EncryptedGraphEndpoints.PayloadAsync(vault, "file", result.Id!.Value, db, http.RequestServices.GetRequiredService<SelfHostedSealedStore>(), ct) : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
                }
                write = new() { VaultId = vault, Record = record.RootElement.Deserialize<SealedFileRecord>(Json)!, PayloadKeyReferences = references.RootElement.Deserialize<List<PortablePayloadKeyReference>>(Json)! };
                binary = captured.Binary;
            }
            else
            {
                var body = await EncryptedVaultEndpoints.ReadBodyAsync(http.Request, ct); if (body == null) return Results.BadRequest();
                using var parsed = StrictJson.Parse(body, 8 * 1024 * 1024);
                if (await db.FileRecords.AnyAsync(f => f.Id == fileId && f.EncryptedFieldsJson != null, ct))
                {
                    var protectedWrite = parsed.RootElement.Deserialize<ProtectedFileWrite>(Json);
                    if (protectedWrite?.Record == null || protectedWrite.PayloadKeyReferences == null || protectedWrite.Record.Id != fileId || SealedMaterialGuard.DeclaresPrivateMaterial(body)) return Results.BadRequest(new { error = "Invalid protected file metadata." });
                    async Task<bool> AuthorizedPlainFields(CancellationToken token) => await AuthorizeFileOwnersAsync(http, protectedWrite.VaultId, fileId, db, access, token);
                    if (!await AuthorizedPlainFields(ct)) return AuthorizationHelpers.Forbidden();
                    var result = await service.UpdateProtectedFieldsAsync(protectedWrite, revision, ct, AuthorizedPlainFields);
                    return result.Ok ? await EncryptedGraphEndpoints.PayloadAsync(protectedWrite.VaultId, "file", fileId, db, http.RequestServices.GetRequiredService<SelfHostedSealedStore>(), ct) : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
                }
                write = parsed.RootElement.Deserialize<SealedFileWrite>(Json);
            }
            if (write?.Record == null || write.PayloadKeyReferences == null || write.Attachments == null || write.Record.Id != fileId || SealedMaterialGuard.DeclaresPrivateMaterial(JsonSerializer.Serialize(write, Json))) return Results.BadRequest(new { error = "Invalid encrypted file identity or private material." });
            async Task<bool> Authorized(CancellationToken token) => await AuthorizeFileOwnersAsync(http, write.VaultId, fileId, db, access, token);
            if (!await Authorized(ct)) return AuthorizationHelpers.Forbidden();
            return Result(await service.UpdateAsync(AuthorizationHelpers.GetCallerId(http)!.Value, write, revision, binary, ct, Authorized));
        });
    }
    internal static async Task<bool> AuthorizeFileOwnersAsync(HttpContext http, Guid? vault, Guid fileId, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct, bool requireDelete = false)
    {
        var callerTenantId = AuthorizationHelpers.GetCallerTenantId(http);
        if (!callerTenantId.HasValue) return false;
        if (vault.HasValue && (!await EncryptedGraphEndpoints.AuthorizedAsync(http, vault.Value, db, access, !requireDelete, ct, requireDelete) || !await EncryptedGraphEndpoints.OwnsPayloadAsync(db, vault.Value, "file", fileId, ct))) return false;
        var tenantId = callerTenantId.Value;
        var root = await db.ShSealedFiles.Where(f => f.Id == fileId).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).SingleOrDefaultAsync(ct)
            ?? await db.FileRecords.Where(f => f.Id == fileId).Select(f => (Guid?)(f.RootFileRecordId ?? f.Id)).SingleOrDefaultAsync(ct) ?? fileId;
        var sealedChain = await db.ShSealedFiles.IgnoreQueryFilters().AsNoTracking().Where(f => f.TenantId == tenantId && (f.Id == root || f.RootFileRecordId == root)).ToListAsync(ct);
        var ids = sealedChain.Select(f => f.Id).Concat(await db.FileRecords.IgnoreQueryFilters().Where(f => f.TenantId == tenantId && (f.Id == root || f.RootFileRecordId == root)).Select(f => f.Id).ToListAsync(ct)).Distinct().ToArray();
        var fileVaults = sealedChain.Where(f => f.VaultId.HasValue).Select(f => f.VaultId!.Value).Distinct().ToList();
        foreach (var fileVault in fileVaults)
            if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, fileVault, db, access, !requireDelete, ct, requireDelete)) return false;
        var attachments = await db.ShSealedAttachments.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted && ids.Contains(a.FileRecordId)).ToListAsync(ct);
        var plainLinks = await db.FileAttachments.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.FileRecordId)).ToListAsync(ct);
        var commentIds = attachments.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value).Concat(plainLinks.Where(a => a.CommentId.HasValue).Select(a => a.CommentId!.Value)).Distinct().ToList();
        var plainComments = await db.Comments.IgnoreQueryFilters().Where(c => c.TenantId == tenantId && !c.IsDeleted && commentIds.Contains(c.Id)).Select(c => new { c.Id, c.KnowledgeId }).ToListAsync(ct);
        var sealedComments = await db.ShSealedComments.IgnoreQueryFilters().Where(c => c.TenantId == tenantId && !c.IsDeleted && commentIds.Contains(c.Id)).Select(c => new { c.Id, c.KnowledgeId }).ToListAsync(ct);
        if (plainComments.Select(c => c.Id).Concat(sealedComments.Select(c => c.Id)).Distinct().Count() != commentIds.Count) return false;
        var owners = attachments.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value)
            .Concat(plainLinks.Where(a => a.KnowledgeId.HasValue).Select(a => a.KnowledgeId!.Value))
            .Concat(plainComments.Select(c => c.KnowledgeId)).Concat(sealedComments.Select(c => c.KnowledgeId))
            .Concat(sealedChain.Where(f => f.KnowledgeId.HasValue).Select(f => f.KnowledgeId!.Value)).Distinct().ToList();
        if (!vault.HasValue && owners.Count == 0 && fileVaults.Count == 0) return false;
        foreach (var owner in owners)
        {
            if (!await db.KnowledgeItems.AnyAsync(k => k.Id == owner, ct)) return false;
            var vaults = await db.KnowledgeVaults.Where(v => v.KnowledgeId == owner).Select(v => v.VaultId).ToListAsync(ct);
            if (vaults.Count == 0) return false;
            if (requireDelete)
            {
                foreach (var candidate in vaults) if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, candidate, db, access, false, ct, requireDelete: true)) return false;
            }
            else
            {
                var allowed = false; foreach (var candidate in vaults) if (await EncryptedGraphEndpoints.AuthorizedAsync(http, candidate, db, access, true, ct)) { allowed = true; break; }
                if (!allowed) return false;
            }
        }
        return !attachments.Any(a => a.InboxId.HasValue); // Inbox ownership requires its separate live policy.
    }
    internal static IResult Result(SealedFileResult result) => result.Ok && result.Write != null
        ? Results.Json(new { id = result.Write.Record.Id, ownerId = result.Write.Record.Id, ownerKind = "file", vaultId = result.Write.VaultId, result.Revision, record = result.Write.Record, payloadKeyReferences = result.Write.PayloadKeyReferences, result.Staged }, statusCode: result.Status)
        : Results.Json(new { error = result.Error }, statusCode: result.Status);
}
