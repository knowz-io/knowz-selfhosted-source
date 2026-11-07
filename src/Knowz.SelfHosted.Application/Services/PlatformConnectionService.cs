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
///
/// ADR_G6 (#1043): a tenant may hold MANY connections (Knowz Cloud + allowlisted peers), unique
/// per (TenantId, normalized PlatformApiUrl). The legacy parameterless members address the
/// PRIMARY connection = the earliest-created row, so the Cloud Destinations UI is unchanged.
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

    // ---------- Multi-connection (ADR_G6, #1043) ----------

    public async Task<IReadOnlyList<PlatformConnectionDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await TenantQuery().AsNoTracking().ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<PlatformConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
    {
        var row = await FindRowAsync(connectionId, track: false, ct);
        return row is null ? null : ToDto(row);
    }

    public Task<PlatformConnectionDto> CreateAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        return CreateRowAsync(request, createdByUserId, ct);
    }

    public async Task<PlatformConnectionDto?> UpdateAsync(
        Guid connectionId,
        UpsertPlatformConnectionRequest request,
        Guid updatedByUserId,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var row = await FindRowAsync(connectionId, track: true, ct);
        if (row is null) return null;

        await ValidateUrlOrThrowAsync(request.PlatformApiUrl, updatedByUserId, ct);
        return await ApplyUpdateAsync(row, request, updatedByUserId, ct);
    }

    public async Task<PlatformConnectionTestResult> TestByIdAsync(
        Guid connectionId,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var row = await FindRowAsync(connectionId, track: true, ct);
        if (row is null)
        {
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Untested, "No connection configured.", null, null);
        }
        return await TestRowAsync(row, userId, userEmail, ct);
    }

    public async Task<bool> DeleteByIdAsync(
        Guid connectionId,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var row = await FindRowAsync(connectionId, track: true, ct);
        if (row is null) return false;
        await DeleteRowAsync(row, userId, userEmail, ct);
        return true;
    }

    public async Task<(string PlatformApiUrl, string ApiKeyPlaintext)?> ResolveForOutboundCallAsync(
        Guid connectionId,
        CancellationToken ct = default)
    {
        var row = await FindRowAsync(connectionId, track: false, ct);
        return row is null ? null : ResolveRow(row);
    }

    // ---------- Legacy singleton-shaped members → PRIMARY connection ----------

    public async Task<PlatformConnectionDto?> GetAsync(CancellationToken ct = default)
    {
        var row = await PrimaryAsync(track: false, ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<PlatformConnectionDto> UpsertAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var existing = await PrimaryAsync(track: true, ct);
        if (existing is null)
        {
            // First connection for the tenant — API key is required on create.
            return await CreateRowAsync(request, createdByUserId, ct);
        }

        await ValidateUrlOrThrowAsync(request.PlatformApiUrl, createdByUserId, ct);
        return await ApplyUpdateAsync(existing, request, createdByUserId, ct);
    }

    public async Task<PlatformConnectionTestResult> TestAsync(
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default)
    {
        var row = await PrimaryAsync(track: true, ct);
        if (row is null)
        {
            return new PlatformConnectionTestResult(
                PlatformConnectionTestStatus.Untested, "No connection configured.", null, null);
        }
        return await TestRowAsync(row, userId, userEmail, ct);
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

        var result = await ExecuteTestAsync(NormalizeUrl(candidateUrl), candidateApiKey, ct);

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
        var row = await PrimaryAsync(track: true, ct);
        if (row is null) return;
        await DeleteRowAsync(row, userId, userEmail, ct);
    }

    public async Task<(string PlatformApiUrl, string ApiKeyPlaintext)?> ResolveForOutboundCallAsync(
        CancellationToken ct = default)
    {
        var row = await PrimaryAsync(track: false, ct);
        return row is null ? null : ResolveRow(row);
    }

    // ---------- shared row operations ----------

    /// <summary>Current tenant's connections, oldest first (stable: CreatedAt then Id).</summary>
    private IOrderedQueryable<PlatformConnection> TenantQuery()
    {
        var tenantId = _tenantProvider.TenantId;
        return _db.PlatformConnections
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id);
    }

    private Task<PlatformConnection?> PrimaryAsync(bool track, CancellationToken ct)
    {
        IQueryable<PlatformConnection> q = TenantQuery();
        if (!track) q = q.AsNoTracking();
        return q.FirstOrDefaultAsync(ct);
    }

    private Task<PlatformConnection?> FindRowAsync(Guid connectionId, bool track, CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;
        IQueryable<PlatformConnection> q = _db.PlatformConnections;
        if (!track) q = q.AsNoTracking();
        return q.FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenantId, ct);
    }

    internal static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    private async Task ValidateUrlOrThrowAsync(string url, Guid userId, CancellationToken ct)
    {
        var validation = _urlValidator.ValidatePlatformUrl(url);
        if (validation.IsValid) return;

        var message = validation.ErrorMessage ?? "Invalid platform URL.";
        await TryLogAuditAsync(
            userId, null,
            PlatformSyncOperation.Connect,
            PlatformSyncRunStatus.Failed,
            message,
            ct);
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Rejects a second connection with the same normalized URL for the tenant. The DB carries
    /// a unique (TenantId, PlatformApiUrl) index as backstop; this check gives a readable error.
    /// </summary>
    private async Task EnsureUrlUniqueAsync(string normalizedUrl, Guid? excludingId, Guid userId, CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;
        var clash = await _db.PlatformConnections
            .AsNoTracking()
            .AnyAsync(c => c.TenantId == tenantId
                && c.PlatformApiUrl == normalizedUrl
                && (excludingId == null || c.Id != excludingId), ct);
        if (!clash) return;

        var message = $"A connection to '{normalizedUrl}' already exists for this tenant.";
        await TryLogAuditAsync(
            userId, null,
            PlatformSyncOperation.Connect,
            PlatformSyncRunStatus.Failed,
            message,
            ct);
        throw new InvalidOperationException(message);
    }

    private async Task<PlatformConnectionDto> CreateRowAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct)
    {
        await ValidateUrlOrThrowAsync(request.PlatformApiUrl, createdByUserId, ct);

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

        var tenantId = _tenantProvider.TenantId;
        var now = DateTime.UtcNow;
        var trimmedUrl = NormalizeUrl(request.PlatformApiUrl);
        await EnsureUrlUniqueAsync(trimmedUrl, excludingId: null, createdByUserId, ct);

        var protector = GetProtector(tenantId);
        var row = new PlatformConnection
        {
            TenantId = tenantId,
            PlatformApiUrl = trimmedUrl,
            ApiKeyProtected = protector.Protect(request.ApiKey),
            ApiKeyLast4 = Last4(request.ApiKey),
            DisplayName = TrimDisplayName(request.DisplayName),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = createdByUserId,
            LastTestStatus = PlatformConnectionTestStatus.Untested
        };
        _db.PlatformConnections.Add(row);
        await _db.SaveChangesAsync(ct);

        // Structured logging — NEVER log the API key itself (V-SEC-03).
        _logger.LogInformation(
            "Created PlatformConnection {ConnectionId} for TenantId {TenantId}, PlatformApiUrl={PlatformApiUrl}",
            row.Id, tenantId, trimmedUrl);

        await TryLogAuditAsync(
            createdByUserId, null,
            PlatformSyncOperation.Connect,
            PlatformSyncRunStatus.Succeeded,
            null,
            ct);

        return ToDto(row);
    }

    /// <summary>
    /// Partial update: keep existing ciphertext if caller omitted ApiKey. Caller has already
    /// validated the URL.
    /// </summary>
    private async Task<PlatformConnectionDto> ApplyUpdateAsync(
        PlatformConnection existing,
        UpsertPlatformConnectionRequest request,
        Guid userId,
        CancellationToken ct)
    {
        var trimmedUrl = NormalizeUrl(request.PlatformApiUrl);
        if (!string.Equals(trimmedUrl, existing.PlatformApiUrl, StringComparison.Ordinal))
            await EnsureUrlUniqueAsync(trimmedUrl, excludingId: existing.Id, userId, ct);

        existing.PlatformApiUrl = trimmedUrl;
        existing.DisplayName = TrimDisplayName(request.DisplayName);
        existing.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            var protector = GetProtector(existing.TenantId);
            existing.ApiKeyProtected = protector.Protect(request.ApiKey);
            existing.ApiKeyLast4 = Last4(request.ApiKey);
            // Resetting test state — the caller should re-test after rotating the key.
            existing.LastTestStatus = PlatformConnectionTestStatus.Untested;
            existing.LastTestError = null;
            existing.LastTestedAt = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Updated PlatformConnection {ConnectionId} for TenantId {TenantId}, PlatformApiUrl={PlatformApiUrl}, KeyRotated={KeyRotated}",
            existing.Id, existing.TenantId, trimmedUrl, !string.IsNullOrWhiteSpace(request.ApiKey));

        await TryLogAuditAsync(
            userId, null,
            PlatformSyncOperation.Connect,
            PlatformSyncRunStatus.Succeeded,
            null,
            ct);

        return ToDto(existing);
    }

    private async Task<PlatformConnectionTestResult> TestRowAsync(
        PlatformConnection row,
        Guid? userId,
        string? userEmail,
        CancellationToken ct)
    {
        string plaintext;
        try
        {
            plaintext = GetProtector(row.TenantId).Unprotect(row.ApiKeyProtected);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for PlatformConnection {ConnectionId} TenantId {TenantId}",
                row.Id, row.TenantId);
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

    private async Task DeleteRowAsync(
        PlatformConnection row,
        Guid? userId,
        string? userEmail,
        CancellationToken ct)
    {
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

        _logger.LogInformation("Deleted PlatformConnection {ConnectionId} for TenantId {TenantId}",
            row.Id, row.TenantId);

        await TryLogAuditAsync(
            userId ?? Guid.Empty, userEmail,
            PlatformSyncOperation.Disconnect,
            PlatformSyncRunStatus.Succeeded,
            null,
            ct);
    }

    private (string PlatformApiUrl, string ApiKeyPlaintext) ResolveRow(PlatformConnection row)
    {
        // Defense in depth — re-validate the stored URL before every outbound call (V-SEC-01).
        // Removing a host from Sync:AllowedPlatformHosts disables its stored connections here.
        var validation = _urlValidator.ValidatePlatformUrl(row.PlatformApiUrl);
        if (!validation.IsValid)
        {
            _logger.LogError(
                "Stored PlatformConnection {ConnectionId} URL failed validation for TenantId {TenantId}: {Error}",
                row.Id, row.TenantId, validation.ErrorMessage);
            throw new InvalidOperationException(
                "Stored platform URL is no longer allowed. Please re-enter.");
        }

        try
        {
            var plaintext = GetProtector(row.TenantId).Unprotect(row.ApiKeyProtected);
            return (row.PlatformApiUrl, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for PlatformConnection {ConnectionId} TenantId {TenantId}",
                row.Id, row.TenantId);
            throw new InvalidOperationException(MsgCorruptCiphertext);
        }
    }

    private static string? TrimDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        return trimmed is { Length: > 100 } ? trimmed.Substring(0, 100) : trimmed;
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
        var mask = hasKey ? FormatApiKeyMask(row.ApiKeyLast4) : null;
        return new PlatformConnectionDto(
            row.Id,
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

    /// <summary>
    /// Stores key prefix + last 4 in ApiKeyLast4 (max 8): e.g. ukz_a9F2 / kz_a9F2 / ksh_a9F2.
    /// Legacy rows with only 4 chars default to the ukz_ mask prefix at read time.
    /// </summary>
    internal static string? Last4(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        var last4 = plaintext.Length <= 4 ? plaintext : plaintext.Substring(plaintext.Length - 4);
        var prefix = DetectKeyPrefix(plaintext) ?? "ukz_";
        var combined = prefix + last4;
        return combined.Length <= 8 ? combined : last4;
    }

    internal static string? FormatApiKeyMask(string? storedLast4)
    {
        if (string.IsNullOrEmpty(storedLast4)) return null;
        if (storedLast4.Length <= 4)
            return $"ukz_****{storedLast4}";
        foreach (var prefix in new[] { "ukz_", "ksh_", "kz_" })
        {
            if (storedLast4.StartsWith(prefix, StringComparison.Ordinal)
                && storedLast4.Length == prefix.Length + 4)
            {
                return prefix + "****" + storedLast4.Substring(prefix.Length);
            }
        }
        var suffix = storedLast4.Length <= 4 ? storedLast4 : storedLast4.Substring(storedLast4.Length - 4);
        return $"ukz_****{suffix}";
    }

    internal static string? DetectKeyPrefix(string plaintext)
    {
        if (plaintext.StartsWith("ukz_", StringComparison.Ordinal)) return "ukz_";
        if (plaintext.StartsWith("ksh_", StringComparison.Ordinal)) return "ksh_";
        if (plaintext.StartsWith("kz_", StringComparison.Ordinal)) return "kz_";
        return null;
    }
}
