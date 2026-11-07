using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

public static class EncryptedVaultEndpoints
{
    private const string PrivateMaterialError = "Private key material is not accepted.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static void MapEncryptedVaultEndpoints(this WebApplication app)
    {
        app.MapEncryptedGraphEndpoints();
        app.MapLocalAiSessionEndpoints();
        app.MapEncryptedFileEndpoints();
        var group = app.MapGroup("/api/v1/encrypted").WithTags("EncryptedVault").AddEndpointFilter(async (invocation, next) => {
            var http = invocation.HttpContext;
            try {
                if (!await EncryptedGraphEndpoints.AuthorizeRequestAsync(http, http.RequestServices.GetRequiredService<SelfHostedDbContext>(), http.RequestServices.GetRequiredService<IVaultAccessService>(), http.RequestAborted)) return AuthorizationHelpers.Forbidden();
                return await next(invocation);
            } catch (BadHttpRequestException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
              catch (JsonException) { return Results.BadRequest(new { error = "Invalid encrypted request JSON." }); }
              catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Encrypted record changed; reload before writing." }); }
        });

        group.MapPost("/packages", async (HttpContext context, SelfHostedSealedStore store, CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();
            var json = await ReadBodyAsync(context.Request, ct);
            if (json == null)
                return Results.BadRequest(new { error = "Expected a JSON body." });
            if (SealedMaterialGuard.DeclaresPrivateMaterial(json))
                return Results.BadRequest(new { error = PrivateMaterialError });

            PortableExportPackage? package;
            Guid? vaultId = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("destinationVaultId", out var vaultElement)
                    && vaultElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(vaultElement.GetString(), out var parsed))
                {
                    vaultId = parsed;
                }

                var payload = doc.RootElement.TryGetProperty("package", out var nested)
                    ? nested.GetRawText()
                    : json;
                package = JsonSerializer.Deserialize<PortableExportPackage>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Expected a JSON body." });
            }

            if (package == null)
                return Results.BadRequest(new { error = "Expected a sealed package." });
            if (!vaultId.HasValue || vaultId.Value == Guid.Empty)
                return Results.BadRequest(new { error = "destinationVaultId is required." });

            var result = await store.ImportPackageAsync(package, vaultId, ct);
            return ToResult(result);
        });

        group.MapGet("/vaults/{vaultId:guid}/export", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid vaultId,
            CancellationToken ct) =>
        {
            if (!AuthorizationHelpers.IsAdminOrAbove(context))
                return AuthorizationHelpers.Forbidden();
            var package = await store.ExportVaultAsync(vaultId, ct);
            return package == null
                ? Results.NotFound(new { error = "Vault not found" })
                : Results.Ok(package);
        });

        group.MapPost("/items", async (HttpContext context, SelfHostedSealedStore store, CancellationToken ct) =>
        {
            var write = await ReadWriteAsync(context.Request, ct);
            if (write.Error != null)
                return write.Error;
            if (!write.Write!.VaultId.HasValue || write.Write.VaultId.Value == Guid.Empty)
                return Results.BadRequest(new { error = "vaultId is required." });
            return await ToWriteResultAsync(context, store, await store.CreateAsync(write.Write, ct, context.Request.Headers.IfMatch.FirstOrDefault()), "knowledge", ct);
        });

        group.MapGet("/items/{id:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid id,
            CancellationToken ct) =>
        {
            var item = await store.GetAsync(id, ct);
            return item == null
                ? Results.NotFound(new { error = "Sealed item not found." })
                : Results.Ok(item);
        });

        group.MapPut("/items/{id:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid id,
            CancellationToken ct) =>
        {
            var write = await ReadWriteAsync(context.Request, ct, allowPlain: true);
            if (write.Error != null)
                return write.Error;
            var result = write.Write!.PlainItem == null
                ? await store.UpdateAsync(id, write.Write, ct, context.Request.Headers.IfMatch.FirstOrDefault())
                : await RunOrdinaryOwnerWriteAsync(context, id, token => store.UpdateAsync(id, write.Write, token, context.Request.Headers.IfMatch.FirstOrDefault()), ct);
            return await ToWriteResultAsync(context, store, result, "knowledge", ct);
        });

        group.MapDelete("/items/{id:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid id,
            CancellationToken ct) =>
        {
            var db = context.RequestServices.GetRequiredService<SelfHostedDbContext>();
            var access = context.RequestServices.GetRequiredService<IVaultAccessService>();
            return ToResult(await EncryptedDeleteOperation.RunAsync(context, db, access, "knowledge", id, token => store.DeleteAsync(id, token, context.Request.Headers.IfMatch.FirstOrDefault()), ct));
        });

        group.MapPost("/items/{id:guid}/local-unlock", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid id,
            CancellationToken ct) =>
        {
            var json = await ReadBodyAsync(context.Request, ct);
            if (json == null)
                return Results.BadRequest(new { error = "Expected a JSON body." });
            if (SealedMaterialGuard.DeclaresPrivateMaterial(json))
                return Results.BadRequest(new { error = PrivateMaterialError });
            LocalUnlockAck? ack;
            try
            {
                ack = JsonSerializer.Deserialize<LocalUnlockAck>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Expected a JSON body." });
            }

            if (ack == null)
                return Results.BadRequest(new { error = "Expected a JSON body." });
            return ToResult(await store.RecordLocalUnlockAsync(id, ack, ct));
        });

        group.MapPost("/items/{knowledgeId:guid}/comments", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid knowledgeId,
            CancellationToken ct) =>
        {
            var write = await ReadCommentWriteAsync(context.Request, ct);
            if (write.Error != null)
                return write.Error;
            var result = await RunCommentWriteAsync(context, knowledgeId, token => store.CreateCommentAsync(knowledgeId, write.Write!, token, context.Request.Headers.IfMatch.FirstOrDefault()), ct);
            return await ToWriteResultAsync(context, store, result, "comment", ct);
        });

        group.MapGet("/items/{knowledgeId:guid}/comments", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid knowledgeId,
            CancellationToken ct) =>
        {
            var comments = await store.ListCommentsAsync(knowledgeId, ct);
            return Results.Ok(comments);
        });

        group.MapGet("/comments/{commentId:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid commentId,
            CancellationToken ct) =>
        {
            var comment = await store.GetCommentAsync(commentId, ct);
            return comment == null
                ? Results.NotFound(new { error = "Sealed comment not found." })
                : Results.Ok(comment);
        });

        group.MapPut("/comments/{commentId:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid commentId,
            CancellationToken ct) =>
        {
            var write = await ReadCommentWriteAsync(context.Request, ct);
            if (write.Error != null)
                return write.Error;
            var db = context.RequestServices.GetRequiredService<SelfHostedDbContext>();
            var owner = await db.ShSealedComments.Where(c => c.Id == commentId).Select(c => (Guid?)c.KnowledgeId).SingleOrDefaultAsync(ct);
            if (!owner.HasValue) return Results.NotFound();
            var result = await RunCommentWriteAsync(context, owner.Value, token => store.UpdateCommentAsync(commentId, write.Write!, token, context.Request.Headers.IfMatch.FirstOrDefault()), ct);
            return await ToWriteResultAsync(context, store, result, "comment", ct);
        });

        group.MapDelete("/comments/{commentId:guid}", async (
            HttpContext context,
            SelfHostedSealedStore store,
            Guid commentId,
            CancellationToken ct) =>
        {
            var db = context.RequestServices.GetRequiredService<SelfHostedDbContext>();
            var access = context.RequestServices.GetRequiredService<IVaultAccessService>();
            return ToCommentResult(await EncryptedDeleteOperation.RunAsync(context, db, access, "comment", commentId, token => store.DeleteCommentAsync(commentId, token, context.Request.Headers.IfMatch.FirstOrDefault()), ct));
        });
    }

    internal static async Task<IResult> ToWriteResultAsync(HttpContext http, SelfHostedSealedStore store, SealedStoreResult result, string kind, CancellationToken ct)
    {
        if (!result.Ok || !result.Id.HasValue) return ToResult(result);
        var db = http.RequestServices.GetRequiredService<SelfHostedDbContext>();
        var access = http.RequestServices.GetRequiredService<IVaultAccessService>();
        var knowledgeId = kind == "knowledge" ? result.Id.Value : (await store.GetCommentAsync(result.Id.Value, ct))!.KnowledgeId;
        foreach (var vault in await db.KnowledgeVaults.Where(v => v.KnowledgeId == knowledgeId).Select(v => v.VaultId).ToListAsync(ct))
            if (await EncryptedGraphEndpoints.AuthorizedAsync(http, vault, db, access, false, ct))
                return await EncryptedGraphEndpoints.PayloadAsync(vault, kind, result.Id.Value, db, store, ct, result.StatusCode);
        return AuthorizationHelpers.Forbidden();
    }

    private static Task<SealedStoreResult> RunOrdinaryOwnerWriteAsync(HttpContext http, Guid id, Func<CancellationToken,Task<SealedStoreResult>> write, CancellationToken ct)
        => EncryptedHistoryRestoreOperation.RunAsync(http,http.RequestServices.GetRequiredService<SelfHostedDbContext>(),http.RequestServices.GetRequiredService<IVaultAccessService>(),id,write,ct);

    private static async Task<SealedStoreResult> RunCommentWriteAsync(HttpContext http, Guid id, Func<CancellationToken,Task<SealedStoreResult>> write, CancellationToken ct)
        => await http.RequestServices.GetRequiredService<SelfHostedDbContext>().ShSealedKnowledge.AnyAsync(k => k.KnowledgeId == id,ct)
            ? await write(ct) : await RunOrdinaryOwnerWriteAsync(http,id,write,ct);

    private static IResult ToResult(SealedStoreResult result)
    {
        if (result.Ok)
        {
            return result.StatusCode == 201
                ? Results.Created($"/api/v1/encrypted/items/{result.Id}", new { id = result.Id, written = result.Written })
                : Results.Ok(new { id = result.Id, written = result.Written });
        }

        return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
    }

    private static async Task<(SealedItemWrite? Write, IResult? Error)> ReadWriteAsync(HttpRequest request, CancellationToken ct, bool allowPlain = false)
    {
        var json = await ReadBodyAsync(request, ct);
        if (json == null)
            return (null, Results.BadRequest(new { error = "Expected a JSON body." }));
        if (SealedMaterialGuard.DeclaresPrivateMaterial(json))
            return (null, Results.BadRequest(new { error = PrivateMaterialError }));
        try
        {
            using var document = StrictJson.Parse(json, 8 * 1024 * 1024);
            var write = JsonSerializer.Deserialize<SealedItemWrite>(json, JsonOptions);
            if (document.RootElement.TryGetProperty("record", out var record))
            {
                if (document.RootElement.TryGetProperty("item", out _)) return (null, Results.BadRequest(new { error = "Use exactly one item representation." }));
                if (write != null)
                {
                    if (allowPlain && record.ValueKind == JsonValueKind.Object && record.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind != JsonValueKind.String || !record.TryGetProperty("encryptedFieldsJson",out var fields) || fields.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fields.GetString())
                            || !record.TryGetProperty("originalItemId",out var original) || original.ValueKind != JsonValueKind.String || !original.TryGetGuid(out var originalId) || originalId == Guid.Empty
                            || record.EnumerateObject().Any(p => new[] { "ciphertext","encryptedContent","encryptedDek","wrappedDek","encryptedDekNonce","dekNonce","ephemeralPublicKey","contentNonce","envelopeVersion","encryptionEnvelopeVersion","encryptionKeyRef","cryptoBindingId","cryptoBindingVaultId","isEncrypted","sealedSnapshot","cryptoVersion" }.Contains(p.Name,StringComparer.OrdinalIgnoreCase)))
                            return (null,Results.BadRequest(new { error = "Ordinary field edits require content, exact fields and original identity without an encrypted-body envelope." }));
                        write.PlainItem = record.Deserialize<PortableKnowledge>(JsonOptions);
                    }
                    else write.Item = record.Deserialize<SealedKnowledgeItem>(JsonOptions)!;
                }
            }
            return write?.Item == null
                ? (null, Results.BadRequest(new { error = "Expected a sealed item." }))
                : (write, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(new { error = "Expected a JSON body." }));
        }
    }

    internal static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength == 0)
            return null;
        const int limit = 8 * 1024 * 1024;
        if (request.ContentLength > limit) throw new BadHttpRequestException("Encrypted metadata exceeds 8 MiB.", 413);
        using var data = new MemoryStream(); var buffer = new byte[81920];
        while (true) { var n = await request.Body.ReadAsync(buffer, ct); if (n == 0) break; if (data.Length + n > limit) throw new BadHttpRequestException("Encrypted metadata exceeds 8 MiB.", 413); data.Write(buffer, 0, n); }
        var json = System.Text.Encoding.UTF8.GetString(data.GetBuffer(), 0, checked((int)data.Length));
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    private static IResult ToCommentResult(SealedStoreResult result)
    {
        if (result.Ok)
        {
            return result.StatusCode == 201
                ? Results.Created($"/api/v1/encrypted/comments/{result.Id}", new { id = result.Id, written = result.Written })
                : Results.Ok(new { id = result.Id, written = result.Written });
        }

        return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
    }

    private static async Task<(SealedCommentWrite? Write, IResult? Error)> ReadCommentWriteAsync(HttpRequest request, CancellationToken ct)
    {
        var json = await ReadBodyAsync(request, ct);
        if (json == null)
            return (null, Results.BadRequest(new { error = "Expected a JSON body." }));
        if (SealedMaterialGuard.DeclaresPrivateMaterial(json))
            return (null, Results.BadRequest(new { error = PrivateMaterialError }));
        try
        {
            using var document = StrictJson.Parse(json, 8 * 1024 * 1024);
            var write = JsonSerializer.Deserialize<SealedCommentWrite>(json, JsonOptions);
            if (document.RootElement.TryGetProperty("record", out var record))
            {
                if (document.RootElement.TryGetProperty("comment", out _)) return (null, Results.BadRequest(new { error = "Use exactly one comment representation." }));
                if (write != null) write.Comment = record.Deserialize<SealedCommentRecord>(JsonOptions)!;
            }
            return write?.Comment == null
                ? (null, Results.BadRequest(new { error = "Expected a sealed comment." }))
                : (write, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(new { error = "Expected a JSON body." }));
        }
    }
}
