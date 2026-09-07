namespace Knowz.SelfHosted.Setup.Models;

public enum RunMode
{
    DockerCompose,
    AspireLocal,
    AspireAzure,
    DirectRun,
    AzureCloudDeploy
}

public enum AiMode
{
    NoAi,
    DirectAzure,
    PlatformProxy,
    AutoDetect
}

public enum StorageMode
{
    LocalFileSystem,
    AzureBlobStorage
}

public class SetupConfig
{
    // Run mode
    public RunMode RunMode { get; set; } = RunMode.DockerCompose;

    // AI configuration
    public AiMode AiMode { get; set; } = AiMode.NoAi;

    // Direct Azure OpenAI
    public string AzureOpenAiEndpoint { get; set; } = string.Empty;
    public string AzureOpenAiApiKey { get; set; } = string.Empty;
    public string AzureOpenAiDeployment { get; set; } = "gpt-4o";
    public string AzureOpenAiEmbedding { get; set; } = "text-embedding-3-small";
    public string AzureAiVisionEndpoint { get; set; } = string.Empty;
    public string AzureAiVisionApiKey { get; set; } = string.Empty;
    public string AzureDocumentIntelligenceEndpoint { get; set; } = string.Empty;
    public string AzureDocumentIntelligenceApiKey { get; set; } = string.Empty;

    // Azure AI Search
    public string AzureSearchEndpoint { get; set; } = string.Empty;
    public string AzureSearchApiKey { get; set; } = string.Empty;
    public string AzureSearchIndex { get; set; } = "knowz-selfhosted";

    // Platform Proxy
    public string PlatformProxyUrl { get; set; } = "https://api.knowz.io";
    public string PlatformProxyApiKey { get; set; } = string.Empty;

    // Auto-detect Key Vault
    public string KeyVaultName { get; set; } = string.Empty;

    // Storage
    public StorageMode StorageMode { get; set; } = StorageMode.LocalFileSystem;
    public string AzureStorageConnectionString { get; set; } = string.Empty;
    public string AzureStorageContainer { get; set; } = "selfhosted-files";

    // Credentials — no defaults (SEC_P0Triage §Rule 2). Values are captured
    // via CredentialsStep, which validates each against ConfigValidator.
    // Empty-string defaults here ensure a config object constructed outside
    // the Setup CLI (e.g. in tests or programmatic import) is rejected at
    // runtime by AuthService / SelfHostedOptionsValidator before any seed.
    public string AdminUsername { get; set; } = string.Empty;
    public string AdminPassword { get; set; } = string.Empty;
    public string JwtSecret { get; set; } = string.Empty;
    public string SaPassword { get; set; } = string.Empty;
    public string McpServiceKey { get; set; } = string.Empty;

    // Advanced
    public string CorsOrigin { get; set; } = "http://localhost:3000";
    public bool RateLimitingEnabled { get; set; } = true;
    public bool SwaggerEnabled { get; set; } = false;
    public int McpPort { get; set; } = 3001;
}
