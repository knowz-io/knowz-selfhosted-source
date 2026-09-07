namespace Knowz.SelfHosted.Application.Services;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPlatformConnectionService"/>. Encrypts the API key using a
/// per-tenant DataProtection purpose string, masks it on read, and validates the
/// platform URL both on write and on every outbound call.
/// </summary>
public class PlatformConnectionService : IPlatformConnectionService
{
    // Master purpose — mirrors GitSyncService.cs:42 to reuse the same keyring.
    internal const string MasterPurpose = "Knowz.SelfHosted.PlatformSync";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Generic user-facing messages (V-SEC-13).
    internal const string MsgUnauthorized = "Invalid platform API key";
    internal const string MsgNetworkError = "Network error contacting platform";
    internal const string MsgSchemaIncompatible = "Platform schema is not compatible";
    internal const string MsgUnknownError = "Unknown error";
    internal const string MsgCorruptCiphertext = "Platform credential is invalid or corrupted — please re-enter";

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IUrlValidator _urlValidator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlatformConnectionService> _logger;
    private readonly IPlatformAuditLog? _auditLog;

    public PlatformConnectionService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IDataProtectionProvider dataProtectionProvider,
        IUrlValidator urlValidator,
        IHttpClientFactory httpClientFactory,
        ILogger<PlatformConnectionService> logger,
        IPlatformAuditLog? auditLog = null)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _dataProtectionProvider = dataProtectionProvider;
        _urlValidator = urlValidator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _auditLog = auditLog;
    }

    public async Task<PlatformConnectionDto?> GetAsync(CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var row = await _db.PlatformConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<PlatformConnectionDto> UpsertAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var validation = _urlValidator.ValidatePlatformUrl(request.PlatformApiUrl);
        if (!validation.IsValid)
        {
            await TryLogAuditAsync(
                createdByUserId, null,
                PlatformSyncOperation.Connect,
                PlatformSyncRunStatus.Failed,
                validation.ErrorMessage ?? "Invalid platform URL.",
                ct);
            throw new InvalidOperationException(validation.ErrorMessage ?? "Invalid platform URL.");
        }

        var tenantId = _tenantProvider.TenantId;
        var now = DateTime.UtcNow;

        var existing = await _db.PlatformConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var trimmedUrl = request.PlatformApiUrl.Trim().TrimEnd('/');
        var trimmedDisplayName = request.DisplayName?.Trim();
        if (trimmedDisplayName is { Length: > 100 })
            trimmedDisplayName = trimmedDisplayName.Substring(0, 100);

        if (existing is null)
        {
            // New row — API key is required on create.
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                await TryLogAuditAsync(
                    createdByUserId, null,
                    PlatformSyncOperation.Connect,
                    PlatformSyncRunStatus.Failed,
                    "API key is required when creating a connection.",
                    ct);
                throw new InvalidOperationException("API key is required when creating a connection.");
            }

            var protector = GetProtector(tenantId);
            var row = new PlatformConnection
            {
                TenantId = tenantId,
                PlatformApiUrl = trimmedUrl,
                ApiKeyProtected = protector.Protect(request.ApiKey),
                ApiKeyLast4 = Last4(request.ApiKey),
                DisplayName = trimmedDisplayName,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedByUserId = createdByUserId,
                LastTestStatus = PlatformConnectionTestStatus.Untested
            };
            _db.PlatformConnections.Add(row);
            await _db.SaveChangesAsync(ct);

            // Structured logging — NEVER log the API key itself (V-SEC-03).
            _logger.LogInformation(
                "Created PlatformConnection for TenantId {TenantId}, PlatformApiUrl={PlatformApiUrl}",
                tenantId, trimmedUrl);

            await TryLogAuditAsync(
                createdByUserId, null,
                PlatformSyncOperation.Connect,
                PlatformSyncRunStatus.Succeeded,
                null,
                ct);

            return ToDto(row);
        }

        // Partial update: keep existing ciphertext if caller omitted ApiKey.
        existing.PlatformApiUrl = trimmedUrl;
        existing.DisplayName = trimmedDisplayName;
        existing.UpdatedAt = now;

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            var protector = GetProtector(tenantId);
            existing.ApiKeyProtected = protector.Protect(request.ApiKey);
            existing.ApiKeyLast4 = Last4(request.ApiKey);
            // Resetting test state — the caller should re-test after rotating the key.
            existing.LastTestStatus = PlatformConnectionTestStatus.Untested;
            existing.LastTestError = null;
            existing.LastTestedAt = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated PlatformConnection for TenantId {TenantId}, PlatformApiUrl={PlatformApiUrl}, KeyRotated={KeyRotated}",
            tenantId, trimmedUrl, !string.IsNullOrWhiteSpace(request.ApiKey));

        await TryLogAuditAsync(
            createdByUserId, null,
            PlatformSyncOperation.Connect,
            PlatformSyncRunStatus.Succeeded,
            null,
            ct);

        return ToDto(existing);
    }

    public async Task<PlatformConnectionTestResult> TestAsync(
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var row = await _db.PlatformConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (row is null)
        {
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Untested, "No connection configured.", null, null);
        }

        string plaintext;
        try
        {
            plaintext = GetProtector(tenantId).Unprotect(row.ApiKeyProtected);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for PlatformConnection TenantId {TenantId}", tenantId);
            row.LastTestedAt = DateTime.UtcNow;
            row.LastTestStatus = PlatformConnectionTestStatus.Unauthorized;
            row.LastTestError = MsgCorruptCiphertext;
            await _db.SaveChangesAsync(ct);
            await TryLogAuditAsync(
                userId ?? Guid.Empty, userEmail,
                PlatformSyncOperation.TestConnection,
                PlatformSyncRunStatus.Failed,
                MsgCorruptCiphertext,
                ct);
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Unauthorized, MsgCorruptCiphertext, null, null);
        }

        var result = await ExecuteTestAsync(row.PlatformApiUrl, plaintext, ct);

        row.LastTestedAt = DateTime.UtcNow;
        row.LastTestStatus = result.Status;
        row.LastTestError = result.Status == PlatformConnectionTestStatus.Ok ? null : result.Message;
        if (result.Status == PlatformConnectionTestStatus.Ok && result.RemoteTenantId.HasValue)
        {
            row.RemoteTenantId = result.RemoteTenantId;
        }
        await _db.SaveChangesAsync(ct);

        await TryLogAuditAsync(
            userId ?? Guid.Empty, userEmail,
            PlatformSyncOperation.TestConnection,
            result.Status == PlatformConnectionTestStatus.Ok
                ? PlatformSyncRunStatus.Succeeded
                : PlatformSyncRunStatus.Failed,
            result.Status == PlatformConnectionTestStatus.Ok ? null : result.Message,
            ct);

        return result;
    }

    public async Task<PlatformConnectionTestResult> TestCandidateAsync(
        string candidateUrl,
        string candidateApiKey,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var validation = _urlValidator.ValidatePlatformUrl(candidateUrl);
        if (!validation.IsValid)
        {
            var msg = validation.ErrorMessage ?? "Invalid URL.";
            await TryLogCandidateTestAuditAsync(
                userId ?? Guid.Empty, userEmail,
                PlatformSyncRunStatus.Failed, msg, ct);
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.NetworkError, msg, null, null);
        }

        if (string.IsNullOrWhiteSpace(candidateApiKey))
        {
            await TryLogCandidateTestAuditAsync(
                userId ?? Guid.Empty, userEmail,
                PlatformSyncRunStatus.Failed, MsgUnauthorized, ct);
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Unauthorized, MsgUnauthorized, null, null);
        }

        var result = await ExecuteTestAsync(candidateUrl.Trim().TrimEnd('/'), candidateApiKey, ct);

        await TryLogCandidateTestAuditAsync(
            userId ?? Guid.Empty, userEmail,
            result.Status == PlatformConnectionTestStatus.Ok
                ? PlatformSyncRunStatus.Succeeded
                : PlatformSyncRunStatus.Failed,
            result.Status == PlatformConnectionTestStatus.Ok ? null : result.Message,
            ct);

        return result;
    }

    public async Task DeleteAsync(
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var row = await _db.PlatformConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (row is null) return;

        var linked = await _db.VaultSyncLinks
            .AnyAsync(l => l.PlatformConnectionId == row.Id, ct);
        if (linked)
        {
            const string errorMsg = "Cannot disconnect — sync links still exist. Remove them first.";
            await TryLogAuditAsync(
                userId ?? Guid.Empty, userEmail,
                PlatformSyncOperation.Disconnect,
                PlatformSyncRunStatus.Failed,
                errorMsg,
                ct);
            throw new InvalidOperationException(errorMsg);
        }

        _db.PlatformConnections.Remove(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted PlatformConnection for TenantId {TenantId}", tenantId);

        await TryLogAuditAsync(
            userId ?? Guid.Empty, userEmail,
            PlatformSyncOperation.Disconnect,
            PlatformSyncRunStatus.Succeeded,
            null,
            ct);
    }

    public async Task<(string PlatformApiUrl, string ApiKeyPlaintext)?> ResolveForOutboundCallAsync(
        CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;
        var row = await _db.PlatformConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);
        if (row is null) return null;

        // Defense in depth — re-validate the stored URL before every outbound call (V-SEC-01).
        var validation = _urlValidator.ValidatePlatformUrl(row.PlatformApiUrl);
        if (!validation.IsValid)
        {
            _logger.LogError(
                "Stored PlatformConnection URL failed validation for TenantId {TenantId}: {Error}",
                tenantId, validation.ErrorMessage);
            throw new InvalidOperationException(
                "Stored platform URL is no longer allowed. Please re-enter.");
        }

        try
        {
            var plaintext = GetProtector(tenantId).Unprotect(row.ApiKeyProtected);
            return (row.PlatformApiUrl, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for PlatformConnection TenantId {TenantId}", tenantId);
            throw new InvalidOperationException(MsgCorruptCiphertext);
        }
    }

    // ---------- internals ----------

    /// <summary>
    /// Writes an audit row for a connect/disconnect/test operation. Audit failures never
    /// bubble up to the caller — a broken audit log must not break the primary operation.
    /// </summary>
    private async Task TryLogAuditAsync(
        Guid userId,
        string? userEmail,
        PlatformSyncOperation operation,
        PlatformSyncRunStatus status,
        string? errorMessage,
        CancellationToken ct)
    {
        if (_auditLog is null) return;
        try
        {
            var start = new PlatformSyncRunStart(
                userId,
                userEmail,
                operation,
                PlatformSyncDirection.None);
            await _auditLog.LogAsync(start, status, errorMessage, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to write audit row for PlatformConnection {Operation}", operation);
        }
    }

    /// <summary>
    /// Audit wrapper for candidate (unpersisted) connection tests. The row is tagged via the
    /// UserEmail prefix so history consumers can distinguish it from stored-connection tests.
    /// </summary>
    private Task TryLogCandidateTestAuditAsync(
        Guid userId,
        string? userEmail,
        PlatformSyncRunStatus status,
        string? errorMessage,
        CancellationToken ct)
    {
        // Tag as a candidate test via the UserEmail column — history DTOs surface this verbatim.
        var tagged = string.IsNullOrEmpty(userEmail)
            ? "[candidate-test]"
            : $"[candidate-test] {userEmail}";
        return TryLogAuditAsync(
            userId, tagged,
            PlatformSyncOperation.TestConnection,
            status, errorMessage, ct);
    }

    internal IDataProtector GetProtector(Guid tenantId)
    {
        // Per-tenant sub-protector — cross-tenant ciphertexts cannot be decrypted (V-SEC-02/06).
        return _dataProtectionProvider
            .CreateProtector(MasterPurpose)
            .CreateProtector($"{MasterPurpose}.{tenantId}");
    }

    private async Task<PlatformConnectionTestResult> ExecuteTestAsync(
        string platformUrl, string apiKey, CancellationToken ct)
    {
        HttpClient client;
        try
        {
            client = _httpClientFactory.CreateClient("KnowzPlatformSync");
            client.BaseAddress = new Uri(platformUrl);
            if (client.DefaultRequestHeaders.Contains("X-Api-Key"))
                client.DefaultRequestHeaders.Remove("X-Api-Key");
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to construct HttpClient for platform test");
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.NetworkError, MsgNetworkError, null, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync("/api/v1/sync/schema", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // Treat client timeouts as network errors.
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.NetworkError, MsgNetworkError, null, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error contacting platform during connection test");
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.NetworkError, MsgNetworkError, null, null);
        }

        // 401/403 → generic message (V-SEC-13).
        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Unauthorized, MsgUnauthorized, null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.NetworkError, MsgNetworkError, null, null);
        }

        // Parse schema envelope — unknown fields must NOT leak into the response.
        try
        {
            var envelope = await response.Content
                .ReadFromJsonAsync<PlatformApiResponse<PlatformSchemaResponse>>(JsonOptions, ct);
            var schema = envelope?.Data;
            if (schema is null)
            {
                return new PlatformConnectionTestResult(
                    PlatformConnectionTestStatus.NetworkError, MsgNetworkError, null, null);
            }

            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Ok,
                null,
                schema.TenantId == Guid.Empty ? null : schema.TenantId,
                schema.Version.ToString());
        }
        catch
        {
            // Any parse failure is treated as schema incompatibility — NEVER leak raw body.
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.SchemaIncompatible, MsgSchemaIncompatible, null, null);
        }
    }

    internal static PlatformConnectionDto ToDto(PlatformConnection row)
    {
        var hasKey = !string.IsNullOrEmpty(row.ApiKeyProtected);
        var mask = hasKey && !string.IsNullOrEmpty(row.ApiKeyLast4)
            ? $"ukz_****{row.ApiKeyLast4}"
            : null;
        return new PlatformConnectionDto(
            row.PlatformApiUrl,
            row.DisplayName,
            hasKey,
            mask,
            row.RemoteTenantId,
            row.LastTestedAt,
            row.LastTestStatus,
            row.LastTestError,
            row.UpdatedAt);
    }

    internal static string? Last4(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        return plaintext.Length <= 4 ? plaintext : plaintext.Substring(plaintext.Length - 4);
    }
}
