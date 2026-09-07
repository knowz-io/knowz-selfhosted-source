using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Writers;

public static class DeployParamsWriter
{
    public static async Task WriteAsync(SetupConfig config, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            if (!AnsiConsole.Confirm($"[yellow]{outputPath} already exists. Overwrite?[/]", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]Skipped deploy params generation.[/]");
                return;
            }
        }

        var parameters = new JsonObject
        {
            ["adminUsername"] = config.AdminUsername,
            ["adminPassword"] = config.AdminPassword,
            ["jwtSecret"] = config.JwtSecret,
            ["saPassword"] = config.SaPassword,
            ["mcpServiceKey"] = config.McpServiceKey,
            ["corsOrigin"] = config.CorsOrigin,
            ["rateLimitingEnabled"] = config.RateLimitingEnabled,
            ["swaggerEnabled"] = config.SwaggerEnabled,
            ["mcpPort"] = config.McpPort,
            ["storageProvider"] = config.StorageMode == StorageMode.AzureBlobStorage ? "AzureBlobStorage" : "LocalFileSystem"
        };

        if (config.AiMode == AiMode.DirectAzure)
        {
            parameters["aiMode"] = "DirectAzure";
            parameters["azureOpenAiEndpoint"] = config.AzureOpenAiEndpoint;
            parameters["azureOpenAiApiKey"] = config.AzureOpenAiApiKey;
            parameters["azureOpenAiDeployment"] = config.AzureOpenAiDeployment;
            parameters["azureOpenAiEmbedding"] = config.AzureOpenAiEmbedding;
            parameters["azureAiVisionEndpoint"] = config.AzureAiVisionEndpoint;
            parameters["azureAiVisionApiKey"] = config.AzureAiVisionApiKey;
            parameters["azureDocumentIntelligenceEndpoint"] = config.AzureDocumentIntelligenceEndpoint;
            parameters["azureDocumentIntelligenceApiKey"] = config.AzureDocumentIntelligenceApiKey;
            parameters["azureSearchEndpoint"] = config.AzureSearchEndpoint;
            parameters["azureSearchApiKey"] = config.AzureSearchApiKey;
            parameters["azureSearchIndex"] = config.AzureSearchIndex;
        }
        else if (config.AiMode == AiMode.PlatformProxy)
        {
            parameters["aiMode"] = "PlatformProxy";
            parameters["platformUrl"] = config.PlatformProxyUrl;
            parameters["platformApiKey"] = config.PlatformProxyApiKey;
        }
        else
        {
            parameters["aiMode"] = "NoAi";
        }

        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            parameters["azureStorageConnectionString"] = config.AzureStorageConnectionString;
            parameters["azureStorageContainer"] = config.AzureStorageContainer;
        }

        var root = new JsonObject
        {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = parameters
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);

        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null) Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, json);
        AnsiConsole.MarkupLine($"[green]Written to {outputPath}[/]");
    }
}
