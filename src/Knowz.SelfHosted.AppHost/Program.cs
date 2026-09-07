var builder = DistributedApplication.CreateBuilder(args);

// =====================================================================
// INFRASTRUCTURE MODE
// =====================================================================
//
// Set via INFRA_MODE environment variable. Three modes:
//
// ┌──────────┬────────────────────────────────────────────────────────────────────────┐
// │  MODE    │  DESCRIPTION                                                          │
// ├──────────┼────────────────────────────────────────────────────────────────────────┤
// │  local   │  DEFAULT. Postgres 16 + pgvector container + local file storage.      │
// │ (default)│  AI via user-secrets (KnowzPlatform proxy or direct Azure). Best for  │
// │          │  isolated dev — no cloud connections needed except AI.                │
// │          │                                                                        │
// │          │  Containers: pgvector/pgvector:pg16, (storage: local FS)              │
// │          │  Cloud:      Azure OpenAI / Search (if configured)                    │
// ├──────────┼────────────────────────────────────────────────────────────────────────┤
// │  cloud   │  Zero containers. Points at rg-knowz-sh Azure resources. API and      │
// │          │  web run locally; all infra (SQL, Storage, AI, monitoring) from dev   │
// │          │  Azure. Ideal for UX iteration with real data — no Docker needed.     │
// │          │                                                                        │
// │          │  Containers: NONE                                                      │
// │          │  Cloud:      SQL, Storage, AI, AppInsights (all from rg-knowz-sh)     │
// │          │  Setup:      Run scripts/setup-sh-dev.ps1 first (once per machine)    │
// ├──────────┼────────────────────────────────────────────────────────────────────────┤
// │  hybrid  │  Starts from "local" defaults. Override per-service:                  │
// │          │    LOCAL_SQL=false      Use dev cloud SQL                              │
// │          │    LOCAL_STORAGE=false  Use dev cloud Azure Blob Storage              │
// └──────────┴────────────────────────────────────────────────────────────────────────┘
//
// USAGE:
//   dotnet run --project selfhosted/src/Knowz.SelfHosted.AppHost                    # local (default)
//   INFRA_MODE=cloud dotnet run --project selfhosted/src/Knowz.SelfHosted.AppHost   # pure dev cloud
//   INFRA_MODE=hybrid LOCAL_SQL=false dotnet run ...                                # local storage, cloud SQL
//
// UI ONLY (no Aspire, web client proxies to deployed Container App):
//   cd selfhosted/src/knowz-selfhosted-web
//   npm run dev:cloud           # uses .env.cloud (see .env.cloud.example)
//
// =====================================================================

const string ModeLocal = "local";
const string ModeCloud = "cloud";
const string ModeHybrid = "hybrid";

var infraMode = (builder.Configuration["INFRA_MODE"] ?? ModeLocal).ToLowerInvariant();
if (infraMode is not (ModeLocal or ModeCloud or ModeHybrid))
{
    Console.Error.WriteLine($"[AppHost] ERROR: Unknown INFRA_MODE '{infraMode}'. Must be one of: local, cloud, hybrid");
    Console.Error.WriteLine($"[AppHost] Falling back to '{ModeLocal}'.");
    infraMode = ModeLocal;
}

// --- Derive per-service flags ---
bool localSql, localStorage;

switch (infraMode)
{
    case ModeCloud:
        localSql = false;
        localStorage = false;
        break;

    case ModeHybrid:
        localSql = ConfigFlag("LOCAL_SQL", defaultValue: true);
        localStorage = ConfigFlag("LOCAL_STORAGE", defaultValue: true);
        break;

    case ModeLocal:
    default:
        localSql = true;
        localStorage = true;
        break;
}

// =====================================================================
// CLOUD CONFIG (from user-secrets on AppHost project)
// =====================================================================
// Run scripts/setup-sh-dev.ps1 to populate these from the rg-knowz-sh Key Vault.
// Alternatively set manually:
//   cd selfhosted/src/Knowz.SelfHosted.AppHost
//   dotnet user-secrets set "ConnectionStrings:McpDb" "Server=tcp:..."
//   dotnet user-secrets set "AzureOpenAI:ApiKey" "..."
//   ... (see scripts/setup-sh-dev.ps1 for full list)

// --- Core infra ---
var dbConnection = builder.Configuration["ConnectionStrings:McpDb"];
var storageConnStr = builder.Configuration["Storage:Azure:ConnectionString"];
var storageContainer = builder.Configuration["Storage:Azure:ContainerName"];

// --- Auth ---
var jwtSecret = builder.Configuration["SelfHosted:JwtSecret"];
var adminPassword = builder.Configuration["SelfHosted:SuperAdminPassword"];
var selfHostedApiKey = builder.Configuration["SelfHosted:ApiKey"];
var mcpServiceKey = builder.Configuration["Mcp:ServiceKey"];

// --- AI Tier 1: Knowz Platform Proxy ---
var platformEnabled = builder.Configuration["KnowzPlatform:Enabled"];
var platformBaseUrl = builder.Configuration["KnowzPlatform:BaseUrl"];
var platformApiKey = builder.Configuration["KnowzPlatform:ApiKey"];

// --- AI Tier 2: Direct Azure ---
var openAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
var openAiApiKey = builder.Configuration["AzureOpenAI:ApiKey"];
var openAiDeployment = builder.Configuration["AzureOpenAI:DeploymentName"];
var openAiEmbedding = builder.Configuration["AzureOpenAI:EmbeddingDeploymentName"];
var visionEndpoint = builder.Configuration["AzureAIVision:Endpoint"];
var visionApiKey = builder.Configuration["AzureAIVision:ApiKey"];
var docIntelEndpoint = builder.Configuration["AzureDocumentIntelligence:Endpoint"];
var docIntelApiKey = builder.Configuration["AzureDocumentIntelligence:ApiKey"];
var searchEndpoint = builder.Configuration["AzureAISearch:Endpoint"];
var searchApiKey = builder.Configuration["AzureAISearch:ApiKey"];
var searchIndexName = builder.Configuration["AzureAISearch:IndexName"];

// --- Monitoring ---
var appInsightsConnStr = builder.Configuration["ApplicationInsights:ConnectionString"];

// --- Startup banner ---
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║       Knowz Self-Hosted Infrastructure Mode          ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Mode       : {infraMode,-39}║");
Console.WriteLine($"║  Postgres   : {Label(localSql),-39}║");
Console.WriteLine($"║  Storage    : {Label(localStorage, "LocalFileSystem", "Azure Blob (rg-knowz-sh)"),-39}║");
var aiLabel = !string.IsNullOrWhiteSpace(platformEnabled) && platformEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)
    ? $"Platform proxy ({platformBaseUrl ?? "url not set"})"
    : !string.IsNullOrWhiteSpace(openAiEndpoint) ? "Direct Azure OpenAI" : "NoOp (not configured)";
Console.WriteLine($"║  AI         : {aiLabel,-39}║");
Console.WriteLine($"║  Monitoring : {(!string.IsNullOrWhiteSpace(appInsightsConnStr) ? "App Insights (rg-knowz-sh)" : "None (local only)"),-39}║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

if (infraMode == ModeCloud && string.IsNullOrWhiteSpace(dbConnection))
{
    Console.Error.WriteLine("[AppHost] WARN: INFRA_MODE=cloud but ConnectionStrings:McpDb is not set.");
    Console.Error.WriteLine("[AppHost]       Run scripts/setup-sh-dev.ps1 to configure user-secrets.");
    Console.Error.WriteLine("[AppHost]       Falling back — API will use its own appsettings.Local.json if present.");
    Console.Error.WriteLine();
}

// =====================================================================
// INFRASTRUCTURE RESOURCES
// =====================================================================

IResourceBuilder<IResourceWithConnectionString>? sqlDb = null;

if (localSql)
{
    var pg = builder.AddPostgres("pg")
        .WithImage("pgvector/pgvector")
        .WithImageTag("pg16")
        .WithLifetime(ContainerLifetime.Persistent);
    sqlDb = pg.AddDatabase("McpDb");
}

// =====================================================================
// SELF-HOSTED API
// =====================================================================

var api = builder.AddProject<Projects.Knowz_SelfHosted_API>("selfhosted-api")
    .WithHttpEndpoint(port: 5000, name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Database__AutoMigrate", "true");

// --- SQL wiring ---
if (localSql)
{
    api.WithReference(sqlDb!).WaitFor(sqlDb!);
}
else if (!string.IsNullOrWhiteSpace(dbConnection))
{
    api.WithEnvironment("ConnectionStrings__McpDb", dbConnection);
}

// --- Storage wiring ---
if (localStorage)
{
    api.WithEnvironment("Storage__Provider", "LocalFileSystem")
       .WithEnvironment("Storage__Local__RootPath", "/tmp/knowz-files");
}
else
{
    api.WithEnvironment("Storage__Provider", "Azure");
    if (!string.IsNullOrWhiteSpace(storageConnStr))
        api.WithEnvironment("Storage__Azure__ConnectionString", storageConnStr);
    if (!string.IsNullOrWhiteSpace(storageContainer))
        api.WithEnvironment("Storage__Azure__ContainerName", storageContainer);
}

// --- Auth ---
if (!string.IsNullOrWhiteSpace(jwtSecret))
    api.WithEnvironment("SelfHosted__JwtSecret", jwtSecret);
if (!string.IsNullOrWhiteSpace(adminPassword))
    api.WithEnvironment("SelfHosted__SuperAdminPassword", adminPassword);
if (!string.IsNullOrWhiteSpace(selfHostedApiKey))
    api.WithEnvironment("SelfHosted__ApiKey", selfHostedApiKey);
if (!string.IsNullOrWhiteSpace(mcpServiceKey))
    api.WithEnvironment("MCP__ServiceKey", mcpServiceKey);

// --- AI Tier 1: Knowz Platform Proxy ---
if (!string.IsNullOrWhiteSpace(platformEnabled))
{
    api.WithEnvironment("KnowzPlatform__Enabled", platformEnabled);
    if (!string.IsNullOrWhiteSpace(platformBaseUrl))
        api.WithEnvironment("KnowzPlatform__BaseUrl", platformBaseUrl);
    if (!string.IsNullOrWhiteSpace(platformApiKey))
        api.WithEnvironment("KnowzPlatform__ApiKey", platformApiKey);
}

// --- AI Tier 2: Direct Azure ---
if (!string.IsNullOrWhiteSpace(openAiEndpoint))
    api.WithEnvironment("AzureOpenAI__Endpoint", openAiEndpoint);
if (!string.IsNullOrWhiteSpace(openAiApiKey))
    api.WithEnvironment("AzureOpenAI__ApiKey", openAiApiKey);
if (!string.IsNullOrWhiteSpace(openAiDeployment))
    api.WithEnvironment("AzureOpenAI__DeploymentName", openAiDeployment);
if (!string.IsNullOrWhiteSpace(openAiEmbedding))
    api.WithEnvironment("AzureOpenAI__EmbeddingDeploymentName", openAiEmbedding);
if (!string.IsNullOrWhiteSpace(visionEndpoint))
    api.WithEnvironment("AzureAIVision__Endpoint", visionEndpoint);
if (!string.IsNullOrWhiteSpace(visionApiKey))
    api.WithEnvironment("AzureAIVision__ApiKey", visionApiKey);
if (!string.IsNullOrWhiteSpace(docIntelEndpoint))
    api.WithEnvironment("AzureDocumentIntelligence__Endpoint", docIntelEndpoint);
if (!string.IsNullOrWhiteSpace(docIntelApiKey))
    api.WithEnvironment("AzureDocumentIntelligence__ApiKey", docIntelApiKey);
if (!string.IsNullOrWhiteSpace(searchEndpoint))
    api.WithEnvironment("AzureAISearch__Endpoint", searchEndpoint);
if (!string.IsNullOrWhiteSpace(searchApiKey))
    api.WithEnvironment("AzureAISearch__ApiKey", searchApiKey);
if (!string.IsNullOrWhiteSpace(searchIndexName))
    api.WithEnvironment("AzureAISearch__IndexName", searchIndexName);

// --- Monitoring ---
if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
    api.WithEnvironment("ApplicationInsights__ConnectionString", appInsightsConnStr);

// =====================================================================
// MCP SERVER
// =====================================================================

var mcp = builder.AddProject<Projects.Knowz_MCP>("mcp")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Knowz__BaseUrl", api.GetEndpoint("http"))
    .WithEnvironment("MCP__ApiKeyValidationEndpoint", "/api/vaults");

if (!string.IsNullOrWhiteSpace(mcpServiceKey))
    mcp.WithEnvironment("MCP__ServiceKey", mcpServiceKey);

// =====================================================================
// SELF-HOSTED WEB CLIENT (React + Vite)
// =====================================================================
// The Vite dev server proxies /api -> local API (port 5000).
// For UI-only mode (no local API), run from the web client directory:
//   npm run dev:cloud    (proxies to rg-knowz-sh deployed Container App)

builder.AddNpmApp("selfhosted-web", "../knowz-selfhosted-web", "dev")
    .WithHttpEndpoint(targetPort: 5173, name: "http", isProxied: false)
    .WithReference(api);

builder.Build().Run();

// =====================================================================
// HELPERS
// =====================================================================

bool ConfigFlag(string key, bool defaultValue)
{
    var value = builder.Configuration[key];
    if (value is null) return defaultValue;
    return value.Equals("true", StringComparison.OrdinalIgnoreCase);
}

string Label(bool isLocal, string localName = "container", string cloudName = "cloud (appsettings)")
{
    return isLocal ? $"LOCAL ({localName})" : $"CLOUD ({cloudName})";
}
