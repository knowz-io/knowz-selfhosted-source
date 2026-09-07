using System.Text;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Writers;

public static class EnvFileWriter
{
    public static async Task WriteAsync(SetupConfig config, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            if (!AnsiConsole.Confirm($"[yellow]{outputPath} already exists. Overwrite?[/]", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]Skipped .env generation.[/]");
                return;
            }
        }

        var sb = new StringBuilder();

        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("# Knowz Self-Hosted — Generated Configuration");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine();

        // Core settings
        sb.AppendLine("# --- Core Settings (always active) -----------------------------------------");
        sb.AppendLine($"POSTGRES_PASSWORD={config.SaPassword}");
        sb.AppendLine($"JWT_SECRET={config.JwtSecret}");
        sb.AppendLine($"ADMIN_USERNAME={config.AdminUsername}");
        sb.AppendLine($"ADMIN_PASSWORD={config.AdminPassword}");
        sb.AppendLine($"MCP_SERVICE_KEY={config.McpServiceKey}");
        sb.AppendLine();

        // AI settings
        switch (config.AiMode)
        {
            case AiMode.DirectAzure:
                sb.AppendLine("# --- Direct Azure OpenAI + AI Search ---------------------------------------");
                AppendIfNotEmpty(sb, "AZURE_OPENAI_ENDPOINT", config.AzureOpenAiEndpoint);
                AppendIfNotEmpty(sb, "AZURE_OPENAI_APIKEY", config.AzureOpenAiApiKey);
                AppendIfNotEmpty(sb, "AZURE_OPENAI_DEPLOYMENT", config.AzureOpenAiDeployment);
                AppendIfNotEmpty(sb, "AZURE_OPENAI_EMBEDDING", config.AzureOpenAiEmbedding);
                AppendIfNotEmpty(sb, "AZURE_AI_VISION_ENDPOINT", config.AzureAiVisionEndpoint);
                AppendIfNotEmpty(sb, "AZURE_AI_VISION_APIKEY", config.AzureAiVisionApiKey);
                AppendIfNotEmpty(sb, "AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT", config.AzureDocumentIntelligenceEndpoint);
                AppendIfNotEmpty(sb, "AZURE_DOCUMENT_INTELLIGENCE_APIKEY", config.AzureDocumentIntelligenceApiKey);
                AppendIfNotEmpty(sb, "AZURE_SEARCH_ENDPOINT", config.AzureSearchEndpoint);
                AppendIfNotEmpty(sb, "AZURE_SEARCH_APIKEY", config.AzureSearchApiKey);
                AppendIfNotEmpty(sb, "AZURE_SEARCH_INDEX", config.AzureSearchIndex);
                sb.AppendLine();
                break;
            case AiMode.PlatformProxy:
                sb.AppendLine("# --- Knowz Platform Proxy --------------------------------------------------");
                sb.AppendLine("KNOWZ_PLATFORM_ENABLED=true");
                AppendIfNotEmpty(sb, "KNOWZ_PLATFORM_URL", config.PlatformProxyUrl);
                AppendIfNotEmpty(sb, "KNOWZ_PLATFORM_APIKEY", config.PlatformProxyApiKey);
                sb.AppendLine();
                break;
            case AiMode.NoAi:
                sb.AppendLine("# --- AI Mode: No AI (CRUD only) -------------------------------------------");
                sb.AppendLine();
                break;
        }

        // Storage
        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            sb.AppendLine("# --- Azure Blob Storage ----------------------------------------------------");
            sb.AppendLine("STORAGE_PROVIDER=AzureBlobStorage");
            AppendIfNotEmpty(sb, "AZURE_STORAGE_CONNECTION_STRING", config.AzureStorageConnectionString);
            AppendIfNotEmpty(sb, "AZURE_STORAGE_CONTAINER", config.AzureStorageContainer);
            sb.AppendLine();
        }

        // Advanced
        sb.AppendLine("# --- Advanced ---------------------------------------------------------------");
        sb.AppendLine($"RATE_LIMITING_ENABLED={config.RateLimitingEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine($"ALLOWED_ORIGIN={config.CorsOrigin}");
        sb.AppendLine($"ENABLE_SWAGGER={config.SwaggerEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine($"MCP_PORT={config.McpPort}");

        await File.WriteAllTextAsync(outputPath, sb.ToString());
        AnsiConsole.MarkupLine($"[green]Written to {outputPath}[/]");
    }

    private static void AppendIfNotEmpty(StringBuilder sb, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"{key}={value}");
    }
}
