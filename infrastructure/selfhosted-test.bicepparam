// infrastructure/selfhosted-test.bicepparam
// Parameter file for self-hosted testing infrastructure.
//
// Usage:
//   az deployment group create -g rg-knowz-selfhosted \
//     -f infrastructure/selfhosted-test.bicep \
//     -p infrastructure/selfhosted-test.bicepparam \
//     -p sqlAdminPassword='<secure>' \
//        adminPassword='<secure>'
//
// Optional overrides via environment variables:
//   SH_PREFIX            - Resource name prefix (default: sh-test)
//   SH_LOCATION          - Azure region (default: eastus2)
//   SH_SQL_ADMIN_PASSWORD - SQL admin password (prefer CLI -p for real deploys)
//   SH_ADMIN_PASSWORD    - SuperAdmin password (prefer CLI -p for real deploys)
//   SH_DEPLOY_OPENAI     - true/false (default: true)
//   SH_SEARCH_SKU        - free/basic/standard (default: basic)
//   SH_EMBEDDING_MODEL   - text-embedding-3-small or text-embedding-3-large (default: text-embedding-3-large)
//   SH_ALLOW_ALL_IPS     - true/false, open SQL firewall to all IPs (default: false)
//   SH_DEPLOY_KEYVAULT   - true/false, deploy Key Vault (default: true)
//   SH_DEPLOY_MONITORING - true/false, deploy Log Analytics + App Insights (default: true)
//   SH_STORAGE_SHARED_KEY - true/false, allow storage shared key access (default: true)
//   SH_CA_EMBEDDING_DIMENSIONS - Container Apps embedding dims override (default: 3072)

using 'selfhosted-test.bicep'

param prefix = readEnvironmentVariable('SH_PREFIX', 'sh-test')
param location = readEnvironmentVariable('SH_LOCATION', 'eastus2')
param sqlAdminUsername = 'sqladmin'
param sqlAdminPassword = readEnvironmentVariable('SH_SQL_ADMIN_PASSWORD', '')
param adminPassword = readEnvironmentVariable('SH_ADMIN_PASSWORD', '')

// OpenAI: deploy locally or use external/shared
param deployOpenAI = readEnvironmentVariable('SH_DEPLOY_OPENAI', 'true') == 'true'
param externalOpenAiEndpoint = readEnvironmentVariable('SH_OPENAI_ENDPOINT', '')

// SQL firewall: allow all IPs (opt-in, default OFF)
param allowAllIps = readEnvironmentVariable('SH_ALLOW_ALL_IPS', 'false') == 'true'

// Search SKU: basic for testing, free if basic unavailable in region
param searchSku = readEnvironmentVariable('SH_SEARCH_SKU', 'basic')

// Search location: override if basic SKU unavailable in primary location
param searchLocation = readEnvironmentVariable('SH_SEARCH_LOCATION', readEnvironmentVariable('SH_LOCATION', 'eastus2'))

// Model deployment names (must match appsettings.json)
param chatDeploymentName = 'gpt-5.4'
param embeddingDeploymentName = 'text-embedding-3-large'
param embeddingModelName = readEnvironmentVariable('SH_EMBEDDING_MODEL', 'text-embedding-3-large')
// Vector dim — MUST match the embeddingModelName value above.
// text-embedding-3-small / ada-002 -> 1536, text-embedding-3-large -> 3072.
// Override via SH_EMBEDDING_DIMENSIONS=1536 when using -3-small.
param embeddingDimensions = int(readEnvironmentVariable('SH_EMBEDDING_DIMENSIONS', '3072'))

// Key Vault: deploy by default for enterprise secret management
param deployKeyVault = readEnvironmentVariable('SH_DEPLOY_KEYVAULT', 'true') == 'true'

// Monitoring: deploy Log Analytics + App Insights
param deployMonitoring = readEnvironmentVariable('SH_DEPLOY_MONITORING', 'true') == 'true'

// Storage shared key access: true for testing (connection string uses AccountKey)
// WARNING: Setting this to false will BREAK blob storage access. The app code
// (AzureBlobStorageProvider + StorageExtensions) only supports connection string auth
// (AccountKey). There is no DefaultAzureCredential/Managed Identity code path for
// blob storage yet. Disabling shared key access requires implementing MI-based
// BlobServiceClient in StorageExtensions.cs first.
param storageAllowSharedKeyAccess = readEnvironmentVariable('SH_STORAGE_SHARED_KEY', 'true') == 'true'

// Additional tags (optional)
// Use --parameters additionalTags='{"costCenter":"engineering"}' on CLI to add custom tags.

// ---- Container Apps (opt-in) ----
// Set SH_DEPLOY_CONTAINER_APPS=true to deploy API, MCP, and Web as Container Apps.
// Requires GHCR credentials (PAT with read:packages scope).
param deployContainerApps = readEnvironmentVariable('SH_DEPLOY_CONTAINER_APPS', 'false') == 'true'
param imageTag = readEnvironmentVariable('SH_IMAGE_TAG', 'latest')
param registryServer = readEnvironmentVariable('SH_REGISTRY_SERVER', 'ghcr.io')
param registryUsername = readEnvironmentVariable('SH_REGISTRY_USERNAME', '')

// Container Apps model deployment names (may differ from the OpenAI resource deployment names)
param caDeploymentName = readEnvironmentVariable('SH_CA_DEPLOYMENT_NAME', 'gpt-5.4')
param caEmbeddingDeploymentName = readEnvironmentVariable('SH_CA_EMBEDDING_DEPLOYMENT_NAME', 'text-embedding-3-large')
// CA Embedding model + dims. If SH_EMBEDDING_DIMENSIONS is changed, set
// SH_CA_EMBEDDING_DIMENSIONS to the same value unless CA intentionally differs.
param caEmbeddingModelName = readEnvironmentVariable('SH_CA_EMBEDDING_MODEL_NAME', embeddingModelName)
param caEmbeddingDimensions = int(readEnvironmentVariable('SH_CA_EMBEDDING_DIMENSIONS', '3072'))
