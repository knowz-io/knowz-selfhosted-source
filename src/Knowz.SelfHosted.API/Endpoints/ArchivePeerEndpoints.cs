using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.API.Middleware;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>Dedicated, authenticated manual peer archive transport. Ordinary archive-copy routes cannot select this identity mode.</summary>
public static class ArchivePeerEndpoints
{
    public sealed record RegisterSavedPeerRequest(string LocalSourceBaseUrl, string? ExpectedBindingRevision);
    public sealed record ManualArchiveTransferRequest(string Direction);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void MapArchivePeerEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/sync");
        routes.MapGet("/links/{linkId:guid}/archive-peer", async (Guid linkId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerTransport transport, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, ct);
            if (actor == null) throw new ArchiveOperationException(403, "An authenticated local user is required.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await transport.GetRegistrationAsync(actor.Value.Id, linkId, ct));
        }));
        routes.MapPost("/links/{linkId:guid}/archive-peer/register", async (Guid linkId, RegisterSavedPeerRequest request, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerTransport transport, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, ct);
            if (actor == null) throw new ArchiveOperationException(403, "An authenticated local user is required.");
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await transport.RegisterAsync(actor.Value.Id, linkId, request.LocalSourceBaseUrl, request.ExpectedBindingRevision, ct));
        }));
        routes.MapPost("/links/{linkId:guid}/archives/transfer", async (Guid linkId, ManualArchiveTransferRequest request, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerTransport transport, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, ct);
            if (actor == null) throw new ArchiveOperationException(403, "An authenticated local user is required.");
            var receipt = await transport.TransferAsync(actor.Value.Id, linkId, request.Direction, ct);
            var binding = await db.ShArchivePeerBindings.AsNoTracking().SingleAsync(p => p.SyncLinkId == linkId, ct);
            http.Response.Headers.CacheControl = "no-store"; return WithProvenance(receipt, binding);
        }));
        routes.MapPost("/vaults/{vaultId:guid}/archive-peers/challenges", async (Guid vaultId, ArchivePeerRegistrationRequest request, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerService peers, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ActorAsync(http, db, ct);
            return Results.Ok(await peers.ChallengeAsync(actor.Id, actor.Credential, vaultId, request, ct));
        }));
        routes.MapPost("/archive-peers/challenges/{challengeId:guid}/accept", async (Guid challengeId, ArchivePeerPossessionProof proof, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerService peers, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ActorAsync(http, db, ct);
            return Results.Ok(await peers.AcceptAsync(actor.Id, actor.Credential, challengeId, proof, ct));
        }));
        routes.MapGet("/archive-peers/{bindingId:guid}", async (Guid bindingId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerService peers, CancellationToken ct) => await Handle(async () => Results.Ok(await BindingAsync(http, db, peers, bindingId, ct))));
        routes.MapPost("/archive-peers/registration-receipt", async (ArchivePeerChallenge challenge, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerService peers, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ActorAsync(http, db, ct); http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await peers.RegistrationReceiptAsync(actor.Id, actor.Credential, challenge, ct));
        }));
        routes.MapGet("/vaults/{vaultId:guid}/archives/identity", async (Guid vaultId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveExportService exporter, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ActorAsync(http, db, ct); await peers.AuthorizeDiscoveryAsync(actor.Id, actor.Credential, vaultId, ct);
            var identity = await exporter.IdentityAsync(ct);
            return Results.Ok(new { tenantId = actor.Tenant, vaultId, identity.SignerFingerprint, identity.SignerPublicKey, capability = "knowz.archive-peer/v1", trustProvenance = "transport-authenticated-tofu" });
        }));
        routes.MapPost("/vaults/{vaultId:guid}/archives/sign-peer-challenge", async (Guid vaultId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveExportService exporter, CancellationToken ct) => await Handle(async () =>
        {
            var actor = await ActorAsync(http, db, ct);
            var body = await EncryptedVaultEndpoints.ReadBodyAsync(http.Request, ct); if (body == null) return Results.BadRequest();
            using var document = StrictJson.Parse(body, 8192);
            var strict = new JsonSerializerOptions(Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            var challenge = document.RootElement.Deserialize<ArchivePeerChallenge>(strict) ?? throw new ArchiveOperationException(400, "A strict peer challenge is required.");
            var proof = await exporter.SignPeerChallengeAsync(vaultId, challenge, token => peers.AuthorizeDiscoveryAsync(actor.Id, actor.Credential, vaultId, token), ct);
            http.Response.Headers.CacheControl = "no-store"; return Results.Ok(proof);
        }));
        routes.MapPost("/archive-peers/{bindingId:guid}/archives/export", async (Guid bindingId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveExportService exporter, CancellationToken ct) => await Handle(async () =>
        {
            var binding = await BindingAsync(http, db, peers, bindingId, ct);
            var result = await exporter.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [binding.DestinationVaultId] }, false, ct);
            try { await BindingAsync(http, db, peers, bindingId, ct); }
            catch { result.Stream.Dispose(); throw; }
            http.Response.Headers["X-Knowz-Source-Receipt"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result.Receipt, Json));
            http.Response.Headers["X-Knowz-Peer-Trust-Provenance"] = binding.TrustProvenance;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Stream(result.Stream, "application/vnd.knowz.archive+zip", $"knowz-{result.Receipt.ArchiveId:D}.knowzarchive");
        }));
        routes.MapPost("/archive-peers/{bindingId:guid}/imports/plan", async (Guid bindingId, HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveImportAdapter adapter,
            [FromServices] ArchiveImportCoordinator coordinator, CancellationToken ct) => await Handle(async () =>
        {
            var binding = await BindingAsync(http, db, peers, bindingId, ct); var peer = adapter.ForPeer(binding);
            if (http.Request.ContentLength > KnowzArchiveLimits.MaxArchiveBytes) return Results.StatusCode(413);
            var maximum = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maximum is { IsReadOnly: false }) maximum.MaxRequestBodySize = KnowzArchiveLimits.MaxArchiveBytes;
            foreach (var option in http.Request.Query)
                if (option.Key != "strategy" || option.Value.Count != 1) throw new ArchiveOperationException(400, "Unknown or duplicate peer import option.");
            var receipt = ReadSourceReceipt(http.Request);
            var operation = OneHeader(http.Request, "Idempotency-Key", 128);
            if (operation.Any(c => c < 0x21 || c > 0x7e)) throw new ArchiveOperationException(400, "Invalid operation identity.");
            var options = new ArchiveImportOptions { TargetVaultId = binding.DestinationVaultId, Strategy = http.Request.Query["strategy"].FirstOrDefault() ?? "overwrite",
                ExpectedSignerFingerprint = binding.SignerFingerprint, SourceReceipt = receipt, IdempotencyKey = $"sync:{binding.Id:D}:{operation}" };
            return WithProvenance(await coordinator.CreatePlanAsync(http.Request.Body, peer.PeerActor, options, peer, ct), binding);
        }));
        routes.MapPost("/archive-peers/{bindingId:guid}/imports/{planId:guid}/commit", async (Guid bindingId, Guid planId, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveImportAdapter adapter,
            [FromServices] ArchiveImportCoordinator coordinator, CancellationToken ct) => await Handle(async () =>
        {
            var binding = await BindingAsync(http, db, peers, bindingId, ct); var peer = adapter.ForPeer(binding);
            return WithProvenance(await peer.GetCommittedReceiptAsync(planId, peer.PeerActor, ct) ?? await coordinator.CommitAsync(planId, peer.PeerActor, peer, ct), binding);
        }));
        routes.MapDelete("/archive-peers/{bindingId:guid}/imports/{planId:guid}", async (Guid bindingId, Guid planId, HttpContext http,
            [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchivePeerService peers, [FromServices] SelfHostedArchiveImportAdapter adapter,
            [FromServices] ArchiveImportCoordinator coordinator, CancellationToken ct) => await Handle(async () =>
        {
            var binding = await BindingAsync(http, db, peers, bindingId, ct); var peer = adapter.ForPeer(binding);
            await coordinator.CancelAsync(planId, peer.PeerActor, peer, ct); return Results.Ok(new { cancelled = true });
        }));
    }
    private static IResult WithProvenance<T>(T value, ShArchivePeerBinding binding)
    {
        var json = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        json["peerTrustProvenance"] = binding.TrustProvenance; json["peerBindingId"] = binding.Id.ToString("D");
        json["peerBindingRevision"] = binding.BindingRevision; return Results.Json(json);
    }
    private static async Task<ShArchivePeerBinding> BindingAsync(HttpContext http, SelfHostedDbContext db, SelfHostedArchivePeerService peers, Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(http, db, ct);
        return await peers.ResolveAsync(actor.Id, actor.Credential, id, OneHeader(http.Request, "X-Knowz-Peer-Binding-Revision", 256), ct);
    }
    private static async Task<(Guid Id, Guid Tenant, string Credential)> ActorAsync(HttpContext http, SelfHostedDbContext db, CancellationToken ct)
    {
        var actor = await ArchivePortabilityEndpoints.ActorAsync(http, db, ct); var key = ApiKeyExtractor.Extract(http);
        if (actor == null || string.IsNullOrEmpty(key) || !key.StartsWith("ksh_", StringComparison.Ordinal))
            throw new ArchiveOperationException(403, "A current scoped user API key is required for peer transport.");
        return (actor.Value.Id, actor.Value.TenantId, ArchivePeerProof.Fingerprint(Encoding.UTF8.GetBytes(key)));
    }
    private static ArchiveSourceReceipt ReadSourceReceipt(HttpRequest request)
    {
        using var json = StrictJson.Parse(Convert.FromBase64String(OneHeader(request, "X-Knowz-Source-Receipt", 16384)), 16384);
        return json.RootElement.Deserialize<ArchiveSourceReceipt>(Json) ?? throw new ArchiveOperationException(400, "Missing source receipt.");
    }
    private static string OneHeader(HttpRequest request, string name, int limit)
    {
        var value = request.Headers[name];
        if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0]) || value[0]!.Length > limit) throw new ArchiveOperationException(400, $"One bounded {name} header is required.");
        return value[0]!;
    }
    private static async Task<IResult> Handle(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or JsonException or KnowzArchiveException or PortableZipSecurityException)
        { return Results.Json(new { error = ex.Message }, statusCode: 400); }
    }
}
