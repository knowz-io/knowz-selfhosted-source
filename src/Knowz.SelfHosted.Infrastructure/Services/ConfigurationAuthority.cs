using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Infrastructure.Services;

public static class ConfigurationAuthority
{
    // These values are needed before DB configuration can be loaded (including its key ring).
    public static bool IsExternallyManaged(string key) => SecretConfigurationKeys.IsSecret(key)
        || key.StartsWith("AzureKeyVault:", StringComparison.OrdinalIgnoreCase);

    public static string? For(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root) return configuration[key] is null ? null : "configuration";
        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value) || value is null) continue;
            if (provider is DatabaseConfigurationProvider) return "database";
            if (provider.GetType().Name == "EnvironmentVariablesConfigurationProvider") return "environment";
            if (provider.GetType().Name == "JsonConfigurationProvider") return "appsettings";
            if (provider.GetType().Name == "AzureKeyVaultConfigurationProvider") return "keyvault";
            return "configuration";
        }
        return null;
    }
}
