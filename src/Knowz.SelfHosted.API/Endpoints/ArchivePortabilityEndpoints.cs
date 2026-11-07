using System.Text.Json;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace Knowz.SelfHosted.API.Endpoints;

public static class ArchivePortabilityEndpoints
{
    public sealed class ExportRequest
    {
        public string? ScopeType { get; set; }
        public List<Guid>? VaultIds { get; set; }
        public List<Guid>? KnowledgeItemIds { get; set; }
        public bool AllowPartial { get; set; }
    }
    public static void MapArchivePortabilityEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/portability");
        MapImportRoutes(routes);
        routes.MapGet("/capabilities", async (HttpContext http, [FromServices] SelfHostedDbContext db, CancellationToken ct) =>
        {
            if (await ActorAsync(http, db, ct) == null) return AuthorizationHelpers.Forbidden();
            return Results.Ok(new { archiveSchemaVersion = 4, graphSchemaVersion = 5,
                capabilities = new[] { KnowzArchiveProfile.Capability, EncryptedGraphProfile.Capability, SuspendedReleaseProfile.Capability },
                supportedScopes = new[] { "Tenant", "Vault", "VaultWithChildren", "SingleItem", "MultiSelect" },
                readAlgorithmProfiles = new[] { "x25519-vault", "x25519-item", "platform-managed-v1" },
                clientLocalRecoveryProfiles = new[] { "x25519-vault", "x25519-item" }, writeAlgorithmProfiles = new[] { "x25519-vault" },
                readEnvelopeVersions = new[] { 1, 2 }, newBodyFileEnvelopeVersion = 2, legacyFieldEnvelopeVersion = 1,
                readerArchiveVersions = new[] { 1 }, writerArchiveVersion = 1, readerGraphSchemaVersions = new[] { 5 }, writerGraphSchemaVersion = 5,
                manifestSignatureAlgorithm = "ecdsa-p256-sha256-p1363", outerPasswordFormat = "age-passphrase-client-local",
                custodyModes = new[] { "plaintext", "customer-held-public-only", "customer-held-local", "source-provider-required", "suspended-release" },
                custodySupport = new { customerHeldLocalConsent = true, managedRecovery = false, managedRecoveryRequiresSeparateAuthorizedConversion = true,
                    destinationManagedRewrap = false, dependencyBoundPreservation = true, dependencyBoundIndependentRecovery = false,
                    suspendedReleasePreservation = true, suspendedReleaseActivation = false, ordinaryArchiveContainsPrivateKeys = false },
                limits = new { archiveBytes = KnowzArchiveLimits.MaxArchiveBytes, ordinaryFileBytes = KnowzArchiveLimits.MaxOrdinaryFileBytes, encryptedFileBytes = EncryptedGraphProfile.MaxFileBytes, packageBytes = KnowzArchiveLimits.MaxPackageBytes, metadataBytes = KnowzArchiveLimits.MaxMetadataBytes, entries = KnowzArchiveLimits.MaxEntries,
                    manifestBytes = KnowzArchiveLimits.MaxManifestBytes, signatureBytes = KnowzArchiveLimits.MaxManifestBytes, packages = 128, expansionRatio = KnowzArchiveLimits.MaxExpansionRatio },
                clientLimits = new { passwordArchiveBytes = 128 * 1024 * 1024, encryptedFileCiphertextBytes = EncryptedGraphProfile.MaxFileBytes, newEncryptedFilePlaintextBytes = EncryptedGraphProfile.MaxFileBytes - 16,
                    passwordScryptLogN = 18, maximumPasswordScryptLogN = 18 },
                verification = new { server = "inventory-preservation", clientDecryption = "not-performed", independentSourcePinRequiredForTrustedLineage = true, sourceDeletion = false } });
        });
        routes.MapGet("/archives/identity", async (HttpContext http, [FromServices] SelfHostedDbContext db, [FromServices] SelfHostedArchiveExportService exporter, CancellationToken ct) =>
            await ActorAsync(http, db, ct) == null ? AuthorizationHelpers.Forbidden() : Results.Ok(await exporter.IdentityAsync(ct)));
        routes.MapPost("/archives/export", async (HttpContext http, ExportRequest request, [FromServices] SelfHostedDbContext db, [FromServices] IVaultAccessService access, [FromServices] SelfHostedArchiveExportService exporter, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor == null) return AuthorizationHelpers.Forbidden();
            var selected = request.VaultIds is { Count: > 0 } || request.KnowledgeItemIds is { Count: > 0 };
            if (request.ScopeType == null && selected)
                return Results.BadRequest(new { error = "Explicit scopeType is required with vaultIds or knowledgeItemIds." });
            var scopeType = request.ScopeType ?? "Tenant";
            if (!Enum.TryParse<ExportScopeType>(scopeType, true, out var type) || !Enum.IsDefined(type)
                || !Enum.GetNames<ExportScopeType>().Contains(scopeType, StringComparer.OrdinalIgnoreCase)
                || type == ExportScopeType.Tenant && selected)
                return Results.BadRequest(new { error = "Invalid archive scopeType." });
            var scope = new ExportScope { Type = type, VaultIds = request.VaultIds, KnowledgeItemIds = request.KnowledgeItemIds };
            if (!await CanExportAsync(actor.Value.Id, actor.Value.Role, scope, db, access, ct)) return AuthorizationHelpers.Forbidden();
            try
            {
                var result = await exporter.ExportAsync(scope, request.AllowPartial, ct);
                try
                {
                    // Capture can outlive the original permission check. Recheck outside its snapshot
                    // before publishing either the signed receipt or any archive bytes.
                    db.ChangeTracker.Clear();
                    var currentActor = await ActorAsync(http, db, ct);
                    if (currentActor == null || !await CanExportAsync(currentActor.Value.Id, currentActor.Value.Role, scope, db, access, ct))
                        throw new ArchiveOperationException(403, "Archive export authorization was revoked.");
                    http.Response.Headers["X-Knowz-Source-Receipt"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result.Receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.Stream(result.Stream, "application/vnd.knowz.archive+zip", $"knowz-{result.Receipt.ArchiveId:D}.knowzarchive");
                }
                catch { await result.Stream.DisposeAsync(); throw; }
            }
            catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "The requested archive scope is invalid." }); }
            catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message, preservationState = "blocked" }, statusCode: 422); }
            catch (KnowzArchiveException ex) { return Results.Json(new { error = ex.Message, preservationState = "blocked" }, statusCode: 422); }
        });
    }
    private static void MapImportRoutes(RouteGroupBuilder routes)
    {
        routes.MapPost("/imports/plan", async (HttpContext http, [FromServices] SelfHostedDbContext db,
            [FromServices] ArchiveImportCoordinator coordinator, [FromServices] SelfHostedArchiveImportAdapter adapter, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor == null) return AuthorizationHelpers.Forbidden();
            if (http.Request.ContentLength > KnowzArchiveLimits.MaxArchiveBytes) return Results.StatusCode(413);
            var maximum = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maximum is { IsReadOnly: false }) maximum.MaxRequestBodySize = KnowzArchiveLimits.MaxArchiveBytes;
            try
            {
                var options = ReadOptions(http.Request);
                var plan = await coordinator.CreatePlanAsync(http.Request.Body, new(actor.Value.TenantId, actor.Value.Id.ToString("D")), options, adapter, ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(plan);
            }
            catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or KnowzArchiveException or PortableZipSecurityException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        routes.MapPost("/imports/{planId:guid}/commit", async (HttpContext http, Guid planId, [FromServices] SelfHostedDbContext db,
            [FromServices] ArchiveImportCoordinator coordinator, [FromServices] SelfHostedArchiveImportAdapter adapter, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor == null) return AuthorizationHelpers.Forbidden();
            try
            {
                var identity = new ArchiveActor(actor.Value.TenantId, actor.Value.Id.ToString("D"));
                return Results.Ok(await adapter.GetCommittedReceiptAsync(planId, identity, ct) ?? await coordinator.CommitAsync(planId, identity, adapter, ct));
            }
            catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        });
        routes.MapDelete("/imports/{planId:guid}", async (HttpContext http, Guid planId, [FromServices] SelfHostedDbContext db,
            [FromServices] ArchiveImportCoordinator coordinator, [FromServices] SelfHostedArchiveImportAdapter adapter, CancellationToken ct) =>
        {
            var actor = await ActorAsync(http, db, ct);
            if (actor == null) return AuthorizationHelpers.Forbidden();
            try { await coordinator.CancelAsync(planId, new(actor.Value.TenantId, actor.Value.Id.ToString("D")), adapter, ct); return Results.Ok(new { cancelled = true }); }
            catch (ArchiveOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        });
    }
    private static ArchiveImportOptions ReadOptions(HttpRequest request)
    {
        var allowed = new HashSet<string>(["strategy", "targetVaultId", "vaultMappings", "allowPartial", "expectedSignerFingerprint"], StringComparer.Ordinal);
        foreach (var entry in request.Query)
            if (!allowed.Contains(entry.Key) || entry.Value.Count != 1) throw new ArgumentException("Unknown or duplicate archive import option.");
        var strategy = request.Query["strategy"].FirstOrDefault() ?? "skip";
        var target = request.Query["targetVaultId"].FirstOrDefault();
        var mappings = new Dictionary<Guid, Guid>();
        if (request.Query["vaultMappings"].FirstOrDefault() is string encoded)
        {
            if (encoded.Length > 65536) throw new ArgumentException("Vault mapping exceeds the supported size.");
            using var parsed = StrictJson.Parse(WebEncoders.Base64UrlDecode(encoded), 65536);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Vault mappings must be a JSON object.");
            foreach (var property in parsed.RootElement.EnumerateObject()) mappings.Add(Guid.Parse(property.Name), property.Value.GetGuid());
        }
        var partial = request.Query["allowPartial"].FirstOrDefault();
        if (partial != null && partial is not ("true" or "false")) throw new ArgumentException("allowPartial must be true or false.");
        if (request.Headers["Idempotency-Key"].Count != 1) throw new ArgumentException("One Idempotency-Key header is required.");
        // The receipt is parsed strictly here; the common coordinator compares it before edition staging.
        ArchiveSourceReceipt? receipt = null;
        if (request.Headers["X-Knowz-Source-Receipt"].FirstOrDefault() is string sourceReceipt)
        {
            if (request.Headers["X-Knowz-Source-Receipt"].Count != 1 || sourceReceipt.Length > 16384) throw new ArgumentException("Invalid source receipt header.");
            using var parsed = StrictJson.Parse(Convert.FromBase64String(sourceReceipt), 16384);
            receipt = parsed.RootElement.Deserialize<ArchiveSourceReceipt>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        return new() { Strategy = strategy, TargetVaultId = target == null ? null : Guid.Parse(target), VaultMappings = mappings,
            AllowPartial = partial == "true", ExpectedSignerFingerprint = request.Query["expectedSignerFingerprint"].FirstOrDefault(),
            IdempotencyKey = request.Headers["Idempotency-Key"].ToString(), SourceReceipt = receipt };
    }

    internal static async Task<(Guid Id, Guid TenantId, UserRole Role)?> ActorAsync(HttpContext http, [FromServices] SelfHostedDbContext db, CancellationToken ct)
    {
        var id = AuthorizationHelpers.GetCallerId(http); var tenant = AuthorizationHelpers.GetCallerTenantId(http);
        if (!AuthorizationHelpers.IsAuthenticatedKnowzUser(http) || !id.HasValue || !tenant.HasValue) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
        if (user == null) return null;
        var membership = await db.UserTenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == id && m.TenantId == tenant, ct);
        if (membership != null ? !membership.IsActive : user.TenantId != tenant) return null;
        return (id.Value, tenant.Value, membership?.Role ?? user.Role);
    }
    private static async Task<bool> CanExportAsync(Guid actor, UserRole role, ExportScope scope, SelfHostedDbContext db, IVaultAccessService access, CancellationToken ct)
    {
        if (scope.Type == ExportScopeType.Tenant) return role >= UserRole.Admin;
        var vaults = new HashSet<Guid>(scope.VaultIds ?? []);
        if (scope.Type is ExportScopeType.MultiSelect or ExportScopeType.SingleItem)
        {
            if (scope.KnowledgeItemIds is not { Count: > 0 }) return false;
            foreach (var id in scope.KnowledgeItemIds)
            {
                var linked = await db.KnowledgeVaults.Where(v => v.KnowledgeId == id).Select(v => v.VaultId).ToListAsync(ct);
                if (linked.Count == 0 && role < UserRole.Admin) return false;
                foreach (var vault in linked) vaults.Add(vault);
            }
        }
        else if (vaults.Count == 0) return false;
        if (scope.Type == ExportScopeType.VaultWithChildren)
        {
            var all = await db.Vaults.Select(v => new { v.Id, v.ParentVaultId }).ToListAsync(ct);
            bool added;
            do { added = false; foreach (var vault in all.Where(v => v.ParentVaultId.HasValue && vaults.Contains(v.ParentVaultId.Value))) added |= vaults.Add(vault.Id); } while (added);
        }
        foreach (var vault in vaults) if (!await access.HasVaultAccessAsync(actor, vault, ct: ct)) return false;
        return true;
    }
}
