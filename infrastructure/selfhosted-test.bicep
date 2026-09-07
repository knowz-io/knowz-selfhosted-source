// infrastructure/selfhosted-test.bicep
// Standalone Bicep template for self-hosted MCP testing infrastructure.
// Provisions: AI Search, Azure OpenAI + model deployments, SQL + McpKnowledge DB,
// Blob Storage, Managed Identity + RBAC, Key Vault + secrets, Log Analytics + App Insights.
//
// Usage:
//   az group create -n rg-selfhosted-test -l eastus2
//   az deployment group create -g rg-selfhosted-test \
//     -f infrastructure/selfhosted-test.bicep \
//     -p infrastructure/selfhosted-test.bicepparam \
//     -p sqlAdminPassword='<secure>' \
//        adminPassword='<secure>'
//
// After deployment, use the deploy script (selfhosted-deploy.ps1) which retrieves
// secrets via Azure CLI and generates appsettings.Local.json automatically.
// Secret values are NOT included in Bicep outputs (ARM deployment history safety).

// ============================================================================
// PARAMETERS
// ============================================================================

@description('Object ID of the deploying user/SP (grants Key Vault Secrets Officer for secret creation). Populated by deploy script from `az ad signed-in-user show`.')
param deployerObjectId string = ''

@description('Resource name prefix (used for all resource naming)')
param prefix string = 'sh-test'

@description('Location for all resources')
param location string = 'eastus2'

@description('SQL Server administrator username')
@secure()
param sqlAdminUsername string = 'sqladmin'

@description('SQL Server administrator password')
@secure()
param sqlAdminPassword string

@description('Deploy Azure OpenAI (set to false when using external/shared OpenAI)')
param deployOpenAI bool = true

@description('External OpenAI endpoint (required if deployOpenAI is false)')
param externalOpenAiEndpoint string = ''

@description('External OpenAI API key (required if deployOpenAI is false)')
@secure()
param externalOpenAiKey string = ''

@description('Name of existing Azure OpenAI resource to reuse (leave empty to deploy new or use external)')
param existingOpenAiName string = ''

@description('Resource group of existing OpenAI resource (defaults to current RG)')
param existingOpenAiResourceGroup string = ''

@description('Deploy Azure Document Intelligence for advanced document extraction (scanned PDFs, images, legacy Office formats)')
param deployDocumentIntelligence bool = true

@description('External Document Intelligence endpoint (required if deployDocumentIntelligence is false)')
param externalDocIntelEndpoint string = ''

@secure()
@description('External Document Intelligence API key (required if deployDocumentIntelligence is false)')
param externalDocIntelKey string = ''

@description('Name of existing Document Intelligence resource to reuse (leave empty to deploy new or use external)')
param existingDocIntelName string = ''

@description('Resource group of existing Document Intelligence resource (defaults to current RG)')
param existingDocIntelResourceGroup string = ''

@description('Deploy Azure AI Vision for image and diagram analysis')
param deployVision bool = true

@description('External Azure AI Vision endpoint (required if deployVision is false)')
param externalVisionEndpoint string = ''

@secure()
@description('External Azure AI Vision API key (required if deployVision is false)')
param externalVisionKey string = ''

@description('Name of existing Azure AI Vision resource to reuse (leave empty to deploy new or use external)')
param existingVisionName string = ''

@description('Resource group of existing Vision resource (defaults to current RG)')
param existingVisionResourceGroup string = ''

@description('Azure AI Search SKU')
@allowed(['free', 'basic', 'standard'])
param searchSku string = 'basic'

@description('Location for AI Search (override if SKU unavailable in primary location)')
param searchLocation string = location

@description('Allow all IPs to access SQL Server (test/dev only, default OFF)')
param allowAllIps bool = false

@description('Chat model deployment name (must match appsettings DeploymentName)')
param chatDeploymentName string = 'gpt-5.4'

@description('Embedding deployment name (must match appsettings EmbeddingDeploymentName)')
param embeddingDeploymentName string = 'text-embedding-3-large'

@description('Embedding model name (text-embedding-3-large or text-embedding-3-small)')
param embeddingModelName string = 'text-embedding-3-large'

@description('Embedding vector dimensions — MUST match the deployed model (1536 for -3-small / ada-002, 3072 for -3-large). Propagated to Container Apps as Embedding__Dimensions. See ARCH_EmbeddingConfigOwnership.')
param embeddingDimensions int = 3072

@description('Deploy Azure Key Vault for secret management (set to false for flat env var config)')
param deployKeyVault bool = true

@description('Deploy Log Analytics + Application Insights for monitoring')
param deployMonitoring bool = true

@description('Allow shared key access on storage account (set to false after migrating to Managed Identity)')
param storageAllowSharedKeyAccess bool = true

@description('Additional tags to apply to all resources (merged with default tags)')
param additionalTags object = {}

// ---- Container Apps parameters (opt-in) ----

@description('Deploy Container Apps for API, MCP, and Web')
param deployContainerApps bool = false

@description('Container image tag (e.g., latest, v1.0.0)')
param imageTag string = 'latest'

@description('Container registry server (GHCR)')
param registryServer string = 'ghcr.io'

@description('Container registry username (only needed for private GHCR images)')
param registryUsername string = ''

@description('Container registry password (only needed for private GHCR images)')
@secure()
param registryPassword string = ''

@description('API key for selfhosted authentication')
@secure()
param apiKey string = ''

@description('JWT secret for selfhosted token signing')
@secure()
param jwtSecret string = ''

@description('SuperAdmin password for initial setup. REQUIRED — must be >=12 chars with upper/lower/digit/symbol and must not contain admin/changeme/password/letmein/... (see AuthService.WeakPasswordList). No default: SEC_P0Triage §Rule 2 forbids shipping a known-weak credential with the infrastructure template.')
@secure()
param adminPassword string

@description('SuperAdmin username for first-boot seed. AuthService.EnsureSuperAdminExistsAsync (SEC_P0Triage §Rule 3) hard-requires this; missing param breaks admin login on fresh deploys. Default "admin" matches skill docs + Web UI convention.')
param superAdminUsername string = 'admin'

@description('Chat model deployment name for Container Apps config')
param caDeploymentName string = chatDeploymentName

@description('Embedding deployment name for Container Apps config')
param caEmbeddingDeploymentName string = 'text-embedding-3-large'

@description('Embedding model name for Container Apps config (Embedding__ModelName)')
param caEmbeddingModelName string = embeddingModelName

@description('Embedding vector dimensions for Container Apps config (Embedding__Dimensions). Must match caEmbeddingModelName.')
param caEmbeddingDimensions int = embeddingDimensions

// ============================================================================
// VARIABLES
// ============================================================================

var uniqueSuffix = uniqueString(resourceGroup().id)
var sqlServerName = '${prefix}-sql-${uniqueSuffix}'
var storagePrefix = toLower(take(replace(prefix, '-', ''), 8))
var storageAccountName = toLower('${storagePrefix}st${take(uniqueSuffix, 12)}')

// Key Vault names must be globally unique, 3-24 chars, alphanumeric + hyphens
var kvPrefix = toLower(take(replace(prefix, '-', ''), 8))
var keyVaultName = '${kvPrefix}kv${take(uniqueSuffix, 8)}'
var mcpServiceKey = 'selfhosted-test-mcp-service-key-${uniqueString(resourceGroup().id)}'

// Connection strings (constructed from resource properties + parameters)
var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=McpKnowledge;Persist Security Info=False;User ID=${sqlAdminUsername};Password=${sqlAdminPassword};MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var storageConnectionString = 'DefaultEndpointsProtocol=https;EndpointSuffix=${environment().suffixes.storage};AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'

// App Insights connection string (empty when monitoring not deployed)
var effectiveAppInsightsConnectionString = deployMonitoring ? appInsights.properties.ConnectionString : ''

// Resource tags (applied to all top-level resources)
var defaultTags = {
  project: 'knowz-selfhosted'
  environment: prefix
  'managed-by': 'bicep'
}
var tags = union(defaultTags, additionalTags)

// ============================================================================
// MANAGED IDENTITY
// ============================================================================

// Reuse existing module -- parameterize with prefix instead of environment
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-identity'
  location: location
  tags: tags
}

// ============================================================================
// AZURE AI SEARCH
// ============================================================================

resource searchService 'Microsoft.Search/searchServices@2023-11-01' = {
  name: '${prefix}-search-${searchLocation}'
  location: searchLocation
  tags: tags
  sku: {
    name: searchSku
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
  }
}

// ============================================================================
// AZURE OPENAI + MODEL DEPLOYMENTS
// ============================================================================

resource cognitiveServices 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployOpenAI) {
  name: '${prefix}-openai-${location}'
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    customSubDomainName: '${prefix}-openai-${location}'
  }
}

resource deploymentChat 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI) {
  parent: cognitiveServices
  name: chatDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4'
      version: '2026-03-05'
    }
  }
}

resource deploymentMini 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI) {
  parent: cognitiveServices
  name: 'gpt-5.4-mini'
  sku: {
    name: 'GlobalStandard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4-mini'
      version: '2026-03-17'
    }
  }
  dependsOn: [deploymentChat]
}

resource deploymentEmbedding 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI) {
  parent: cognitiveServices
  name: embeddingDeploymentName
  sku: {
    name: 'Standard'
    capacity: 5
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: '1'
    }
  }
  dependsOn: [deploymentMini]
}

// Existing OpenAI resource reference (when not deploying new and existing name provided).
// NOTE: Bicep cannot resolve a conditional scope at compile time, so cross-RG existing resources
// are handled by the deploy script (selfhosted-deploy.ps1), which performs the lookup and passes
// in resolved endpoint/key via external* parameters. Here we only support same-RG existing.
resource existingOpenAi 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = if (!deployOpenAI && existingOpenAiName != '' && empty(existingOpenAiResourceGroup)) {
  name: existingOpenAiName
}

// 3-tier resolution: deploy new -> use existing (same RG) -> external endpoint
var effectiveOpenAiEndpoint = deployOpenAI ? cognitiveServices.properties.endpoint : (existingOpenAiName != '' && empty(existingOpenAiResourceGroup) ? existingOpenAi.properties.endpoint : externalOpenAiEndpoint)

// ============================================================================
// DOCUMENT INTELLIGENCE (Form Recognizer)
// ============================================================================

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployDocumentIntelligence) {
  name: '${prefix}-docintel-${location}'
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    customSubDomainName: '${prefix}-docintel-${location}'
  }
}

// Existing Document Intelligence resource reference (same RG only — see note on OpenAI).
resource existingDocIntel 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = if (!deployDocumentIntelligence && existingDocIntelName != '' && empty(existingDocIntelResourceGroup)) {
  name: existingDocIntelName
}

// 3-tier resolution: deploy new -> use existing (same RG) -> external endpoint
var effectiveDocIntelEndpoint = deployDocumentIntelligence ? documentIntelligence.properties.endpoint : (existingDocIntelName != '' && empty(existingDocIntelResourceGroup) ? existingDocIntel.properties.endpoint : externalDocIntelEndpoint)

// ============================================================================
// AZURE AI VISION / COMPUTER VISION
// ============================================================================

resource visionAccount 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployVision) {
  name: '${prefix}-vision-${location}'
  location: location
  tags: tags
  kind: 'ComputerVision'
  sku: {
    name: 'S1'
  }
  properties: {
    publicNetworkAccess: 'Enabled'
    customSubDomainName: '${prefix}-vision-${location}'
  }
}

// Existing Vision resource reference (same RG only — see note on OpenAI).
resource existingVision 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = if (!deployVision && existingVisionName != '' && empty(existingVisionResourceGroup)) {
  name: existingVisionName
}

// 3-tier resolution: deploy new -> use existing (same RG) -> external endpoint
var effectiveVisionEndpoint = deployVision ? visionAccount.properties.endpoint : (existingVisionName != '' && empty(existingVisionResourceGroup) ? existingVision.properties.endpoint : externalVisionEndpoint)

// ============================================================================
// SQL SERVER + DATABASE
// ============================================================================

resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminUsername
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlFirewallRuleAzure 'Microsoft.Sql/servers/firewallRules@2022-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Allow all IPs for testing (local dev, CI/CD, EF migrations) — opt-in only
resource sqlFirewallRuleAll 'Microsoft.Sql/servers/firewallRules@2022-05-01-preview' = if (allowAllIps) {
  parent: sqlServer
  name: 'AllowAllIps-TestOnly'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '255.255.255.255'
  }
}

// Single database for self-hosted MCP (NOT the dual KnowzMaster/KnowzKnowledge pattern)
resource mcpDb 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  parent: sqlServer
  name: 'McpKnowledge'
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
    zoneRedundant: false
  }
}

// ============================================================================
// STORAGE ACCOUNT
// ============================================================================

resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: storageAllowSharedKeyAccess
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2022-09-01' = {
  parent: storageAccount
  name: 'default'
}

resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2022-09-01' = {
  parent: blobService
  name: 'selfhosted-files'
  properties: {
    publicAccess: 'None'
  }
}

// ============================================================================
// KEY VAULT (optional — for enterprise secret management)
// ============================================================================

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (deployKeyVault) {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// Key Vault Secrets User role for Managed Identity (read secrets at runtime)
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployKeyVault) {
  name: guid(keyVault.id, managedIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Key Vault Secrets Officer role for the deploying user/SP (create secrets at deploy time)
// RBAC-enabled vaults require explicit data-plane role assignment even for subscription
// Owners. Without this, the deployment fails with 403 on every secretXxx resource.
// Populated by the deploy script from `az ad signed-in-user show --query id -o tsv`.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource kvSecretsOfficerDeployerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployKeyVault && !empty(deployerObjectId)) {
  name: guid(keyVault.id, deployerObjectId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

// ============================================================================
// KEY VAULT SECRETS (IConfiguration hierarchy naming: -- maps to :)
// ============================================================================

resource secretSqlConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'ConnectionStrings--McpDb'
  properties: {
    value: sqlConnectionString
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretSearchEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureAISearch--Endpoint'
  properties: {
    value: 'https://${searchService.name}.search.windows.net'
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretSearchKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureAISearch--ApiKey'
  properties: {
    value: searchService.listAdminKeys().primaryKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureOpenAI--Endpoint'
  properties: {
    value: effectiveOpenAiEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureOpenAI--ApiKey'
  properties: {
    value: deployOpenAI ? cognitiveServices.listKeys().key1 : (existingOpenAiName != '' && empty(existingOpenAiResourceGroup) ? existingOpenAi.listKeys().key1 : externalOpenAiKey)
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiDeploymentName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureOpenAI--DeploymentName'
  properties: {
    value: chatDeploymentName
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiEmbeddingDeploymentName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureOpenAI--EmbeddingDeploymentName'
  properties: {
    value: embeddingDeploymentName
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource kvSecretDocIntelEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureDocumentIntelligence--Endpoint'
  properties: {
    value: effectiveDocIntelEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource kvSecretDocIntelApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureDocumentIntelligence--ApiKey'
  properties: {
    value: deployDocumentIntelligence ? documentIntelligence.listKeys().key1 : (existingDocIntelName != '' && empty(existingDocIntelResourceGroup) ? existingDocIntel.listKeys().key1 : externalDocIntelKey)
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource kvSecretVisionEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureAIVision--Endpoint'
  properties: {
    value: effectiveVisionEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource kvSecretVisionApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'AzureAIVision--ApiKey'
  properties: {
    value: deployVision ? visionAccount.listKeys().key1 : (existingVisionName != '' && empty(existingVisionResourceGroup) ? existingVision.listKeys().key1 : externalVisionKey)
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretStorageConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault) {
  parent: keyVault
  name: 'Storage--Azure--ConnectionString'
  properties: {
    value: storageConnectionString
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretAppInsights 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployKeyVault && deployMonitoring) {
  parent: keyVault
  name: 'ApplicationInsights--ConnectionString'
  properties: {
    value: effectiveAppInsightsConnectionString
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// ============================================================================
// LOG ANALYTICS WORKSPACE (monitoring foundation)
// ============================================================================

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = if (deployMonitoring) {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    workspaceCapping: {
      dailyQuotaGb: 1
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ============================================================================
// APPLICATION INSIGHTS (linked to Log Analytics)
// ============================================================================

resource appInsights 'Microsoft.Insights/components@2020-02-02' = if (deployMonitoring) {
  name: '${prefix}-appinsights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ============================================================================
// DIAGNOSTIC SETTINGS (Key Vault audit logs -> Log Analytics)
// ============================================================================

resource kvDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployKeyVault && deployMonitoring) {
  name: '${prefix}-kv-diagnostics'
  scope: keyVault
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
    ]
  }
}

// ============================================================================
// ROLE ASSIGNMENTS (Managed Identity -> Azure Services)
// ============================================================================

// Built-in role definition IDs
var cognitiveServicesOpenAIContributorRoleId = 'a001fd3d-188f-4b5d-821b-7da978bf7442'
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// Search: Index Data Contributor
resource searchIndexDataRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, managedIdentity.id, searchIndexDataContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Search: Service Contributor
resource searchServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, managedIdentity.id, searchServiceContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// OpenAI: Cognitive Services OpenAI Contributor (only when deploying OpenAI locally)
resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployOpenAI) {
  name: guid(cognitiveServices.id, managedIdentity.id, cognitiveServicesOpenAIContributorRoleId)
  scope: cognitiveServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIContributorRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage: Blob Data Contributor
resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, managedIdentity.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// CONTAINER APPS (conditional — opt-in via deployContainerApps)
// ============================================================================

// Container Apps Environment (linked to Log Analytics for logging)
resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = if (deployContainerApps) {
  name: '${prefix}-cae'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: deployMonitoring ? 'log-analytics' : null
      logAnalyticsConfiguration: deployMonitoring ? {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      } : null
    }
  }
}

// Key Vault secrets for Container Apps (API key + JWT secret)
resource secretApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployContainerApps && deployKeyVault) {
  parent: keyVault
  name: 'SelfHosted--ApiKey'
  properties: {
    value: apiKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretJwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployContainerApps && deployKeyVault) {
  parent: keyVault
  name: 'SelfHosted--JwtSecret'
  properties: {
    value: jwtSecret
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretAdminPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployContainerApps && deployKeyVault) {
  parent: keyVault
  name: 'SelfHosted--SuperAdminPassword'
  properties: {
    value: adminPassword
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// ---- API Container App ----
resource apiContainerApp 'Microsoft.App/containerApps@2024-03-01' = if (deployContainerApps) {
  name: '${prefix}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: empty(registryUsername) ? [] : [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: concat(empty(registryUsername) ? [] : [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ], [
        {
          name: 'sql-connection'
          value: sqlConnectionString
        }
        {
          name: 'openai-endpoint'
          value: effectiveOpenAiEndpoint
        }
        {
          name: 'openai-apikey'
          value: deployOpenAI ? cognitiveServices.listKeys().key1 : (existingOpenAiName != '' && empty(existingOpenAiResourceGroup) ? existingOpenAi.listKeys().key1 : externalOpenAiKey)
        }
        {
          name: 'search-endpoint'
          value: 'https://${searchService.name}.search.windows.net'
        }
        {
          name: 'search-apikey'
          value: searchService.listAdminKeys().primaryKey
        }
        {
          name: 'storage-connection'
          value: storageConnectionString
        }
        {
          name: 'selfhosted-apikey'
          value: apiKey
        }
        {
          name: 'selfhosted-jwtsecret'
          value: jwtSecret
        }
        {
          name: 'selfhosted-adminpassword'
          value: adminPassword
        }
      ])
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${registryServer}/knowz-io/knowz-selfhosted-api:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__McpDb'
              secretRef: 'sql-connection'
            }
            {
              name: 'AzureOpenAI__Endpoint'
              secretRef: 'openai-endpoint'
            }
            {
              name: 'AzureOpenAI__ApiKey'
              secretRef: 'openai-apikey'
            }
            {
              name: 'AzureOpenAI__DeploymentName'
              value: caDeploymentName
            }
            {
              name: 'AzureOpenAI__EmbeddingDeploymentName'
              value: caEmbeddingDeploymentName
            }
            {
              name: 'Embedding__ModelName'
              value: caEmbeddingModelName
            }
            {
              name: 'Embedding__Dimensions'
              value: string(caEmbeddingDimensions)
            }
            {
              name: 'AzureAISearch__Endpoint'
              secretRef: 'search-endpoint'
            }
            {
              name: 'AzureAISearch__ApiKey'
              secretRef: 'search-apikey'
            }
            {
              name: 'AzureAISearch__IndexName'
              value: 'knowledge'
            }
            {
              name: 'Storage__Provider'
              value: 'AzureBlob'
            }
            {
              name: 'Storage__Azure__ConnectionString'
              secretRef: 'storage-connection'
            }
            {
              name: 'Storage__Azure__ContainerName'
              value: 'selfhosted-files'
            }
            {
              name: 'SelfHosted__ApiKey'
              secretRef: 'selfhosted-apikey'
            }
            {
              name: 'SelfHosted__JwtSecret'
              secretRef: 'selfhosted-jwtsecret'
            }
            {
              name: 'SelfHosted__SuperAdminPassword'
              secretRef: 'selfhosted-adminpassword'
            }
            {
              name: 'SelfHosted__SuperAdminUsername'
              value: superAdminUsername
            }
            {
              name: 'Database__AutoMigrate'
              value: 'true'
            }
            {
              name: 'MCP__ServiceKey'
              value: mcpServiceKey
            }
            {
              name: 'AzureDocumentIntelligence__Endpoint'
              value: effectiveDocIntelEndpoint
            }
            {
              name: 'AzureDocumentIntelligence__ApiKey'
              value: deployDocumentIntelligence ? documentIntelligence.listKeys().key1 : (existingDocIntelName != '' && empty(existingDocIntelResourceGroup) ? existingDocIntel.listKeys().key1 : externalDocIntelKey)
            }
            {
              name: 'AzureAIVision__Endpoint'
              value: effectiveVisionEndpoint
            }
            {
              name: 'AzureAIVision__ApiKey'
              value: deployVision ? visionAccount.listKeys().key1 : (existingVisionName != '' && empty(existingVisionResourceGroup) ? existingVision.listKeys().key1 : externalVisionKey)
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

// ---- MCP Container App ----
resource mcpContainerApp 'Microsoft.App/containerApps@2024-03-01' = if (deployContainerApps) {
  name: '${prefix}-mcp'
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: empty(registryUsername) ? [] : [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: empty(registryUsername) ? [] : [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mcp'
          image: '${registryServer}/knowz-io/knowz-selfhosted-mcp:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'Knowz__BaseUrl'
              value: 'https://${apiContainerApp.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'MCP__BackendMode'
              value: 'selfhosted'
            }
            {
              name: 'MCP__ApiKeyValidationEndpoint'
              value: '/api/vaults'
            }
            {
              name: 'Authentication__ValidateApiKey'
              value: 'true'
            }
            {
              name: 'MCP__ServiceKey'
              value: mcpServiceKey
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

// ---- Web Container App ----
resource webContainerApp 'Microsoft.App/containerApps@2024-03-01' = if (deployContainerApps) {
  name: '${prefix}-web'
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: empty(registryUsername) ? [] : [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: empty(registryUsername) ? [] : [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${registryServer}/knowz-io/knowz-selfhosted-web:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'API_UPSTREAM'
              value: apiContainerApp.properties.configuration.ingress.fqdn
            }
            {
              name: 'API_PROTOCOL'
              value: 'https'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

// ============================================================================
// OUTPUTS (non-secret values only — secrets retrieved via Azure CLI in deploy script)
// ============================================================================

// Azure AI Search (non-secret: endpoint, index name, service name)
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'
output searchServiceName string = searchService.name
output searchIndexName string = 'knowledge'

// Azure OpenAI (non-secret: endpoint, deployment names, resource name)
output openAiEndpoint string = effectiveOpenAiEndpoint
output openAiResourceName string = deployOpenAI ? cognitiveServices.name : (existingOpenAiName != '' ? existingOpenAiName : 'external')
output chatDeploymentName string = chatDeploymentName
output miniDeploymentName string = 'gpt-5.4-mini'
output embeddingDeploymentName string = embeddingDeploymentName

// Document Intelligence (non-secret: endpoint, resource name)
output documentIntelligenceEndpoint string = effectiveDocIntelEndpoint
output documentIntelligenceName string = deployDocumentIntelligence ? documentIntelligence.name : (existingDocIntelName != '' ? existingDocIntelName : 'external')

// Azure AI Vision (non-secret: endpoint, resource name)
output visionEndpoint string = effectiveVisionEndpoint
output visionName string = deployVision ? visionAccount.name : (existingVisionName != '' ? existingVisionName : 'external')

// AI Configuration Summary (mode per service)
output aiConfigurationSummary object = {
  openai: deployOpenAI ? 'deployed' : (existingOpenAiName != '' ? 'existing:${existingOpenAiName}' : 'external')
  vision: deployVision ? 'deployed' : (existingVisionName != '' ? 'existing:${existingVisionName}' : 'external')
  docIntel: deployDocumentIntelligence ? 'deployed' : (existingDocIntelName != '' ? 'existing:${existingDocIntelName}' : 'external')
}

// SQL Database (non-secret: FQDN, database name, server name)
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = 'McpKnowledge'
output sqlServerName string = sqlServerName

// Storage (non-secret: account name, blob endpoint)
output storageAccountName string = storageAccount.name
output storageBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob

// Managed Identity
output managedIdentityId string = managedIdentity.id
output managedIdentityPrincipalId string = managedIdentity.properties.principalId
output managedIdentityClientId string = managedIdentity.properties.clientId

// Key Vault (non-secret: vault name and URI for configuration)
output keyVaultName string = deployKeyVault ? keyVault.name : ''
output keyVaultUri string = deployKeyVault ? keyVault.properties.vaultUri : ''

// Monitoring
output logAnalyticsWorkspaceId string = deployMonitoring ? logAnalytics.id : ''
output logAnalyticsWorkspaceName string = deployMonitoring ? logAnalytics.name : ''
output appInsightsName string = deployMonitoring ? appInsights.name : ''
output appInsightsConnectionString string = deployMonitoring ? appInsights.properties.ConnectionString : ''
output appInsightsInstrumentationKey string = deployMonitoring ? appInsights.properties.InstrumentationKey : ''

// Container Apps
output apiContainerAppFqdn string = deployContainerApps ? apiContainerApp.properties.configuration.ingress.fqdn : ''
output mcpContainerAppFqdn string = deployContainerApps ? mcpContainerApp.properties.configuration.ingress.fqdn : ''
output webContainerAppFqdn string = deployContainerApps ? webContainerApp.properties.configuration.ingress.fqdn : ''
