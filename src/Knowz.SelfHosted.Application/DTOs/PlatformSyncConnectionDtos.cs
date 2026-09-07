namespace Knowz.SelfHosted.Application.DTOs;

using Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Response DTO for GET /api/v1/sync/connection.
/// NEVER contains the ciphertext or plaintext API key.
/// </summary>
public record PlatformConnectionDto(
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
/// Request body for PUT /api/v1/sync/connection.
/// When <see cref="ApiKey"/> is null or empty the stored ciphertext is preserved
/// (partial update — URL/display name can be changed without re-entering the key).
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
