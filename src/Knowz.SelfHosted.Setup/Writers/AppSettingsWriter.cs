using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Writers;

public static class AppSettingsWriter
{
    public static async Task WriteAsync(SetupConfig config, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            if (!AnsiConsole.Confirm($"[yellow]{outputPath} already exists. Overwrite?[/]", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]Skipped appsettings.Local.json generation.[/]");
                return;
            }
        }

        var root = new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject
            {
                ["McpDb"] = $"Host=localhost;Port=5432;Database=knowz_selfhosted;Username=knowz;Password={config.SaPassword}"
            },
            ["Jwt"] = new JsonObject
            {
                ["Secret"] = config.JwtSecret
            },
            ["Admin"] = new JsonObject
            {
                ["Username"] = config.AdminUsername,
                ["Password"] = config.AdminPassword
            },
            ["Mcp"] = new JsonObject
            {
                ["ServiceKey"] = config.McpServiceKey,
                ["Port"] = config.McpPort
            }
        };

        if (config.AiMode == AiMode.DirectAzure)
        {
            root["AzureOpenAI"] = new JsonObject
            {
                ["Endpoint"] = config.AzureOpenAiEndpoint,
                ["ApiKey"] = config.AzureOpenAiApiKey,
                ["DeploymentName"] = config.AzureOpenAiDeployment,
                ["EmbeddingDeployment"] = config.AzureOpenAiEmbedding
            };
            root["AzureAISearch"] = new JsonObject
            {
                ["Endpoint"] = config.AzureSearchEndpoint,
                ["ApiKey"] = config.AzureSearchApiKey,
                ["IndexName"] = config.AzureSearchIndex
            };
            if (!string.IsNullOrWhiteSpace(config.AzureAiVisionEndpoint) ||
                !string.IsNullOrWhiteSpace(config.AzureAiVisionApiKey))
            {
                root["AzureAIVision"] = new JsonObject
                {
                    ["Endpoint"] = config.AzureAiVisionEndpoint,
                    ["ApiKey"] = config.AzureAiVisionApiKey
                };
            }

            if (!string.IsNullOrWhiteSpace(config.AzureDocumentIntelligenceEndpoint) ||
                !string.IsNullOrWhiteSpace(config.AzureDocumentIntelligenceApiKey))
            {
                root["AzureDocumentIntelligence"] = new JsonObject
                {
                    ["Endpoint"] = config.AzureDocumentIntelligenceEndpoint,
                    ["ApiKey"] = config.AzureDocumentIntelligenceApiKey
                };
            }
        }
        else if (config.AiMode == AiMode.PlatformProxy)
        {
            root["KnowzPlatform"] = new JsonObject
            {
                ["Enabled"] = true,
                ["Url"] = config.PlatformProxyUrl,
                ["ApiKey"] = config.PlatformProxyApiKey
            };
        }

        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            root["Storage"] = new JsonObject
            {
                ["Provider"] = "AzureBlobStorage"
            };
            root["AzureStorage"] = new JsonObject
            {
                ["ConnectionString"] = config.AzureStorageConnectionString,
                ["ContainerName"] = config.AzureStorageContainer
            };
        }

        root["RateLimiting"] = new JsonObject
        {
            ["Enabled"] = config.RateLimitingEnabled
        };
        root["AllowedOrigin"] = config.CorsOrigin;
        root["Swagger"] = new JsonObject
        {
            ["Enabled"] = config.SwaggerEnabled
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);

        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, json);
        AnsiConsole.MarkupLine($"[green]Written to {outputPath}[/]");
    }
}
