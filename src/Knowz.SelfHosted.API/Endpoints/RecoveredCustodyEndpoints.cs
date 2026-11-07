using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;

namespace Knowz.SelfHosted.API.Endpoints;

public static class RecoveredCustodyEndpoints
{
    public static void MapRecoveredCustodyEndpoints(this WebApplication app)
    {
        // The public host uses AuthenticationMiddleware; ExecuteAsync and the service
        // enforce fresh actor, tenant, vault-management and exact imported-key authority.
        var group = app.MapGroup("/api/v1/vaults/{vaultId:guid}/encryption/contexts/{contextId:guid}");
        group.MapPost("/custody-challenges", (Guid vaultId, Guid contextId, HttpContext http, [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedRecoveredCustodyService service)
            => ExecuteAsync<ShCustodyChallengeRequest>(http, db, ["planId", "generation", "fingerprint"],
                async (tenant, actor, request) => (object)await service.CreateChallengeAsync(tenant, vaultId, contextId, actor, request, http.RequestAborted)));
        group.MapPost("/recovered-custody", (Guid vaultId, Guid contextId, HttpContext http, [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedRecoveredCustodyService service)
            => ExecuteAsync<ShCustodyProofRequest>(http, db, ["challengeId", "proof"],
                async (tenant, actor, request) => (object)await service.AcceptAsync(tenant, vaultId, contextId, actor, request, http.RequestAborted)));
    }
    private static async Task<IResult> ExecuteAsync<T>(HttpContext http, SelfHostedDbContext db, string[] fields, Func<Guid, Guid, T, Task<object>> run) where T : class
    {
        var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, http.RequestAborted);
        if (actor == null) return AuthorizationHelpers.Forbidden();
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Pragma = "no-cache";
        if (http.Request.ContentLength > 4096) return Results.Problem("Custody request exceeds its supported bound.", statusCode: 413);
        try
        {
            var buffer = new byte[4097]; var length = 0;
            while (length < buffer.Length) { var count = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted); if (count == 0) break; length += count; }
            if (length > 4096) return Results.Problem("Custody request exceeds its supported bound.", statusCode: 413);
            using var document = StrictJson.Parse(buffer.AsSpan(0, length).ToArray(), 4096); var expected = fields.ToHashSet(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => !expected.Remove(p.Name)) || expected.Count != 0) return Results.BadRequest(new { error = "Invalid custody request fields." });
            var request = document.RootElement.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            return Results.Ok(await run(actor.Value.TenantId, actor.Value.Id, request));
        }
        catch (UnauthorizedAccessException) { return AuthorizationHelpers.Forbidden(); }
        catch (ArchiveOperationException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { return Results.BadRequest(new { error = "Invalid custody proof or request." }); }
    }
}
