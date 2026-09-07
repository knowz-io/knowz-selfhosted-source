using System.Diagnostics;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Writers;

public static class UserSecretsWriter
{
    private const string UserSecretsId = "knowz-selfhosted-apphost";

    public static async Task WriteAsync(SetupConfig config)
    {
        var secrets = BuildSecretsDictionary(config);

        AnsiConsole.MarkupLine($"[blue]Setting {secrets.Count} user secrets for '{UserSecretsId}'...[/]");

        var failures = 0;
        foreach (var (key, value) in secrets)
        {
            if (!await SetSecretAsync(key, value))
                failures++;
        }

        if (failures == 0)
            AnsiConsole.MarkupLine($"[green]All {secrets.Count} user secrets set successfully.[/]");
        else
            AnsiConsole.MarkupLine($"[red]{failures} of {secrets.Count} secrets failed to set.[/]");
    }

    private static Dictionary<string, string> BuildSecretsDictionary(SetupConfig config)
    {
        var secrets = new Dictionary<string, string>
        {
            ["Jwt:Secret"] = config.JwtSecret,
            ["Admin:Username"] = config.AdminUsername,
            ["Admin:Password"] = config.AdminPassword,
            ["Mcp:ServiceKey"] = config.McpServiceKey,
            ["ConnectionStrings:McpDb"] = $"Host=localhost;Port=5432;Database=knowz_selfhosted;Username=knowz;Password={config.SaPassword}",
        };

        if (config.AiMode == AiMode.DirectAzure)
        {
            AddIfNotEmpty(secrets, "AzureOpenAI:Endpoint", config.AzureOpenAiEndpoint);
            AddIfNotEmpty(secrets, "AzureOpenAI:ApiKey", config.AzureOpenAiApiKey);
            AddIfNotEmpty(secrets, "AzureOpenAI:DeploymentName", config.AzureOpenAiDeployment);
            AddIfNotEmpty(secrets, "AzureOpenAI:EmbeddingDeployment", config.AzureOpenAiEmbedding);
            AddIfNotEmpty(secrets, "AzureAIVision:Endpoint", config.AzureAiVisionEndpoint);
            AddIfNotEmpty(secrets, "AzureAIVision:ApiKey", config.AzureAiVisionApiKey);
            AddIfNotEmpty(secrets, "AzureDocumentIntelligence:Endpoint", config.AzureDocumentIntelligenceEndpoint);
            AddIfNotEmpty(secrets, "AzureDocumentIntelligence:ApiKey", config.AzureDocumentIntelligenceApiKey);
            AddIfNotEmpty(secrets, "AzureAISearch:Endpoint", config.AzureSearchEndpoint);
            AddIfNotEmpty(secrets, "AzureAISearch:ApiKey", config.AzureSearchApiKey);
            AddIfNotEmpty(secrets, "AzureAISearch:IndexName", config.AzureSearchIndex);
        }
        else if (config.AiMode == AiMode.PlatformProxy)
        {
            secrets["KnowzPlatform:Enabled"] = "true";
            AddIfNotEmpty(secrets, "KnowzPlatform:Url", config.PlatformProxyUrl);
            AddIfNotEmpty(secrets, "KnowzPlatform:ApiKey", config.PlatformProxyApiKey);
        }

        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            secrets["Storage:Provider"] = "AzureBlobStorage";
            AddIfNotEmpty(secrets, "AzureStorage:ConnectionString", config.AzureStorageConnectionString);
            AddIfNotEmpty(secrets, "AzureStorage:ContainerName", config.AzureStorageContainer);
        }

        return secrets;
    }

    private static void AddIfNotEmpty(Dictionary<string, string> dict, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            dict[key] = value;
    }

    private static async Task<bool> SetSecretAsync(string key, string value)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"user-secrets set \"{key}\" \"{value}\" --id {UserSecretsId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to set secret '{key}': {ex.Message.EscapeMarkup()}[/]");
            return false;
        }
    }
}
