using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Knowz.Core.Enums;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.API.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>
/// SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §2.2:
///
/// - <c>GET /api/bootstrap/status</c> (anonymous, rate-limited) → <c>{ready: bool}</c>.
///   Polled by `deploy-selfhosted` and `post-deploy-smoke.sh`. Intentionally
///   minimal shape — no error strings, user counts, or progress details that
///   would leak deploy state to an unauth attacker.
///
/// - <c>POST /api/bootstrap/consume</c> (auth required) → 204 or 404. Marks the
///   KV secret as consumed and schedules delete. Second call returns 404 (tag
///   already set); once deleted, middleware will reject the key (not routed here).
/// </summary>
public static class BootstrapEndpoints
{
    public static void MapBootstrapEndpoints(this WebApplication app)
    {
        app.MapGet("/api/bootstrap/status", async (
                SelfHostedDbContext db,
                CancellationToken ct) =>
            {
                // Cheap liveness signal: exists a SuperAdmin row.
                // IMPORTANT: do not include error reasons, counts, or hints — any
                // detail is a recon vector for unauth attackers.
                var ready = await db.Users
                    .AnyAsync(u => u.Role == UserRole.SuperAdmin, ct);
                return Results.Ok(new { ready });
            })
            .AllowAnonymous()
            .RequireRateLimiting("auth")
            .Produces(200)
            .WithTags("Bootstrap");

        app.MapPost("/api/bootstrap/consume", async (
                HttpContext ctx,
                IConfiguration config,
                TokenCredential tokenCredential,
                CancellationToken ct) =>
            {
                // AuthenticationMiddleware has already validated the caller's key.
                // If we got here, the bootstrap key is still valid. Mark the KV
                // secret consumed + queue delete.
                var kvUri = config["AzureKeyVault:VaultUri"];
                if (string.IsNullOrWhiteSpace(kvUri))
                {
                    return Results.NoContent();
                }

                var client = new SecretClient(new Uri(kvUri), tokenCredential);
                try
                {
                    var existing = await client.GetSecretAsync(
                        BootstrapService.BootstrapSecretName, cancellationToken: ct);
                    var tags = existing.Value.Properties.Tags;
                    if (tags.TryGetValue("consumed", out var consumed) && consumed == "true")
                    {
                        return Results.NotFound();
                    }
                    existing.Value.Properties.Tags["consumed"] = "true";
                    await client.UpdateSecretPropertiesAsync(existing.Value.Properties, ct);
                    // Fire-and-forget delete — no need to await the long-running delete
                    // polling operation. Azure enforces the 24h TTL as a backstop.
                    _ = client.StartDeleteSecretAsync(
                        BootstrapService.BootstrapSecretName, ct);
                    return Results.NoContent();
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return Results.NotFound();
                }
            })
            .RequireAuthorization()
            .Produces(204).Produces(401).Produces(404)
            .WithTags("Bootstrap");
    }
}
