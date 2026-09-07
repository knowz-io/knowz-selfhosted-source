namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>
/// Per-tenant platform credential store — one row per selfhosted tenant.
/// Holds the encrypted platform API key and base URL used by all sync operations.
/// Replaces the per-link API key duplication on <see cref="VaultSyncLink"/>.
/// Security: the ApiKeyProtected column is ciphertext produced by
/// <c>IDataProtectionProvider.CreateProtector("Knowz.SelfHosted.PlatformSync").CreateProtector($"Knowz.SelfHosted.PlatformSync.{tenantId}")</c>.
/// </summary>
public class PlatformConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Owning tenant. Unique index: one PlatformConnection per tenant.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Validated https:// URL of the platform API (e.g., "https://api.knowz.io").
    /// </summary>
    public string PlatformApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// DataProtection ciphertext of the platform API key. NEVER logged, NEVER returned in API responses.
    /// </summary>
    public string ApiKeyProtected { get; set; } = string.Empty;

    /// <summary>
    /// Last 4 plaintext characters of the API key, captured at upsert time, used for the
    /// masked "ukz_****LAST4" display. Populated from plaintext, NOT re-derived from ciphertext.
    /// </summary>
    public string? ApiKeyLast4 { get; set; }

    /// <summary>
    /// Optional user-supplied label for the connection. Server-trims to 100 chars.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Remote tenant ID discovered from the platform /schema response on first successful test.
    /// </summary>
    public Guid? RemoteTenantId { get; set; }

    /// <summary>
    /// Timestamp of the last TestAsync invocation (regardless of outcome).
    /// </summary>
    public DateTime? LastTestedAt { get; set; }

    /// <summary>
    /// Outcome of the last test.
    /// </summary>
    public PlatformConnectionTestStatus LastTestStatus { get; set; } = PlatformConnectionTestStatus.Untested;

    /// <summary>
    /// Sanitized error message (never raw platform response bodies).
    /// </summary>
    public string? LastTestError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
}

/// <summary>
/// Result of the most recent connection test. Values are persisted to SQL.
/// </summary>
public enum PlatformConnectionTestStatus
{
    Untested = 0,
    Ok = 1,
    Unauthorized = 2,
    NetworkError = 3,
    SchemaIncompatible = 4
}
