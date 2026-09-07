namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Registry of configuration keys (in <c>Category:Key</c> form) that hold secrets
/// and therefore MUST come from Key Vault or environment variables — never from
/// the database-backed <see cref="DatabaseConfigurationProvider"/>.
///
/// Consulted by <c>DatabaseConfigurationProvider.Load</c> to drop any DB row
/// whose key appears here, preserving the trust-sources invariant documented in
/// SEC_P0Triage §Rule 4 (SH_ENTERPRISE_SECURITY_HARDENING.md).
///
/// Two parity tests in <c>DatabaseConfigurationProviderTests</c> keep this
/// registry honest: <c>SecretConfigurationKeys_CoversEverySchemaSecret</c>
/// fails if the schema adds a secret we forget here;
/// <c>SecretConfigurationKeys_ExtrasAreProductivePreDenylisting</c> fails if
/// this list grows unexpected entries (extras must be documented in the
/// approved-extras list with rationale — see <c>SelfHosted:SuperAdminPassword</c>
/// below).
/// </summary>
public static class SecretConfigurationKeys
{
    /// <summary>
    /// Canonical list of secret-tier configuration keys, case-insensitive.
    /// Order does not matter; duplicates would be benign.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ConnectionStrings:McpDb",
        "AzureOpenAI:ApiKey",
        "OpenAiCompatible:ApiKey",
        "AzureAIVision:ApiKey",
        "AzureDocumentIntelligence:ApiKey",
        "AzureAISearch:ApiKey",
        "Storage:Azure:ConnectionString",
        "SelfHosted:JwtSecret",
        "SelfHosted:ApiKey",
        // Pre-denylisted against schema evolution — SuperAdminPassword is NOT
        // in ConfigurationManagementService.CategorySchemas today (no DB row
        // emits it), but if a future dev adds it as an admin-editable config
        // they'd create a KV-override hole unless this denylist already
        // covers the key. The paired "approvedExtras" entry in
        // DatabaseConfigurationProviderTests.SecretConfigurationKeys_ExtrasAre
        // ProductivePreDenylisting documents this as an intentional extra.
        "SelfHosted:SuperAdminPassword",
        "KnowzPlatform:ApiKey",
        "SSO:Microsoft:ClientSecret",
        "SSO:Google:ClientSecret",
    };

    /// <summary>
    /// Returns true if <paramref name="configKey"/> (already in
    /// <c>Category:Key</c> form) is in the secret registry.
    /// </summary>
    public static bool IsSecret(string configKey) => All.Contains(configKey);
}
