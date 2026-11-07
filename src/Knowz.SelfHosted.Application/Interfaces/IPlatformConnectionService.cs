namespace Knowz.SelfHosted.Application.Interfaces;

using Knowz.SelfHosted.Application.DTOs;

/// <summary>
/// Per-tenant platform / peer credential store. Wraps <c>PlatformConnection</c>
/// with DataProtection encryption, masking, and URL validation.
///
/// Since ADR_G6_Allowlisted_Peer_Destinations (#1043) a tenant may hold MANY connections
/// (Knowz Cloud + operator-allowlisted peers). The parameterless legacy members
/// (<see cref="GetAsync"/>, <see cref="UpsertAsync"/>, <see cref="TestAsync"/>,
/// <see cref="DeleteAsync"/>, <see cref="ResolveForOutboundCallAsync(CancellationToken)"/>)
/// address the tenant's PRIMARY connection — the earliest-created row — so the existing
/// Cloud Destinations UI keeps working unchanged. The by-id members are additive.
/// </summary>
public interface IPlatformConnectionService
{
    // ---------- Multi-connection (ADR_G6, #1043) ----------

    /// <summary>All of the current tenant's connections, oldest first, API keys masked.</summary>
    Task<IReadOnlyList<PlatformConnectionDto>> ListAsync(CancellationToken ct = default);

    /// <summary>One connection by id, scoped to the current tenant. Null when absent or foreign.</summary>
    Task<PlatformConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Creates a NEW connection. Validates the URL, requires an API key, and rejects a second
    /// row with the same normalized URL for the tenant (<see cref="InvalidOperationException"/>).
    /// </summary>
    Task<PlatformConnectionDto> CreateAsync(
        UpsertPlatformConnectionRequest request,
        Guid createdByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Updates one connection by id (URL / display name; key only when supplied). Returns null
    /// when the id is absent or belongs to another tenant. Throws on URL validation failure or
    /// when the new URL collides with another connection of the tenant.
    /// </summary>
    Task<PlatformConnectionDto?> UpdateAsync(
        Guid connectionId,
        UpsertPlatformConnectionRequest request,
        Guid updatedByUserId,
        CancellationToken ct = default);

    /// <summary>Tests one stored connection by id against its /api/v1/sync/schema route.</summary>
    Task<PlatformConnectionTestResult> TestByIdAsync(
        Guid connectionId,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes one connection by id. Returns false when absent/foreign. Throws
    /// <see cref="InvalidOperationException"/> if any <c>VaultSyncLink</c> still references it.
    /// </summary>
    Task<bool> DeleteByIdAsync(
        Guid connectionId,
        Guid? userId = null,
        string? userEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve URL + decrypted key for ONE connection by id (tenant-scoped). Null when absent
    /// or foreign. Re-validates the stored URL (V-SEC-01) and never leaks CryptographicException.
    /// </summary>
    Task<(string PlatformApiUrl, string ApiKeyPlaintext)?> ResolveForOutboundCallAsync(
        Guid connectionId,
        CancellationToken ct = default);

    // ---------- Legacy singleton-shaped members (address the PRIMARY connection) ----------

    /// <summary>
    /// Returns the current tenant's PRIMARY (earliest-created) connection with the API key
    /// masked. Returns null if no connection exists.
    /// </summary>
    Task<PlatformConnectionDto?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the tenant's first connection, or updates the PRIMARY one.
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
