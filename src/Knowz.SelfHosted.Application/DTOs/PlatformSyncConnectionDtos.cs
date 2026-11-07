namespace Knowz.SelfHosted.Application.DTOs;

using Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Response DTO for GET /api/v1/sync/connection and /api/v1/sync/connections[/{id}].
/// NEVER contains the ciphertext or plaintext API key.
/// <see cref="Id"/> is additive (ADR_G6_Allowlisted_Peer_Destinations, #1043) — connections are
/// one-of-many per tenant; the legacy singleton routes address the primary (earliest-created) row.
/// </summary>
public record PlatformConnectionDto(
    Guid Id,
    string PlatformApiUrl,
    string? DisplayName,
    bool HasApiKey,
    string? ApiKeyMask,
    Guid? RemoteTenantId,
    DateTime? LastTestedAt,
    PlatformConnectionTestStatus LastTestStatus,
    string? LastTestError,
    DateTime UpdatedAt);

/// <summary>
/// Request body for PUT /api/v1/sync/connection, POST /api/v1/sync/connections and
/// PUT /api/v1/sync/connections/{id}.
/// When <see cref="ApiKey"/> is null or empty on an update the stored ciphertext is preserved
/// (partial update — URL/display name can be changed without re-entering the key).
/// On create the key is required.
/// </summary>
public record UpsertPlatformConnectionRequest(
    string PlatformApiUrl,
    string? DisplayName,
    string? ApiKey);

/// <summary>
/// Request body for POST /api/v1/sync/connection/test-candidate.
/// </summary>
public record TestCandidateConnectionRequest(
    string PlatformApiUrl,
    string ApiKey);

/// <summary>
/// Result of an explicit connection test (stored or candidate).
/// </summary>
public record PlatformConnectionTestResult(
    PlatformConnectionTestStatus Status,
    string? Message,
    Guid? RemoteTenantId,
    string? SchemaVersion);
