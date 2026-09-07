namespace Knowz.SelfHosted.Application.Services;

using System.Security.Cryptography;
using Knowz.SelfHosted.Application.Validators;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// The single place that turns a <see cref="VaultSyncLink"/> into an authenticated outbound
/// <see cref="HttpClient"/> for the Knowz platform. Shared by <see cref="PlatformSyncClient"/>
/// (entity sync, browse) and <see cref="FileSyncService"/> (file bytes) so the two lanes cannot
/// drift on credential resolution, URL validation, or the named client.
///
/// Credentials come from the per-tenant <see cref="PlatformConnection"/> row and are decrypted
/// per request through the DataProtection purposes
/// <c>Knowz.SelfHosted.PlatformSync</c> / <c>Knowz.SelfHosted.PlatformSync.{tenantId}</c>.
/// The obsolete <c>VaultSyncLink.ApiKeyEncrypted</c> column (plaintext despite its name) is a
/// migration-window fallback only. The plaintext key is never logged.
/// </summary>
public class PlatformSyncCredentialResolver
{
    /// <summary>Name of the HttpClient registered in Program.cs (30s timeout, 50 MB buffer, no auto-redirect).</summary>
    public const string HttpClientName = "KnowzPlatformSync";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IUrlValidator _urlValidator;
    private readonly SelfHostedDbContext _db;
    private readonly ILogger _logger;

    public PlatformSyncCredentialResolver(
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IUrlValidator urlValidator,
        SelfHostedDbContext db,
        ILogger<PlatformSyncCredentialResolver> logger)
        : this(httpClientFactory, dataProtectionProvider, urlValidator, db, (ILogger)logger)
    {
    }

    internal PlatformSyncCredentialResolver(
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtectionProvider,
        IUrlValidator urlValidator,
        SelfHostedDbContext db,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _dataProtectionProvider = dataProtectionProvider;
        _urlValidator = urlValidator;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Create an authenticated client for the platform behind <paramref name="link"/>.
    /// Re-validates the platform URL on every call (V-SEC-01 defense in depth), uses the
    /// registered <see cref="HttpClientName"/> client, and applies X-Api-Key with
    /// remove-then-add hygiene. <paramref name="timeout"/> overrides the instance timeout only —
    /// the shared 30s default registered in Program.cs is never changed.
    /// </summary>
    public HttpClient CreatePlatformHttpClient(VaultSyncLink link, TimeSpan? timeout = null)
    {
        var (url, apiKey) = ResolveCredentialsForLink(link);

        var validation = _urlValidator.ValidatePlatformUrl(url);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Platform URL is no longer allowed: {validation.ErrorMessage}");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(url.TrimEnd('/'));
        if (client.DefaultRequestHeaders.Contains("X-Api-Key"))
            client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        if (timeout is TimeSpan t)
            client.Timeout = t;
        return client;
    }

    /// <summary>
    /// Resolve URL + plaintext API key: prefer the per-tenant PlatformConnection row, fall back
    /// to the obsolete legacy columns for links not yet migrated.
    /// </summary>
    public (string Url, string ApiKey) ResolveCredentialsForLink(VaultSyncLink link)
    {
        if (link.PlatformConnectionId is Guid connectionId)
        {
            var connection = _db.PlatformConnections
                .AsNoTracking()
                .FirstOrDefault(c => c.Id == connectionId)
                ?? throw new InvalidOperationException(
                    "Platform connection not found. Please reconfigure.");

            string plaintext;
            try
            {
                var protector = _dataProtectionProvider
                    .CreateProtector(PlatformConnectionService.MasterPurpose)
                    .CreateProtector($"{PlatformConnectionService.MasterPurpose}.{connection.TenantId}");
                plaintext = protector.Unprotect(connection.ApiKeyProtected);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(
                    ex,
                    "Decryption failed for PlatformConnection {ConnectionId}",
                    connectionId);
                throw new InvalidOperationException(
                    PlatformConnectionService.MsgCorruptCiphertext);
            }

            return (connection.PlatformApiUrl, plaintext);
        }

#pragma warning disable CS0618 // Legacy fallback — removed in follow-up migration.
        if (string.IsNullOrEmpty(link.ApiKeyEncrypted) || string.IsNullOrEmpty(link.PlatformApiUrl))
        {
            throw new InvalidOperationException(
                "Platform connection not configured for this sync link.");
        }
        return (link.PlatformApiUrl, link.ApiKeyEncrypted);
#pragma warning restore CS0618
    }
}
