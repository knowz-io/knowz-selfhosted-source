using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class ReviewStep
{
    public static Task<bool> RunAsync(SetupConfig config)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Configuration Summary[/]");

        table.AddColumn("Setting");
        table.AddColumn("Value");

        table.AddRow("Run Mode", config.RunMode.ToString());
        table.AddRow("AI Mode", config.AiMode.ToString());

        if (config.AiMode == AiMode.DirectAzure)
        {
            table.AddRow("OpenAI Endpoint", config.AzureOpenAiEndpoint);
            table.AddRow("OpenAI Key", MaskSecret(config.AzureOpenAiApiKey));
            table.AddRow("Chat Deployment", config.AzureOpenAiDeployment);
            table.AddRow("Embedding Deployment", config.AzureOpenAiEmbedding);
            table.AddRow("Vision Endpoint", string.IsNullOrWhiteSpace(config.AzureAiVisionEndpoint) ? "(not set)" : config.AzureAiVisionEndpoint);
            table.AddRow("Vision Key", MaskSecret(config.AzureAiVisionApiKey));
            table.AddRow("DocInt Endpoint", string.IsNullOrWhiteSpace(config.AzureDocumentIntelligenceEndpoint) ? "(not set)" : config.AzureDocumentIntelligenceEndpoint);
            table.AddRow("DocInt Key", MaskSecret(config.AzureDocumentIntelligenceApiKey));
            table.AddRow("Search Endpoint", config.AzureSearchEndpoint);
            table.AddRow("Search Key", MaskSecret(config.AzureSearchApiKey));
            table.AddRow("Search Index", config.AzureSearchIndex);
        }
        else if (config.AiMode == AiMode.PlatformProxy)
        {
            table.AddRow("Platform URL", config.PlatformProxyUrl);
            table.AddRow("Platform Key", MaskSecret(config.PlatformProxyApiKey));
        }

        table.AddRow("Storage", config.StorageMode.ToString());
        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            table.AddRow("Storage Connection", MaskSecret(config.AzureStorageConnectionString));
            table.AddRow("Storage Container", config.AzureStorageContainer);
        }

        table.AddRow("Admin", $"{config.AdminUsername} / {MaskSecret(config.AdminPassword)}");
        table.AddRow("JWT Secret", MaskSecret(config.JwtSecret));
        table.AddRow("Postgres password", MaskSecret(config.SaPassword));
        table.AddRow("MCP Service Key", MaskSecret(config.McpServiceKey));
        table.AddRow("CORS Origin", config.CorsOrigin);
        table.AddRow("Rate Limiting", config.RateLimitingEnabled ? "Enabled" : "Disabled");
        table.AddRow("Swagger", config.SwaggerEnabled ? "Enabled" : "Disabled");
        table.AddRow("MCP Port", config.McpPort.ToString());

        var outputFile = GetOutputDescription(config);
        table.AddRow("Output", outputFile);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var confirmed = AnsiConsole.Confirm("Generate configuration?", defaultValue: true);
        return Task.FromResult(confirmed);
    }

    private static string MaskSecret(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= 8) return new string('*', value.Length);
        return value[..4] + new string('*', value.Length - 8) + value[^4..];
    }

    private static string GetOutputDescription(SetupConfig config)
    {
        return config.RunMode switch
        {
            RunMode.DockerCompose => "selfhosted/.env",
            RunMode.AspireLocal or RunMode.AspireAzure => "dotnet user-secrets",
            RunMode.DirectRun => "appsettings.Local.json",
            RunMode.AzureCloudDeploy => "selfhosted-deploy-params.json",
            _ => "unknown"
        };
    }
}
