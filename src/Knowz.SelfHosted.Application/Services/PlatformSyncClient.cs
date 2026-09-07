namespace Knowz.SelfHosted.Application.Services;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// HTTP client for communicating with the Knowz platform sync API.
/// All endpoints are under /api/v1/sync/ and authenticated via X-Api-Key header.
/// Credentials are resolved from the per-tenant <see cref="PlatformConnection"/> row
/// and decrypted per-request via the shared DataProtection keyring.
/// </summary>
public class PlatformSyncClient : IPlatformSyncClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IUrlValidator _urlValidator;
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<PlatformSyncClient> _logger;
    private readonly PlatformSyncCredentialResolver _credentials;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // V-SEC-12: Strip unknown fields from untrusted platform responses.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public PlatformSyncClient(
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IUrlValidator urlValidator,
        SelfHostedDbContext db,
        ILogger<PlatformSyncClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dataProtectionProvider = dataProtectionProvider;
        _urlValidator = urlValidator;
        _db = db;
        _logger = logger;
        _credentials = new PlatformSyncCredentialResolver(
            httpClientFactory, dataProtectionProvider, urlValidator, db, logger);
    }

    public async Task<PlatformSchemaResponse> GetSchemaAsync(VaultSyncLink link, CancellationToken ct = default)
    {
        using var client = CreateClient(link);
        var response = await client.GetAsync("/api/v1/sync/schema", ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<PlatformApiResponse<PlatformSchemaResponse>>(JsonOptions, ct);
        return envelope?.Data ?? throw new InvalidOperationException("Platform returned null schema response");
    }

    public async Task RegisterPartnerAsync(VaultSyncLink link, string partnerName, CancellationToken ct = default)
    {
        using var client = CreateClient(link);
        var body = new { PartnerName = partnerName };
        var response = await client.PostAsJsonAsync(
            $"/api/v1/sync/vaults/{link.RemoteVaultId}/register", body, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Registered sync partner for vault {RemoteVaultId}", link.RemoteVaultId);
    }

    public async Task<PortableExportPackage> ExportDeltaAsync(
        VaultSyncLink link, DateTime? since, int page = 1, int pageSize = 500,
        CancellationToken ct = default)
    {
        using var client = CreateClient(link);

        var url = $"/api/v1/sync/vaults/{link.RemoteVaultId}/export?page={page}&pageSize={pageSize}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";

        _logger.LogInformation(
            "Pulling delta from platform vault {RemoteVaultId}, since={Since}, page={Page}",
            link.RemoteVaultId, since, page);

        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<PlatformApiResponse<PortableExportPackage>>(JsonOptions, ct);
        return envelope?.Data ?? throw new InvalidOperationException("Platform returned null export package");
    }

    public async Task<PlatformImportResponse> ImportDeltaAsync(
        VaultSyncLink link, PortableExportPackage package,
        CancellationToken ct = default)
    {
        using var client = CreateClient(link);

        _logger.LogInformation(
            "Pushing delta to platform vault {RemoteVaultId}, {KnowledgeCount} knowledge items",
            link.RemoteVaultId, package.Metadata.TotalKnowledgeItems);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sync/vaults/{link.RemoteVaultId}/import", package, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<PlatformApiResponse<PlatformImportResponse>>(JsonOptions, ct);
        return envelope?.Data ?? throw new InvalidOperationException("Platform returned null import response");
    }

    public async Task<PortableExportPackage?> ExportItemAsync(
        VaultSyncLink link, Guid remoteKnowledgeId, CancellationToken ct = default)
    {
        using var client = CreateClient(link);

        var url = $"/api/v1/sync/vaults/{link.RemoteVaultId}/items/{remoteKnowledgeId}/export";
        _logger.LogInformation(
            "Pulling single item from platform vault {RemoteVaultId}, knowledgeId={KnowledgeId}",
            link.RemoteVaultId, remoteKnowledgeId);

        var response = await client.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<PlatformApiResponse<PortableExportPackage>>(JsonOptions, ct);
        var package = envelope?.Data
            ?? throw new InvalidOperationException("Platform returned invalid data");

        // V-SEC-12: ReadFromJsonAsync would have parsed missing/malformed Guids as Guid.Empty.
        if (package.Data.KnowledgeItems.Any(k => k.Id == Guid.Empty))
            throw new InvalidOperationException("Platform returned invalid data");

        return package;
    }

    /// <summary>
    /// Entity-lane client. Shared with <see cref="FileSyncService"/> through
    /// <see cref="PlatformSyncCredentialResolver"/> (NodeID FIX_SelfHostedFileSyncAuth) so both lanes
    /// resolve credentials, re-validate the URL (V-SEC-01), and pick the named client identically.
    /// </summary>
    private HttpClient CreateClient(VaultSyncLink link) =>
        _credentials.CreatePlatformHttpClient(link);

    // --- Read-only browse proxy (NodeID: PlatformBrowsing) ---

    public async Task<PlatformVaultListDto> ListPlatformVaultsAsync(
        string platformApiUrl, string apiKey, CancellationToken ct = default)
    {
        using var client = CreateBrowseClient(platformApiUrl, apiKey);
        var response = await SendBrowseRequestAsync(
            client, HttpMethod.Get, "/api/v1/vaults", ct);

        var envelope = await DeserializeBrowseEnvelopeAsync<List<PlatformVaultWire>>(response, ct);
        var rawVaults = envelope?.Data ?? new List<PlatformVaultWire>();

        var safe = new List<PlatformVaultDto>(rawVaults.Count);
        foreach (var raw in rawVaults)
        {
            // V-SEC-12: Validate every ID coming from the untrusted platform response.
            if (!TryParsePlatformGuid(raw.Id, out var id))
            {
                _logger.LogWarning(
                    "Rejecting platform vault with invalid Id; skipping entry.");
                continue;
            }

            safe.Add(new PlatformVaultDto(
                Id: id,
                Name: SanitizeBrowseString(raw.Name) ?? string.Empty,
                Description: SanitizeBrowseString(raw.Description),
                KnowledgeCount: raw.KnowledgeCount < 0 ? 0 : raw.KnowledgeCount,
                UpdatedAt: raw.UpdatedAt));
        }

        return new PlatformVaultListDto(safe, safe.Count);
    }

    public async Task<PlatformKnowledgeListDto> ListPlatformKnowledgeAsync(
        string platformApiUrl, string apiKey,
        Guid vaultId, int page, int pageSize,
        string? search = null,
        CancellationToken ct = default)
    {
        using var client = CreateBrowseClient(platformApiUrl, apiKey);

        // Page/pageSize are already validated by the endpoint — belt + braces here.
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize is < 1 or > 100 ? 25 : pageSize;

        var path = $"/api/v1/vaults/{vaultId}/knowledge?page={safePage}&pageSize={safePageSize}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Cap at 200 chars to keep the outbound URL small and mitigate log-volume abuse.
            var trimmed = search.Trim();
            if (trimmed.Length > 200) trimmed = trimmed.Substring(0, 200);
            path += $"&search={Uri.EscapeDataString(trimmed)}";
        }
        var response = await SendBrowseRequestAsync(client, HttpMethod.Get, path, ct);

        var envelope = await DeserializeBrowseEnvelopeAsync<List<PlatformKnowledgeWire>>(response, ct);
        var rawItems = envelope?.Data ?? new List<PlatformKnowledgeWire>();

        var safeItems = new List<PlatformKnowledgeSummaryDto>(rawItems.Count);
        foreach (var raw in rawItems)
        {
            if (!TryParsePlatformGuid(raw.Id, out var id))
            {
                _logger.LogWarning(
                    "Rejecting platform knowledge item with invalid Id in vault {VaultId}; skipping entry.",
                    vaultId);
                continue;
            }

            safeItems.Add(new PlatformKnowledgeSummaryDto(
                Id: id,
                Title: SanitizeBrowseString(raw.Title) ?? string.Empty,
                Summary: SanitizeBrowseString(raw.Summary ?? raw.AiSummary),
                UpdatedAt: raw.UpdatedAt,
                CreatedBy: SanitizeBrowseString(raw.CreatedByUserName)));
        }

        return new PlatformKnowledgeListDto(
            Items: safeItems,
            Page: safePage,
            PageSize: safePageSize,
            TotalCount: safeItems.Count);
    }

    public async Task<PlatformKnowledgeDetailDto> GetPlatformKnowledgeAsync(
        string platformApiUrl, string apiKey,
        Guid knowledgeId, CancellationToken ct = default)
    {
        using var client = CreateBrowseClient(platformApiUrl, apiKey);
        var response = await SendBrowseRequestAsync(
            client, HttpMethod.Get, $"/api/v1/knowledge/{knowledgeId}", ct);

        var envelope = await DeserializeBrowseEnvelopeAsync<PlatformKnowledgeDetailWire>(response, ct);
        var raw = envelope?.Data;

        if (raw is null || !TryParsePlatformGuid(raw.Id, out var id))
        {
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.NotFound,
                "Knowledge item not found on platform");
        }

        return new PlatformKnowledgeDetailDto(
            Id: id,
            Title: SanitizeBrowseString(raw.Title) ?? string.Empty,
            Content: TruncatePreview(SanitizeBrowseString(raw.Content)),
            Summary: SanitizeBrowseString(raw.Summary ?? raw.AiSummary),
            Tags: SanitizeBrowseString(raw.Tags),
            UpdatedAt: raw.UpdatedAt,
            CreatedBy: SanitizeBrowseString(raw.CreatedByUserName));
    }

    /// <summary>
    /// Cap preview content at 2000 chars so the browse panel never pulls megabytes of body
    /// text from the platform. Preview is read-only and never fed back into sync.
    /// </summary>
    private static string? TruncatePreview(string? value)
    {
        const int MaxPreviewLength = 2000;
        if (value is null) return null;
        return value.Length <= MaxPreviewLength
            ? value
            : value.Substring(0, MaxPreviewLength) + "... (truncated)";
    }

    private HttpClient CreateBrowseClient(string platformApiUrl, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(platformApiUrl))
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.NotConfigured,
                "Platform connection not configured");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.NotConfigured,
                "Platform connection not configured");

        var client = _httpClientFactory.CreateClient("KnowzPlatformSync");
        client.BaseAddress = new Uri(platformApiUrl.TrimEnd('/'));
        if (client.DefaultRequestHeaders.Contains("X-Api-Key"))
            client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private async Task<HttpResponseMessage> SendBrowseRequestAsync(
        HttpClient client, HttpMethod method, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(method, path);
            response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Platform browse request timed out for path {Path}", path);
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.Unreachable,
                "Failed to fetch from platform");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Platform browse request failed for path {Path}", path);
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.Unreachable,
                "Failed to fetch from platform");
        }

        if (response.IsSuccessStatusCode)
            return response;

        // V-SEC-13: never echo platform response body. Log status, wrap as sanitized error.
        _logger.LogWarning(
            "Platform browse request returned {StatusCode} for path {Path}",
            (int)response.StatusCode, path);

        var statusCode = response.StatusCode;
        response.Dispose();

        var kind = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => PlatformBrowseErrorKind.Unauthorized,
            HttpStatusCode.NotFound => PlatformBrowseErrorKind.NotFound,
            _ => PlatformBrowseErrorKind.Unreachable
        };
        var message = kind switch
        {
            PlatformBrowseErrorKind.Unauthorized => "Invalid platform API key",
            PlatformBrowseErrorKind.NotFound => "Resource not found on platform",
            _ => "Failed to fetch from platform"
        };
        throw new PlatformBrowseException(kind, message);
    }

    private static async Task<PlatformBrowseEnvelope<T>?> DeserializeBrowseEnvelopeAsync<T>(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<PlatformBrowseEnvelope<T>>(
                stream, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new PlatformBrowseException(
                PlatformBrowseErrorKind.Unreachable,
                "Failed to fetch from platform",
                ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static bool TryParsePlatformGuid(string? value, out Guid id)
    {
        if (!string.IsNullOrEmpty(value) && Guid.TryParse(value, out id))
            return true;
        id = Guid.Empty;
        return false;
    }

    private static string? SanitizeBrowseString(string? value)
    {
        if (value is null) return null;
        // Cap length to prevent a malicious platform response from blowing up the UI.
        const int MaxLength = 100_000;
        return value.Length > MaxLength ? value.Substring(0, MaxLength) : value;
    }

    // Typed wire DTOs used only for deserializing the untrusted platform response.
    // Unknown fields are silently dropped by JsonOptions.UnmappedMemberHandling.

    private sealed class PlatformBrowseEnvelope<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public string? Message { get; set; }
    }

    private sealed class PlatformVaultWire
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int KnowledgeCount { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class PlatformKnowledgeWire
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
        public string? AiSummary { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedByUserName { get; set; }
    }

    private sealed class PlatformKnowledgeDetailWire
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public string? Summary { get; set; }
        public string? AiSummary { get; set; }
        public string? Tags { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedByUserName { get; set; }
    }
}

/// <summary>
/// Classified error kinds for the platform browse proxy. Used to map HTTP status
/// codes to sanitized selfhosted responses without leaking the raw platform body.
/// </summary>
public enum PlatformBrowseErrorKind
{
    NotConfigured,
    Unauthorized,
    NotFound,
    Unreachable,
    InvalidRequest
}

/// <summary>
/// Sanitized exception thrown by the platform browse proxy. Callers translate this
/// into HTTP status codes at the endpoint boundary.
/// </summary>
public sealed class PlatformBrowseException : Exception
{
    public PlatformBrowseErrorKind Kind { get; }

    public PlatformBrowseException(PlatformBrowseErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public PlatformBrowseException(PlatformBrowseErrorKind kind, string message, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
    }
}
