namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// HTTP client for communicating with the Knowz platform sync API.
/// </summary>
public interface IPlatformSyncClient
{
    /// <summary>
    /// Check platform schema compatibility.
    /// </summary>
    Task<PlatformSchemaResponse> GetSchemaAsync(VaultSyncLink link, CancellationToken ct = default);

    /// <summary>
    /// Register this selfhosted instance as a sync partner.
    /// </summary>
    Task RegisterPartnerAsync(VaultSyncLink link, string partnerName, CancellationToken ct = default);

    /// <summary>
    /// Export a delta from the platform vault (entities changed since cursor).
    /// </summary>
    Task<PortableExportPackage> ExportDeltaAsync(
        VaultSyncLink link, DateTime? since, int page = 1, int pageSize = 500,
        CancellationToken ct = default);

    /// <summary>
    /// Import a delta package into the platform vault.
    /// </summary>
    Task<PlatformImportResponse> ImportDeltaAsync(
        VaultSyncLink link, PortableExportPackage package,
        CancellationToken ct = default);

    /// <summary>
    /// Read-only proxy: list all vaults visible to the configured platform API key.
    /// Thin DTO — platform response is validated and unknown fields stripped (V-SEC-12).
    /// </summary>
    Task<PlatformVaultListDto> ListPlatformVaultsAsync(
        string platformApiUrl, string apiKey, CancellationToken ct = default);

    /// <summary>
    /// Read-only proxy: list knowledge items in a remote platform vault.
    /// <paramref name="search"/> is optional; when provided it is capped at 200 characters and
    /// forwarded as a <c>?search=</c> query parameter to the platform list endpoint.
    /// </summary>
    Task<PlatformKnowledgeListDto> ListPlatformKnowledgeAsync(
        string platformApiUrl, string apiKey,
        Guid vaultId, int page, int pageSize,
        string? search = null,
        CancellationToken ct = default);

    /// <summary>
    /// Read-only proxy: fetch a single knowledge item (title + content + metadata) from the platform.
    /// </summary>
    Task<PlatformKnowledgeDetailDto> GetPlatformKnowledgeAsync(
        string platformApiUrl, string apiKey,
        Guid knowledgeId, CancellationToken ct = default);

    /// <summary>
    /// Export a single knowledge item from the platform vault as a portable package
    /// containing exactly one primary entity. Used by single-item pull (V-SEC-09/11/12).
    /// Returns null when the platform returns 404.
    /// </summary>
    Task<PortableExportPackage?> ExportItemAsync(
        VaultSyncLink link, Guid remoteKnowledgeId,
        CancellationToken ct = default);
}
