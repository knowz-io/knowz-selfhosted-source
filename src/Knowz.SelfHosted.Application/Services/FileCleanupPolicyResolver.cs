using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Self-hosted file-cleanup behavior. Mirrors the platform enum semantics, but is a
/// SELF-HOSTED-LOCAL declaration — it deliberately does NOT reference the platform
/// <c>Knowz.Domain</c> enum (self-hosted is a separate solution).
/// WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
/// </summary>
public enum FileCleanupMode
{
    /// <summary>Always delete the underlying file on detach (when not referenced elsewhere).</summary>
    AutoCleanup,
    /// <summary>Never delete the underlying file on detach — just remove the link.</summary>
    PreserveAlways,
    /// <summary>Ask the user each time. Server treats an absent <c>deleteFiles</c> param as preserve (fail-safe).</summary>
    PromptUser
}

/// <summary>
/// Resolves the configured self-hosted file-cleanup mode.
/// </summary>
public interface IFileCleanupPolicyResolver
{
    /// <summary>
    /// Returns the configured <see cref="FileCleanupMode"/> (from <c>FileCleanup:Mode</c> /
    /// env var <c>FileCleanup__Mode</c>). Defaults to <see cref="FileCleanupMode.PromptUser"/>
    /// when unset or unparseable — fail-safe parity with the platform default.
    /// </summary>
    FileCleanupMode ResolveMode();
}

/// <summary>
/// Config-backed resolver. Self-hosted is single-tenant with no background worker, so the policy
/// is a single scalar read from configuration rather than a DB table (a per-tenant table is
/// unnecessary machinery and would force a self-hosted EF migration for a single choice).
/// Tracked <c>REFACTOR_SelfHostedFileCleanupPolicyTable</c> for the day a runtime UI is wanted.
/// </summary>
public sealed class FileCleanupPolicyResolver : IFileCleanupPolicyResolver
{
    private const string ConfigKey = "FileCleanup:Mode";
    private readonly IConfiguration _configuration;

    public FileCleanupPolicyResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public FileCleanupMode ResolveMode()
    {
        var raw = _configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse<FileCleanupMode>(raw, ignoreCase: true, out var mode))
        {
            return mode;
        }

        // Fail-safe default (parity with platform R4): when unset or invalid, PromptUser → preserve.
        return FileCleanupMode.PromptUser;
    }
}
