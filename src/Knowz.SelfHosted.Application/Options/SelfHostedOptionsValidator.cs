using Knowz.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.Application.Options;

/// <summary>
/// Startup-time validator for <see cref="SelfHostedOptions"/>. Fails-closed when
/// authentication is enabled but the JWT signing key is absent or too short.
///
/// Registered in <c>Program.cs</c> via
/// <c>services.AddOptions&lt;SelfHostedOptions&gt;().BindConfiguration(...).ValidateOnStart()</c>
/// so a misconfigured deployment crashes at boot rather than silently accepting
/// forged tokens.
///
/// Part of SEC_P0Triage Item 4 (SH_ENTERPRISE_SECURITY_HARDENING.md §Rule 1).
/// </summary>
public sealed class SelfHostedOptionsValidator : IValidateOptions<SelfHostedOptions>
{
    /// <summary>Minimum acceptable JWT signing secret length (bytes/chars of UTF-8).</summary>
    public const int MinJwtSecretLength = 32;

    public ValidateOptionsResult Validate(string? name, SelfHostedOptions options)
    {
        var errors = new List<string>();

        var jwtSet = !string.IsNullOrWhiteSpace(options.JwtSecret);
        var apiKeySet = !string.IsNullOrWhiteSpace(options.ApiKey);
        var authEnabled = jwtSet || apiKeySet;

        if (authEnabled && !jwtSet)
        {
            errors.Add(
                "SelfHosted:JwtSecret is required when authentication is enabled. " +
                "Supply via env var SelfHosted__JwtSecret or Key Vault secret SelfHosted--JwtSecret.");
        }

        if (jwtSet && options.JwtSecret.Length < MinJwtSecretLength)
        {
            errors.Add(
                $"SelfHosted:JwtSecret must be at least {MinJwtSecretLength} characters " +
                $"(current: {options.JwtSecret.Length}). Use a cryptographically random value.");
        }

        if (jwtSet && string.IsNullOrWhiteSpace(options.JwtIssuer))
        {
            errors.Add("SelfHosted:JwtIssuer is required when JwtSecret is set.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
