namespace Knowz.SelfHosted.Application.Validators;

/// <summary>
/// Validates platform URLs before they are stored or used for outbound calls.
/// Enforces HTTPS, allowlist, and SSRF guards (no private IPs, no metadata endpoints).
/// Called at config-write (PlatformConnectionService.UpsertAsync) AND on every outbound
/// call (PlatformSyncCredentialResolver.CreatePlatformHttpClient, shared by PlatformSyncClient and
/// FileSyncService) — two-layer defense per V-SEC-01.
/// </summary>
public interface IUrlValidator
{
    UrlValidationResult ValidatePlatformUrl(string url);
}

/// <summary>
/// Result of a URL validation check. Success carries a null error; failure carries
/// a safe-to-display error message plus a machine-readable error code.
/// </summary>
public record UrlValidationResult(
    bool IsValid,
    string? ErrorMessage,
    UrlValidationErrorCode? ErrorCode = null);

public enum UrlValidationErrorCode
{
    InvalidFormat,
    NonHttpsScheme,
    NotAllowlisted,
    PrivateIpAddress,
    LoopbackAddress,
    MetadataEndpoint
}
