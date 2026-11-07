using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>Explicit transient local-model consent. These handlers never save content or queue work.</summary>
public static class LocalAiSessionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record ChallengeRequest(Guid VaultId, string Operation, Guid ContextId, string Fingerprint,
        int Generation, string ProviderPolicyRevision, string InputSha256, string InstructionSha256);
    private sealed record RunRequest(Guid VaultId, Guid ChallengeId, bool Consent, string Content, string Instruction);
    private sealed record CurrentOwner(Guid ActorId, Guid TenantId, Guid ItemId, Guid VaultId,
        string Revision, string StateSha256, string BodyMode, SealedCatalogResponse Catalog,
        List<PortablePayloadKeyReference> References);

    public static void MapLocalAiSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/encrypted/items/{id:guid}/ai-session-policy", async (
            HttpContext http, Guid id, Guid vaultId, SelfHostedDbContext db,
            IVaultAccessService access, SelfHostedSealedStore store, CancellationToken ct) =>
            await GuardAsync(http, async () =>
            {
                var owner = await CaptureAsync(http, db, access, store, id, vaultId, ct);
                var policy = http.RequestServices.GetService<LocalAiSessionService>()?.Policy
                    ?? new LocalAiPolicy(http.RequestServices.GetRequiredService<IConfiguration>()).Read();
                return Results.Ok(new { policy.Supported, policy.Reason, provider = policy.Public,
                    limits = new { inputBytes = LocalAiTransport.MaxInputBytes, instructionBytes = LocalAiTransport.MaxInstructionBytes,
                        outputBytes = LocalAiTransport.MaxOutputBytes, timeoutSeconds = 120 },
                    itemRevision = owner.Revision, bodyMode = owner.BodyMode });
            }));
        app.MapPost("/api/v1/encrypted/items/{id:guid}/ai-session-challenges", async (
            HttpContext http, Guid id, SelfHostedDbContext db, IVaultAccessService access,
            SelfHostedSealedStore store, CancellationToken ct) => await GuardAsync(http, async () =>
            {
                var input = await ReadAsync<ChallengeRequest>(http, ct);
                if (input.Operation != "amend-body") throw new LocalAiException(400, "local-ai-invalid");
                var current = await CaptureAsync(http, db, access, store, id, input.VaultId, ct);
                RequireRevision(http, current.Revision);
                var owner = SelectWriteIdentity(current, input.ContextId, input.Fingerprint, input.Generation);
                var service = GetService(http);
                if (service.Policy.PolicyRevision != input.ProviderPolicyRevision) throw new LocalAiException(409, "local-ai-stale");
                return Results.Ok(service.Issue(owner, input.InputSha256, input.InstructionSha256));
            }));
        app.MapPost("/api/v1/encrypted/items/{id:guid}/ai-sessions", async (
            HttpContext http, Guid id, SelfHostedDbContext db, IVaultAccessService access,
            SelfHostedSealedStore store, CancellationToken ct) => await GuardAsync(http, async () =>
            {
                var input = await ReadAsync<RunRequest>(http, ct);
                var current = await CaptureAsync(http, db, access, store, id, input.VaultId, ct);
                RequireRevision(http, current.Revision);
                var service = GetService(http);
                var binding = service.Read(input.ChallengeId, current.ActorId, current.TenantId).ConsentBinding.Owner;
                if (binding.ItemId != id || binding.VaultId != input.VaultId) throw new LocalAiException(410, "local-ai-consent-unavailable");
                async Task<LocalAiOwner> Authorize(CancellationToken token)
                {
                    var fresh = await CaptureAsync(http, db, access, store, id, input.VaultId, token);
                    RequireRevision(http, fresh.Revision);
                    return SelectWriteIdentity(fresh, binding.ContextId, binding.Fingerprint, binding.Generation);
                }
                var result = await service.RunAsync(input.ChallengeId, current.ActorId, current.TenantId,
                    input.Consent, input.Content, input.Instruction, Authorize, ct);
                return Results.Ok(result);
            }));
    }

    private static LocalAiSessionService GetService(HttpContext http) =>
        http.RequestServices.GetService<LocalAiSessionService>() ?? throw new LocalAiException(409, "local-ai-disabled");

    private static async Task<CurrentOwner> CaptureAsync(HttpContext http, SelfHostedDbContext db,
        IVaultAccessService access, SelfHostedSealedStore store, Guid id, Guid vaultId, CancellationToken ct)
    {
        var actor = AuthorizationHelpers.GetCallerId(http);
        var tenant = AuthorizationHelpers.GetCallerTenantId(http);
        if (!actor.HasValue || !tenant.HasValue || !await EncryptedGraphEndpoints.AuthorizedAsync(http, vaultId, db, access, true, ct))
            throw new LocalAiException(403, "local-ai-denied");
        var row = await db.KnowledgeItems.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id && k.TenantId == tenant && k.PortableRecoveryState == null, ct);
        if (row == null || await SelfHostedInactiveShelf.IsInactiveAsync(db, id, ct)) throw new LocalAiException(403, "local-ai-denied");
        var vaults = await db.KnowledgeVaults.AsNoTracking().Where(v => v.KnowledgeId == id).Select(v => v.VaultId).Distinct().OrderBy(v => v).ToListAsync(ct);
        if (!vaults.Contains(vaultId)) throw new LocalAiException(403, "local-ai-denied");
        foreach (var vault in vaults)
            if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, vault, db, access, true, ct)) throw new LocalAiException(403, "local-ai-denied");
        var sealedItem = await store.GetAsync(id, ct);
        object record = sealedItem?.Envelope ?? (object?)await store.GetPlainItemAsync(id, ct)
            ?? throw new LocalAiException(403, "local-ai-denied");
        var references = (await db.ShPayloadKeyReferences.AsNoTracking().Where(r => r.OwnerKind == "knowledge" && r.OwnerId == id)
            .OrderBy(r => r.Role).ThenBy(r => r.FieldName).ThenBy(r => r.ContextId).ThenBy(r => r.Generation).ToListAsync(ct))
            .Select(PortablePublicProjection.Reference).ToList();
        var catalog = await store.GetCatalogAsync(vaultId, ct) ?? throw new LocalAiException(403, "local-ai-denied");
        var bodyMode = sealedItem == null ? "ordinary" : "encrypted";
        var revision = sealedItem?.Revision ?? SelfHostedSealedStore.PayloadRevision(record);
        var state = JsonSerializer.SerializeToUtf8Bytes(new { record, references, vaults, catalog.Revision, bodyMode }, Json);
        try { return new(actor.Value, tenant.Value, id, vaultId, revision, Convert.ToHexStringLower(SHA256.HashData(state)), bodyMode, catalog, references); }
        finally { CryptographicOperations.ZeroMemory(state); }
    }

    private static LocalAiOwner SelectWriteIdentity(CurrentOwner owner, Guid contextId, string fingerprint, int generation)
    {
        var catalog = owner.Catalog;
        var context = catalog.Contexts.SingleOrDefault(c => c.ContextId == contextId);
        var key = catalog.Keys.SingleOrDefault(k => k.ContextId == contextId && k.Fingerprint == fingerprint && k.Generation == generation);
        var primary = catalog.Links.SingleOrDefault(l => l.VaultId == owner.VaultId && l.IsPrimaryWriteContext);
        if (context?.BindingKind != "vault" || context.AlgorithmProfile != "x25519-vault" || key == null || !catalog.IsEligible(key)
            || primary?.ContextId != contextId || primary.PrimaryWriteFingerprint != fingerprint || primary.PrimaryWriteGeneration != generation)
            throw new LocalAiException(409, "local-ai-stale");
        // Current ciphertext keeps its exact attribution. Only the later explicit client save
        // may create a fresh envelope under the selected eligible original context generation.
        var current = owner.BodyMode == "encrypted" ? owner.References.Where(r => r.Role == "body") : owner.References.Where(r => r.Role == "field");
        if (!current.Any(r => r.ContextId == contextId && r.OriginalBindingId == context.OriginalBindingId))
            throw new LocalAiException(409, "local-ai-stale");
        return new(owner.ActorId, owner.TenantId, owner.ItemId, owner.VaultId, owner.Revision,
            owner.StateSha256, owner.BodyMode, contextId, fingerprint, generation);
    }

    private static void RequireRevision(HttpContext http, string revision)
    {
        var expected = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(expected)) throw new LocalAiException(428, "local-ai-stale");
        if (expected.Trim('"') != revision) throw new LocalAiException(409, "local-ai-stale");
    }
    private static async Task<T> ReadAsync<T>(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength > LocalAiTransport.MaxWireBytes) throw new LocalAiException(413, "local-ai-invalid");
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; byte[]? bytes = null;
        try
        {
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > LocalAiTransport.MaxWireBytes) throw new LocalAiException(413, "local-ai-invalid");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            bytes = buffer.ToArray(); using var document = StrictJson.Parse(bytes, LocalAiTransport.MaxWireBytes);
            return document.RootElement.Deserialize<T>(Json) ?? throw new LocalAiException(400, "local-ai-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk); if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
            if (buffer.TryGetBuffer(out var memory)) CryptographicOperations.ZeroMemory(memory.AsSpan());
        }
    }
    private static async Task<IResult> GuardAsync(HttpContext http, Func<Task<IResult>> operation)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await operation(); }
        catch (LocalAiException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is JsonException or KnowzArchiveException or InvalidDataException)
        { return Results.Json(new { error = "The local AI request is invalid. No change was saved.", code = "local-ai-invalid" }, statusCode: 400); }
    }
}
