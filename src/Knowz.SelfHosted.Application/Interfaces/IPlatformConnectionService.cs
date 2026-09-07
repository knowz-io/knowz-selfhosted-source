namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Per-tenant platform credential store. Wraps <c>PlatformConnection</c>
/// with DataProtection encryption, masking, and URL validation.
/// </summary>
public interface IPlatformConnectionService
{
    /// <summary>
    /// Returns the current tenant's stored connection with the API key masked.
    /// Returns null if no connection exists.
    /// </summary>
    Task<PlatformConnectionDto?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates or updates the current tenant's connection.
    /// Validates the URL, encrypts the API key with a per-tenant DataProtection purpose.
    /// When <see cref="UpsertPlatformConnectionRequest.ApiKey"/> is null/empty on an
    /// existing row the stored ciphertext is preserved (partial update).
    /// </summary>
    Task<PlatformConnectionDto> UpsertAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Tests the stored connection against the platform /schema endpoint.
    /// Updates LastTestedAt / LastTestStatus / LastTestError on the row.
    /// </summary>
    Task<PlatformConnectionTestResult> TestAsync(
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Tests a candidate URL + API key without persisting. Used by the UI "Test"
    /// button before the user saves the connection.
    /// </summary>
    Task<PlatformConnectionTestResult> TestCandidateAsync(
        string candidateUrl,
        string candidateApiKey,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the current tenant's connection. Throws <see cref="InvalidOperationException"/>
    /// if any <c>VaultSyncLink</c> still references it — the caller must remove links first.
    /// </summary>
    Task DeleteAsync(
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Internal helper for <c>PlatformSyncClient</c>: resolve the stored URL and
    /// decrypt the API key for an outbound call. Returns null if no connection exists.
    /// Throws <see cref="InvalidOperationException"/> with a generic message on decryption
    /// failure (ciphertext corruption / keyring loss) — NEVER surfaces the raw
    /// <c>CryptographicException</c> past the service boundary.
    /// </summary>
    Task<(string PlatformApiUrl, string ApiKeyPlaintext)?> ResolveForOutboundCallAsync(
        CancellationToken ct = default);
}
