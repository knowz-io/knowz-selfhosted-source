// infrastructure/selfhosted-enterprise.bicep
// Enterprise-grade Bicep template for Knowz Self-Hosted deployment.
// Designed to comply with Azure Landing Zone CSPM/CSE security policies.
//
// Key differences from standard template (selfhosted-test.bicep):
//   - VNet with private endpoints for ALL data-plane services
//   - Azure Front Door (Premium) with WAF policy + managed rule sets as single ingress
//   - AAD-only SQL authentication (no SQL admin password)
//   - Storage: GRS, network ACLs deny-all (shared key access until MI blob support)
//   - Key Vault: purge protection, RBAC auth, private endpoint
//   - All Cognitive Services: publicNetworkAccess=Disabled
//   - SQL: publicNetworkAccess=Disabled, Defender, auditing, LTR backup
//   - Container Apps Environment: internal-only (VNet-injected)
//   - Private DNS zones for all 6 service types
//   - Log Analytics + diagnostics on all major resources
//
// Usage:
//   az group create -n rg-knowz-enterprise -l eastus2
//   az deployment group create -g rg-knowz-enterprise \
//     -f infrastructure/selfhosted-enterprise.bicep \
//     -p prefix='kze' location='eastus2' \
//        aadAdminObjectId='<guid>' aadAdminDisplayName='DBA Group' \
//        adminPassword='<secure>'

// ============================================================================
// PARAMETERS
// ============================================================================

@description('Resource name prefix (2-8 chars, used for all resource naming)')
@minLength(2)
@maxLength(8)
param prefix string

@description('Object ID of the deploying user/SP (grants Key Vault Secrets Officer for secret creation). Populated by deploy script.')
param deployerObjectId string = ''

@description('Azure region for all resources')
param location string

@description('AAD Object ID for SQL administrator (user or group)')
param aadAdminObjectId string

@description('AAD display name for SQL administrator')
param aadAdminDisplayName string

@description('SuperAdmin password for Knowz application initial setup')
@secure()
param adminPassword string

@description('Tags applied to all resources (organizations typically add their own via Azure Policy)')
param tags object = {}

@description('Deploy Azure OpenAI (false = use external OpenAI endpoint)')
param deployOpenAI bool = true

@description('External OpenAI endpoint (required when deployOpenAI is false)')
param externalOpenAiEndpoint string = ''

@description('External OpenAI API key (required when deployOpenAI is false)')
@secure()
param externalOpenAiKey string = ''

@description('Name of existing Azure OpenAI resource to reuse (leave empty to deploy new or use external)')
param existingOpenAiName string = ''

@description('Resource group of existing OpenAI resource (defaults to current RG)')
param existingOpenAiResourceGroup string = ''

@description('Chat model deployment name (must match appsettings DeploymentName)')
param chatDeploymentName string = 'gpt-5.4'

@description('Embedding deployment name (must match appsettings EmbeddingDeploymentName)')
param embeddingDeploymentName string = 'text-embedding-3-large'

@description('Embedding model name (text-embedding-3-large or text-embedding-3-small). Propagated to Container App as Embedding__ModelName.')
param embeddingModelNameParam string = 'text-embedding-3-large'

@description('Embedding vector dimensions — MUST match the deployed model (1536 for -3-small / ada-002, 3072 for -3-large). Propagated to Container App as Embedding__Dimensions. See ARCH_EmbeddingConfigOwnership.')
param embeddingDimensions int = 3072

@description('Deploy Azure AI Vision for image/diagram analysis (caption, tags, objects, OCR)')
param deployVision bool = true

@description('External Azure AI Vision endpoint (required when deployVision is false)')
param externalVisionEndpoint string = ''

@description('External Azure AI Vision API key (required when deployVision is false)')
@secure()
param externalVisionKey string = ''

@description('Name of existing Azure AI Vision resource to reuse (leave empty to deploy new or use external)')
param existingVisionName string = ''

@description('Resource group of existing Vision resource (defaults to current RG)')
param existingVisionResourceGroup string = ''

@description('Deploy Azure Document Intelligence for advanced document extraction')
param deployDocumentIntelligence bool = true

@description('External Document Intelligence endpoint (required when deployDocumentIntelligence is false)')
param externalDocIntelEndpoint string = ''

@description('External Document Intelligence API key (required when deployDocumentIntelligence is false)')
@secure()
param externalDocIntelKey string = ''

@description('Name of existing Document Intelligence resource to reuse (leave empty to deploy new or use external)')
param existingDocIntelName string = ''

@description('Resource group of existing Document Intelligence resource (defaults to current RG)')
param existingDocIntelResourceGroup string = ''

@description('Azure AI Search SKU (enterprise requires standard or higher for private endpoint support)')
@allowed(['standard', 'standard2', 'standard3'])
param searchSku string = 'standard'

@description('Container image tag (e.g., latest, v1.0.0)')
param imageTag string = 'latest'

@description('Container registry username (empty = public GHCR)')
param registryUsername string = ''

@description('Container registry password (empty = public GHCR)')
@secure()
param registryPassword string = ''

@secure()
@description('API key for the selfhosted API. Auto-generated if not provided.')
param apiKey string = newGuid()

@secure()
@description('JWT signing secret. Must be at least 32 characters. Auto-generated if not provided.')
param jwtSecret string = '${newGuid()}${newGuid()}'

// ============================================================================
// BYO INFRASTRUCTURE (optional; all empty defaults preserve back-compat)
// See SH_ENTERPRISE_BYO_INFRA.md for spec + rationale.
// ============================================================================

@description('BYO Container Apps delegated subnet resource ID. Empty = auto-provision VNet.')
param byoVnetSubnetId string = ''

@description('BYO non-delegated PE subnet resource ID. Empty AND peSubnetAddressPrefix empty = auto-provision inside BYO VNet.')
param byoVnetPeSubnetId string = ''

@description('CIDR to auto-create PE subnet inside BYO VNet (used when byoVnetPeSubnetId empty).')
param peSubnetAddressPrefix string = ''

@description('Auto-provision VNet when BYO inputs absent. Set false for strict BYO mode with fail-fast asserts.')
param autoProvisionVnet bool = true

@description('BYO Key Vault resource ID (full /subscriptions/.../Microsoft.KeyVault/vaults/<name>). Empty = create new per-env KV.')
param byoKeyVaultId string = ''

@description('Central Log Analytics workspace resource ID. Empty = per-env LAW.')
param centralLogAnalyticsId string = ''

@description('Customer-provisioned Azure OpenAI resource ID. Empty = deploy local OpenAI (gated on deployOpenAI) or use externalOpenAiEndpoint.')
param existingOpenAiResourceId string = ''

@description('External ACR name (not FQDN) for air-gapped/policy-restricted pulls. Empty = pull from ghcr.io.')
param externalAcrName string = ''

@description('External ACR resource group (required when externalAcrName non-empty).')
param externalAcrResourceGroup string = ''

// ============================================================================
// ENTERPRISE HARDENING PARAMS (SH_ENTERPRISE_BICEP_HARDENING.md)
// ============================================================================

@allowed(['Detection', 'Prevention'])
@description('WAF policy mode. Default Prevention for enterprise tier (blocks attacks instead of only logging).')
param wafMode string = 'Prevention'

@allowed(['Basic', 'S0', 'S1', 'S2', 'S3', 'P1', 'P2'])
@description('SQL database SKU name. Default S1 for enterprise workload. Basic@2GB dies at ~5 concurrent enrichment jobs.')
param sqlDatabaseSkuName string = 'S1'

@description('SQL database max size in bytes. Default 250GB for S1 tier.')
param sqlDatabaseMaxSizeBytes int = 268435456000

@description('Container image registry prefix (e.g., knowz-io for ghcr.io/knowz-io/*). Customize for vendored builds.')
param imageRepositoryPrefix string = 'knowz-io'

@description('Enforce strict ingestion PE (AMPLS) for App Insights. Default false — AMPLS deferred (see top-of-file note).')
param strictIngestion bool = false

@secure()
@description('MCP service key. Default newGuid() on first deploy; pass from KV on rerun. Do NOT use uniqueString() — that is deterministic per RG name.')
param mcpServiceKey string = newGuid()

// ============================================================================
// VARIABLES
// ============================================================================

// uniqueSuffix is OK for resource naming (RG-deterministic is intended for naming).
// DO NOT use uniqueString() for secrets — use @secure() params seeded from newGuid() instead.
var uniqueSuffix = uniqueString(resourceGroup().id)
var sqlServerName = '${prefix}-sql-${uniqueSuffix}'
var storagePrefix = toLower(take(replace(prefix, '-', ''), 8))
var storageAccountName = toLower('${storagePrefix}st${take(uniqueSuffix, 12)}')
var kvPrefix = toLower(take(replace(prefix, '-', ''), 8))
var keyVaultName = '${kvPrefix}kv${take(uniqueSuffix, 8)}'

// Effective registry: switches to customer ACR when externalAcrName provided (air-gapped / policy deploys).
// Uses azurecr.io literal — sovereign cloud ACR domains are customer-specific and would require
// a separate `externalAcrFqdn` override param (deferred per SH_ENTERPRISE_BYO_INFRA.md §Rule 6).
var registryServer = !empty(externalAcrName) ? '${externalAcrName}.azurecr.io' : 'ghcr.io'
var registryPath = '${registryServer}/${imageRepositoryPrefix}'

// Effective registries[] for container-app configuration:
//   BYO ACR: MI-auth via `identity:`, no password secret ref
//   GHCR with PAT: legacy username/passwordSecretRef flow
//   GHCR public: empty array (anonymous pulls — only works for public repos)
var effectiveRegistries = !empty(externalAcrName) ? [
  {
    server: registryServer
    identity: managedIdentity.id
  }
] : (empty(registryUsername) ? [] : [
  {
    server: registryServer
    username: registryUsername
    passwordSecretRef: 'registry-password'
  }
])
var effectiveRegistrySecrets = (!empty(externalAcrName) || empty(registryUsername)) ? [] : [
  {
    name: 'registry-password'
    value: registryPassword
  }
]

// Resolve effective embedding model name: explicit param wins; default matches text-embedding-3-large.
var embeddingModelName = empty(embeddingModelNameParam) ? 'text-embedding-3-large' : embeddingModelNameParam

// ============================================================================
// BYO VNET REFERENCES + EFFECTIVE SUBNET IDS
// ============================================================================
// When byoVnetSubnetId is provided, we skip the VNet resource block below and
// thread the customer-supplied IDs through downstream subnet consumers instead.
// The `alz-assert-pe-subnet-sh` guard (below, in the assertion section) fails
// template evaluation when strict BYO mode is requested without required inputs.

// Parse BYO subnet IDs: /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>
// Split index reference: [0]='' [1]='subscriptions' [2]=<sub> [3]='resourceGroups' [4]=<rg> [5]='providers' [6]='Microsoft.Network' [7]='virtualNetworks' [8]=<vnet> [9]='subnets' [10]=<subnet>
var byoVnetSubscriptionId = !empty(byoVnetSubnetId) ? split(byoVnetSubnetId, '/')[2] : ''
var byoVnetResourceGroupName = !empty(byoVnetSubnetId) ? split(byoVnetSubnetId, '/')[4] : ''
var byoVnetName = !empty(byoVnetSubnetId) ? split(byoVnetSubnetId, '/')[8] : ''

// Resource tags
var defaultTags = {
  project: 'knowz-selfhosted'
  environment: prefix
  tier: 'enterprise'
  'managed-by': 'bicep'
}
var effectiveTags = union(defaultTags, tags)

// Effective endpoints (local, BYO existing, or external string)
// Priority: BYO existing resource → local deployment → external endpoint string
var effectiveOpenAiEndpoint = !empty(existingOpenAiResourceId)
  ? existingOpenAi.properties.endpoint
  : (deployOpenAI ? cognitiveServices.properties.endpoint : externalOpenAiEndpoint)
var effectiveVisionEndpoint = deployVision ? visionService.properties.endpoint : externalVisionEndpoint
var effectiveDocIntelEndpoint = deployDocumentIntelligence ? documentIntelligence.properties.endpoint : externalDocIntelEndpoint

// SQL connection string using AAD Managed Identity authentication (no password)
var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=McpKnowledge;Authentication=Active Directory Managed Identity;User Id=${managedIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

// Storage:Azure:AccountUrl pattern — app (BlobServiceClient) uses DefaultAzureCredential
// via managed identity (commit 3a690a4ca — Builder B app-side MI swap). AccountKey-based
// connection string retained as KV secret for legacy code paths (e.g. Azure Functions
// that don't use DI registration), but is no longer the primary auth vector.
// See SH_ENTERPRISE_MI_SWAP.md §2.2 for the full client-swap matrix.
var storageBlobEndpoint = storageAccount.properties.primaryEndpoints.blob

// App Insights connection string
var appInsightsConnectionString = appInsights.properties.ConnectionString

// ============================================================================
// NETWORK SECURITY GROUPS
// ============================================================================

resource containerAppsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: '${prefix}-container-apps-nsg'
  location: location
  tags: effectiveTags
  properties: {
    securityRules: [
      {
        name: 'AllowFrontDoorInbound'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: 'AzureFrontDoor.Backend'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'AllowAllOutbound'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource privateEndpointsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: '${prefix}-private-endpoints-nsg'
  location: location
  tags: effectiveTags
  properties: {
    securityRules: [
      {
        name: 'DenyAllInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'AllowAllOutbound'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// ============================================================================
// ALZ FAIL-FAST ASSERTIONS (template-evaluation errors — no mutation before failure)
// ============================================================================
// Ported from infrastructure/modules/assert.bicep pattern. When strict BYO mode is
// requested (autoProvisionVnet=false), these modules fire at compile time if required
// BYO inputs are absent — prevents silent unreachable-PE deployments per the April 2026
// partner outage postmortem.

module alzAssertSubnet 'modules/assert.bicep' = if (!autoProvisionVnet && empty(byoVnetSubnetId)) {
  name: 'alz-assert-subnet-sh'
  params: {
    message: 'Enterprise self-hosted: byoVnetSubnetId required when autoProvisionVnet=false. Provide a pre-existing Container Apps delegated subnet.'
  }
}

module alzAssertPeSubnet 'modules/assert.bicep' = if (!autoProvisionVnet && empty(byoVnetPeSubnetId) && empty(peSubnetAddressPrefix)) {
  name: 'alz-assert-pe-subnet-sh'
  params: {
    message: 'Enterprise self-hosted: either byoVnetPeSubnetId or peSubnetAddressPrefix required when autoProvisionVnet=false. publicNetworkAccess is disabled on SQL/Storage/KV/OpenAI/Search; services unreachable without a PE subnet.'
  }
}

module alzAssertStrictIngestion 'modules/assert.bicep' = if (strictIngestion) {
  name: 'alz-assert-strict-ingestion-sh'
  params: {
    message: 'strictIngestion=true requires AMPLS deployment which is not yet implemented. Set strictIngestion=false or file a feature request. App Insights uses public ingestion endpoint by design — see top-of-file note.'
  }
}

// ============================================================================
// VIRTUAL NETWORK
// ============================================================================
// Auto-provisioning path: only runs when no BYO VNet supplied AND autoProvisionVnet=true.
// BYO path: `byoVnet` existing reference below pulls the customer-supplied VNet into scope
// for DNS zone linking + PE subnet auto-creation (via modules/pe-subnet.bicep).

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = if (empty(byoVnetSubnetId) && autoProvisionVnet) {
  name: '${prefix}-vnet'
  location: location
  tags: effectiveTags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'container-apps'
        properties: {
          addressPrefix: '10.0.0.0/23'
          networkSecurityGroup: {
            id: containerAppsNsg.id
          }
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: '10.0.2.0/24'
          networkSecurityGroup: {
            id: privateEndpointsNsg.id
          }
        }
      }
    ]
  }
}

// BYO VNet — resolved in the customer's networking RG when byoVnetSubnetId is provided.
resource byoVnet 'Microsoft.Network/virtualNetworks@2023-11-01' existing = if (!empty(byoVnetSubnetId)) {
  name: byoVnetName
  scope: resourceGroup(byoVnetSubscriptionId, byoVnetResourceGroupName)
}

// Optional: auto-create a PE subnet inside the BYO VNet (when byoVnetPeSubnetId empty but peSubnetAddressPrefix provided).
module byoPeSubnet 'modules/pe-subnet.bicep' = if (!empty(byoVnetSubnetId) && empty(byoVnetPeSubnetId) && !empty(peSubnetAddressPrefix)) {
  name: 'byo-pe-subnet'
  scope: resourceGroup(byoVnetSubscriptionId, byoVnetResourceGroupName)
  params: {
    vnetName: byoVnetName
    subnetName: '${prefix}-pe'
    addressPrefix: peSubnetAddressPrefix
  }
}

// Effective IDs — downstream consumers use these instead of the concrete `vnet.*` references.
var effectiveContainerAppsSubnetId = !empty(byoVnetSubnetId) ? byoVnetSubnetId : vnet.properties.subnets[0].id
var effectivePeSubnetId = !empty(byoVnetPeSubnetId)
  ? byoVnetPeSubnetId
  : (!empty(byoVnetSubnetId) && !empty(peSubnetAddressPrefix))
      ? byoPeSubnet.outputs.subnetId
      : vnet.properties.subnets[1].id
var effectiveVnetId = !empty(byoVnetSubnetId) ? byoVnet.id : vnet.id

// ============================================================================
// PRIVATE DNS ZONES (6 zones for all private endpoint services)
// ============================================================================

var privateDnsZones = [
  'privatelink.database.windows.net'
  'privatelink.blob.core.windows.net'
  'privatelink.vaultcore.azure.net'
  'privatelink.openai.azure.com'
  'privatelink.cognitiveservices.azure.com'
  'privatelink.search.windows.net'
]

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zone in privateDnsZones: {
  name: zone
  location: 'global'
  tags: effectiveTags
}]

resource dnsZoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zone, i) in privateDnsZones: {
  parent: dnsZones[i]
  name: '${prefix}-link-${i}'
  location: 'global'
  tags: effectiveTags
  properties: {
    virtualNetwork: {
      // When BYO VNet, link zones to the customer VNet; otherwise link to the auto-provisioned VNet.
      id: effectiveVnetId
    }
    registrationEnabled: false
  }
}]

// ============================================================================
// MANAGED IDENTITY
// ============================================================================

resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-identity'
  location: location
  tags: effectiveTags
}

// ============================================================================
// LOG ANALYTICS WORKSPACE (local) OR CENTRAL LAW (cross-RG reference)
// ============================================================================
// When centralLogAnalyticsId is empty: create a per-env workspace.
// When non-empty: use the customer's central workspace for all diagnostics + CAE logs.
// Per SH_ENTERPRISE_BYO_INFRA §Rule 4 — enterprise customers typically centralize
// log aggregation for unified query/billing/retention policy.

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = if (empty(centralLogAnalyticsId)) {
  name: '${prefix}-logs'
  location: location
  tags: effectiveTags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 90
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    workspaceCapping: {
      dailyQuotaGb: 5
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Central LAW reference — parsed from the customer-supplied resource ID.
var centralLawSubscriptionId = !empty(centralLogAnalyticsId) ? split(centralLogAnalyticsId, '/')[2] : ''
var centralLawResourceGroupName = !empty(centralLogAnalyticsId) ? split(centralLogAnalyticsId, '/')[4] : ''
var centralLawName = !empty(centralLogAnalyticsId) ? split(centralLogAnalyticsId, '/')[8] : ''

resource centralLaw 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = if (!empty(centralLogAnalyticsId)) {
  name: centralLawName
  scope: resourceGroup(centralLawSubscriptionId, centralLawResourceGroupName)
}

// Effective LAW values — all diagnostic settings + CAE logs thread through these.
// listKeys() on the workspace is still required by the CAE API
// (appLogsConfiguration.destination='log-analytics' expects a shared key). When Microsoft
// ships MI-auth for CAE log sinks, this listKeys() call becomes removable (tracked as
// external blocker D7 in SH_ENTERPRISE_BICEP_HARDENING spec).
var effectiveLawId = !empty(centralLogAnalyticsId) ? centralLaw.id : logAnalytics.id
var effectiveLawCustomerId = !empty(centralLogAnalyticsId) ? centralLaw.properties.customerId : logAnalytics.properties.customerId
var effectiveLawKey = !empty(centralLogAnalyticsId) ? centralLaw.listKeys().primarySharedKey : logAnalytics.listKeys().primarySharedKey

// ============================================================================
// APPLICATION INSIGHTS
// ============================================================================
// Ingestion endpoint is PUBLIC by design (AMPLS deferred — see TRUSTED CONFIG SOURCES
// note below + SH_ENTERPRISE_BICEP_HARDENING §Rule 8). Customers with strict egress policy
// set strictIngestion=true (currently fails fast via alz-assert-strict-ingestion-sh
// until AMPLS lands in a follow-up WorkGroup).

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appinsights'
  location: location
  tags: effectiveTags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: effectiveLawId
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ============================================================================
// STORAGE ACCOUNT (hardened: GRS, no public blob, deny-all network, shared key until MI support)
// ============================================================================

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: effectiveTags
  sku: {
    name: 'Standard_GRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    // MI-swap landed (Builder B commit 3a690a4c): BlobServiceClient uses DefaultAzureCredential
    // + Storage:Azure:AccountUrl (no SAS / no account key). Shared-key access now disabled.
    // Any legacy code path that still relies on AccountKey will fail-fast at runtime — that's
    // intentional; customers should report those paths so we can finish the swap.
    allowSharedKeyAccess: false
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'selfhosted-files'
  properties: {
    publicAccess: 'None'
  }
}

// Data Protection key ring storage — ASP.NET Core persists its key ring here so
// cookies/tokens survive container restarts. Wrapped by the KV key `dp-master-key`
// (below) for defense-in-depth. App-side wiring landed in Builder A commit 4b06cb3e4.
// See SH_ENTERPRISE_BICEP_HARDENING §Rule 9 / SH_ENTERPRISE_SECURITY_HARDENING §Rule 8.
resource dpKeysContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dp-keys'
  properties: {
    publicAccess: 'None'
  }
}

// Private endpoint: Storage Blob
resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${prefix}-pe-storage'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-storage'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource storageDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: storagePrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[1].id // privatelink.blob.core.windows.net
        }
      }
    ]
  }
}

// Storage diagnostics
resource storageDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${prefix}-storage-diagnostics'
  scope: blobService
  properties: {
    workspaceId: effectiveLawId
    logs: [
      {
        category: 'StorageRead'
        enabled: true
      }
      {
        category: 'StorageWrite'
        enabled: true
      }
      {
        category: 'StorageDelete'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Transaction'
        enabled: true
      }
    ]
  }
}

// ============================================================================
// SQL SERVER + DATABASE (AAD-only auth, private endpoint, Defender, auditing)
// ============================================================================

// The managed identity is set as SQL AAD admin so it can run EF Core migrations at startup
// and authenticate at runtime via Active Directory Managed Identity.
// The user-provided aadAdminObjectId is granted access via a deployment script below.
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: effectiveTags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Application'
      login: managedIdentity.name
      sid: managedIdentity.properties.principalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// SH_ENTERPRISE_BICEP_HARDENING §Rule 2: SKU raised from Basic@2GB to S1@250GB.
// Basic@2GB dies at ~5 concurrent enrichment jobs (confirmed against selfhosted-test).
// Tier / capacity mapping is derived from the SKU name via sqlSkuTier / sqlSkuCapacity vars.
var sqlSkuTier = startsWith(sqlDatabaseSkuName, 'P') ? 'Premium' : (startsWith(sqlDatabaseSkuName, 'S') ? 'Standard' : 'Basic')
var sqlSkuCapacity = sqlDatabaseSkuName == 'Basic'
  ? 5
  : (sqlDatabaseSkuName == 'S0' ? 10 : (sqlDatabaseSkuName == 'S1' ? 20 : (sqlDatabaseSkuName == 'S2' ? 50 : (sqlDatabaseSkuName == 'S3' ? 100 : (sqlDatabaseSkuName == 'P1' ? 125 : 250)))))

resource mcpDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'McpKnowledge'
  location: location
  tags: effectiveTags
  sku: {
    name: sqlDatabaseSkuName
    tier: sqlSkuTier
    capacity: sqlSkuCapacity
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: sqlDatabaseMaxSizeBytes
    zoneRedundant: false
  }
}

// Short-term backup retention: 35 days
resource sqlBackupShortTerm 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01-preview' = {
  parent: mcpDb
  name: 'default'
  properties: {
    retentionDays: 35
  }
}

// Long-term backup retention: weekly for 26 weeks (6 months)
resource sqlBackupLongTerm 'Microsoft.Sql/servers/databases/backupLongTermRetentionPolicies@2023-08-01-preview' = {
  parent: mcpDb
  name: 'default'
  properties: {
    weeklyRetention: 'P26W'
  }
}

// SQL Server auditing to Log Analytics
resource sqlAudit 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview' = {
  parent: sqlServer
  name: 'default'
  properties: {
    state: 'Enabled'
    isAzureMonitorTargetEnabled: true
  }
}

// Defender for SQL
resource sqlDefender 'Microsoft.Sql/servers/advancedThreatProtectionSettings@2023-08-01-preview' = {
  parent: sqlServer
  name: 'Default'
  properties: {
    state: 'Enabled'
  }
}

// SQL Vulnerability Assessment (requires Defender for SQL)
resource sqlVulnAssessment 'Microsoft.Sql/servers/vulnerabilityAssessments@2023-08-01-preview' = {
  parent: sqlServer
  name: 'default'
  properties: {
    storageContainerPath: '${storageAccount.properties.primaryEndpoints.blob}vulnerability-assessments'
    recurringScans: {
      isEnabled: true
      emailSubscriptionAdmins: true
    }
  }
  dependsOn: [sqlDefender]
}

// Deployment script: grant the user-provided AAD admin access to the SQL database.
// The managed identity is the SQL AAD admin (for migrations), so we use it to run this script
// which grants db_owner to the user-specified admin group/user.
resource sqlPermissionScript 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: '${prefix}-sql-permission-script'
  location: location
  tags: effectiveTags
  kind: 'AzurePowerShell'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  properties: {
    azPowerShellVersion: '11.0'
    retentionInterval: 'PT1H'
    timeout: 'PT10M'
    arguments: '-SqlServerFqdn ${sqlServer.properties.fullyQualifiedDomainName} -DatabaseName McpKnowledge -AadAdminObjectId ${aadAdminObjectId} -AadAdminDisplayName \'${aadAdminDisplayName}\''
    scriptContent: '''
      param($SqlServerFqdn, $DatabaseName, $AadAdminObjectId, $AadAdminDisplayName)
      Install-Module -Name SqlServer -Force -AllowClobber -Scope CurrentUser
      $token = (Get-AzAccessToken -ResourceUrl "https://database.windows.net/").Token

      # SEC_P0Triage §Rule 5: parameterize to prevent SQL injection via the
      # customer-supplied aadAdminDisplayName / aadAdminObjectId Bicep inputs.
      # Previously these were string-interpolated into T-SQL with a literal
      # identifier, so a crafted display name like
      #   O'Brien]; DROP TABLE Users;--
      # would execute as SQL. sqlcmd variables ($(Name)) are substituted safely
      # and are rejected if they contain reserved chars such as ']'.
      # QUOTENAME wraps the identifier defensively in case a future change
      # reintroduces dynamic identifier usage.

      # Defensive input validation: display name must match the same regex the
      # portal createUiDefinition uses. Reject anything else before the script
      # touches SQL at all.
      if ($AadAdminDisplayName -notmatch "^[A-Za-z0-9 _\-\.@']{1,128}$") {
        throw "AadAdminDisplayName contains disallowed characters: '$AadAdminDisplayName'"
      }
      if ($AadAdminObjectId -notmatch "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") {
        throw "AadAdminObjectId is not a GUID: '$AadAdminObjectId'"
      }

      $query = @"
        IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = $(AadAdminDisplayName))
        BEGIN
          DECLARE @stmt nvarchar(max) = N'CREATE USER ' + QUOTENAME($(AadAdminDisplayName)) +
            N' WITH SID = ' + CONVERT(varchar(36), CAST($(AadAdminObjectId) AS uniqueidentifier)) +
            N', TYPE = E;';
          EXEC sp_executesql @stmt;
          DECLARE @role nvarchar(max) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME($(AadAdminDisplayName)) + N';';
          EXEC sp_executesql @role;
        END
"@
      # SEC_P0Triage §Rule 5 (defense-in-depth): -QueryTimeout 60 is the
      # last-resort guard against a pathological identifier that slips past
      # the PowerShell whitelist regex above and triggers locking on
      # sys.database_principals. Without this, a hostile input that's
      # whitelist-valid but computationally expensive could stall the deploy.
      Invoke-Sqlcmd `
        -ServerInstance $SqlServerFqdn `
        -Database $DatabaseName `
        -AccessToken $token `
        -Variable @("AadAdminDisplayName=$AadAdminDisplayName", "AadAdminObjectId=$AadAdminObjectId") `
        -QueryTimeout 60 `
        -Query $query
    '''
  }
  dependsOn: [mcpDb]
}

// SQL diagnostics (database-level)
resource sqlDbDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${prefix}-sqldb-diagnostics'
  scope: mcpDb
  properties: {
    workspaceId: effectiveLawId
    logs: [
      {
        category: 'SQLSecurityAuditEvents'
        enabled: true
      }
      {
        category: 'QueryStoreRuntimeStatistics'
        enabled: true
      }
      {
        category: 'Errors'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'Basic'
        enabled: true
      }
    ]
  }
}

// Private endpoint: SQL Server
resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${prefix}-pe-sql'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-sql'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: [
            'sqlServer'
          ]
        }
      }
    ]
  }
}

resource sqlDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: sqlPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[0].id // privatelink.database.windows.net
        }
      }
    ]
  }
}

// ============================================================================
// KEY VAULT (hardened: purge protection, RBAC, private endpoint)
// ============================================================================

// ============================================================================
// KEY VAULT (local creation) OR BYO KEY VAULT (cross-RG reference)
// ============================================================================
// When byoKeyVaultId is empty: create a per-env vault locally and write all secrets.
// When byoKeyVaultId is non-empty: skip creation, grant MI the Secrets User role
// cross-RG on the customer's vault. Customer pre-populates secrets per the runbook
// (deployer SP needs Key Vault Secrets Officer pre-granted on the BYO vault).

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (empty(byoKeyVaultId)) {
  name: keyVaultName
  location: location
  tags: effectiveTags
  properties: {
    sku: {
      family: 'A'
      // Enterprise template favors premium: HSM-protected keys become available and the
      // SKU itself has no base fee (only HSM keys bill). Non-enterprise stays standard.
      name: 'premium'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    }
  }
}

// BYO KV existing reference — scope split: [2]=subId [4]=rgName [8]=kvName
var byoKvSubscriptionId = !empty(byoKeyVaultId) ? split(byoKeyVaultId, '/')[2] : ''
var byoKvResourceGroupName = !empty(byoKeyVaultId) ? split(byoKeyVaultId, '/')[4] : ''
var byoKvName = !empty(byoKeyVaultId) ? split(byoKeyVaultId, '/')[8] : ''

resource byoKv 'Microsoft.KeyVault/vaults@2023-07-01' existing = if (!empty(byoKeyVaultId)) {
  name: byoKvName
  scope: resourceGroup(byoKvSubscriptionId, byoKvResourceGroupName)
}

// Effective KV metadata — used by container-app secret refs (keyVaultUrl).
var effectiveKvName = !empty(byoKeyVaultId) ? byoKvName : keyVaultName
var effectiveKvUri = !empty(byoKeyVaultId)
  ? 'https://${byoKvName}${environment().suffixes.keyvaultDns}/'
  : 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/'

// Role IDs — scoped as vars (consumed by both local + BYO paths).
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

// Key Vault Secrets User role for Managed Identity (read secrets at runtime) — LOCAL KV
resource kvSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (empty(byoKeyVaultId)) {
  name: guid(keyVault.id, managedIdentity.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// BYO KV: cross-RG role grant for MI — uses cross-rg-role.bicep.
// Customer deployer SP must have Key Vault Secrets Officer pre-granted on the BYO vault.
module byoKvSecretsUserRole 'modules/cross-rg-role.bicep' = if (!empty(byoKeyVaultId)) {
  name: 'byo-kv-mi-secrets-user'
  scope: resourceGroup(byoKvSubscriptionId, byoKvResourceGroupName)
  params: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    targetResourceId: byoKeyVaultId
  }
}

// Key Vault Secrets Officer role for the deploying user/SP (create secrets at deploy time).
// RBAC-enabled vaults require explicit data-plane role assignment even for subscription
// Owners. Without this, the deployment fails with 403 on every secretXxx resource.
// Populated by the deploy script from `az ad signed-in-user show --query id -o tsv`.
// BYO KV: caller must pre-grant this role; we do NOT emit it cross-RG (deployer owns that).
resource kvSecretsOfficerDeployerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId) && empty(byoKeyVaultId)) {
  name: guid(keyVault.id, deployerObjectId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

// Private endpoint: Key Vault — only for LOCAL KV. BYO KV has its own PE (customer-managed).
resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (empty(byoKeyVaultId)) {
  name: '${prefix}-pe-kv'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-kv'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource kvDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (empty(byoKeyVaultId)) {
  parent: kvPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[2].id // privatelink.vaultcore.azure.net
        }
      }
    ]
  }
}

// Key Vault diagnostics — only for LOCAL KV. BYO KV has customer-managed diagnostics.
resource kvDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (empty(byoKeyVaultId)) {
  name: '${prefix}-kv-diagnostics'
  scope: keyVault
  properties: {
    workspaceId: effectiveLawId
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
      }
      {
        category: 'AzurePolicyEvaluationDetails'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ============================================================================
// KEY VAULT SECRETS
// ============================================================================
// TRUSTED CONFIG SOURCES (KV-only; DatabaseConfigurationSource MUST skip these at runtime)
// See ConfigurationManagementService.cs:806 for IsSecret==true filter (app-layer enforcement).
// Secrets listed here are the CANONICAL source; override order is: env var → KV → DB (DB filtered).
//   AzureOpenAI--Endpoint        (swap risk → attacker-controlled proxy)
//   AzureOpenAI--ApiKey          (legacy external-endpoint flow only)
//   SelfHosted--JwtSecret        (forgery risk)
//   SelfHosted--SuperAdminPassword (admin takeover)
//   MCP--ServiceKey              (RCE risk via MCP tool invocation)
//   Storage--Azure--AccountUrl   (data redirect)
//   ApplicationInsights--ConnectionString (telemetry redirect + tenant pivoting)
// Per SH_ENTERPRISE_BICEP_HARDENING §Rule 10. Runtime-enforcement side is β spec.

resource secretSqlConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'ConnectionStrings--McpDb'
  properties: {
    value: sqlConnectionString
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretSearchEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureAISearch--Endpoint'
  properties: {
    value: 'https://${searchService.name}.search.windows.net'
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// AzureAISearch--ApiKey — MI-swap: SearchClient/SearchIndexClient use DefaultAzureCredential
// (Builder B commit 3a690a4c). listAdminKeys() REMOVED. Search Service Contributor role
// has also been dropped in favor of Search Index Data Contributor (data-plane only) —
// prevents runtime schema drift (2026-03 incident). Secret retained as placeholder so
// the container-app secretRef 'search-apikey' stays valid during the transition.
resource secretSearchKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureAISearch--ApiKey'
  properties: {
    value: 'mi-auth-placeholder'
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureOpenAI--Endpoint'
  properties: {
    value: effectiveOpenAiEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// AzureOpenAI--ApiKey — MI-swap: AzureOpenAIClient uses DefaultAzureCredential when
// deployOpenAI=true OR existingOpenAiResourceId is set (Builder B commit 3a690a4c).
// Secret retained as placeholder for MI paths to keep container-app secretRef valid;
// actual external-endpoint key flows through when deployOpenAI=false AND no BYO id.
// SH_ENTERPRISE_BICEP_HARDENING §Rule 10 Trusted Config Sources — secret kept
// in KV only; DB override filter blocks it at runtime.
resource secretOpenAiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureOpenAI--ApiKey'
  properties: {
    value: (deployOpenAI || !empty(existingOpenAiResourceId)) ? 'mi-auth-placeholder' : externalOpenAiKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// MCP--ServiceKey — persisted to KV for idempotent reruns. First deploy: newGuid() default
// generates the value → KV stores it. Subsequent deploys: fetch from KV and pass via
// --parameters mcpServiceKey=<value> so the container app picks up the SAME key and
// MCP tool-auth remains stable. Per SH_ENTERPRISE_BICEP_HARDENING §Rule 4 — replaces
// the previous deterministic uniqueString(resourceGroup().id) pattern (predictable per RG).
resource secretMcpServiceKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'MCP--ServiceKey'
  properties: {
    value: mcpServiceKey
    attributes: {
      enabled: true
    }
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiDeploymentName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureOpenAI--DeploymentName'
  properties: {
    value: chatDeploymentName
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretOpenAiEmbeddingDeploymentName 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureOpenAI--EmbeddingDeploymentName'
  properties: {
    value: embeddingDeploymentName
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretDocIntelEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureDocumentIntelligence--Endpoint'
  properties: {
    value: effectiveDocIntelEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// AzureDocumentIntelligence--ApiKey — MI-swap: listKeys() for locally-deployed DocIntel REMOVED.
// DocumentIntelligenceClient uses DefaultAzureCredential when deployDocumentIntelligence=true
// (Builder B commit 3a690a4c). Secret retained (placeholder when MI-active) so the container-app
// secretRef 'docintel-apikey' doesn't fail even if the app no longer consumes it.
resource secretDocIntelApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureDocumentIntelligence--ApiKey'
  properties: {
    value: deployDocumentIntelligence ? 'mi-auth-placeholder' : externalDocIntelKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretVisionEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureAIVision--Endpoint'
  properties: {
    value: effectiveVisionEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// AzureAIVision--ApiKey — MI-swap: listKeys() for locally-deployed Vision REMOVED.
// Vision REST calls use Authorization: Bearer header when deployVision=true
// (Builder B commit 3a690a4c). Secret retained (placeholder when MI-active).
resource secretVisionApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'AzureAIVision--ApiKey'
  properties: {
    value: deployVision ? 'mi-auth-placeholder' : externalVisionKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// Storage:Azure:AccountUrl — replaces Storage--Azure--ConnectionString after MI swap.
// BlobServiceClient uses DefaultAzureCredential + this URL (no AccountKey needed).
// Old Storage--Azure--ConnectionString secret REMOVED in this commit; app-layer
// supports MI-only auth per Builder B's commit 3a690a4ca.
resource secretStorageAccountUrl 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'Storage--Azure--AccountUrl'
  properties: {
    value: storageBlobEndpoint
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretAppInsights 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'ApplicationInsights--ConnectionString'
  properties: {
    value: appInsightsConnectionString
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'SelfHosted--ApiKey'
  properties: {
    value: apiKey
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretJwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'SelfHosted--JwtSecret'
  properties: {
    value: jwtSecret
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

resource secretAdminPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'SelfHosted--SuperAdminPassword'
  properties: {
    value: adminPassword
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// ============================================================================
// DATA PROTECTION MASTER KEY (co-scoped with Builder A commit 4b06cb3e4)
// ============================================================================
// SH_ENTERPRISE_BICEP_HARDENING §Rule 9 / security-officer finding #12.
// App persists its ASP.NET Core Data Protection key ring to blob (dp-keys container)
// and wraps it with this KV key (RSA 2048, 90-day rotation policy).
// Without this, container restart rotates all cookies/tokens and logs users out.
// Name MUST match the app-side default (selfhosted/src/Knowz.SelfHosted.API/Program.cs:50) —
// "selfhosted-dp-key". Override via env var AzureKeyVault__DataProtectionKeyName if needed.
resource dpMasterKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = if (empty(byoKeyVaultId)) {
  parent: keyVault
  name: 'selfhosted-dp-key'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
    rotationPolicy: {
      attributes: {
        expiryTime: 'P2Y'
      }
      lifetimeActions: [
        {
          action: {
            type: 'rotate'
          }
          trigger: {
            timeAfterCreate: 'P90D'
          }
        }
        {
          action: {
            type: 'notify'
          }
          trigger: {
            timeBeforeExpiry: 'P30D'
          }
        }
      ]
    }
  }
  dependsOn: [kvSecretsOfficerDeployerRole]
}

// MI grant: Key Vault Crypto User (wrap/unwrap the DP key ring).
// For BYO KV, the cross-rg role module grants this in the customer's RG.
resource dpKeyCryptoUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (empty(byoKeyVaultId)) {
  name: guid(keyVault.id, managedIdentity.id, keyVaultCryptoUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

module byoDpKeyCryptoUserRole 'modules/cross-rg-role.bicep' = if (!empty(byoKeyVaultId)) {
  name: 'byo-kv-dp-crypto-user'
  scope: resourceGroup(byoKvSubscriptionId, byoKvResourceGroupName)
  params: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
    targetResourceId: byoKeyVaultId
  }
}

// ============================================================================
// AZURE AI SEARCH (private endpoint, public access disabled)
// ============================================================================

resource searchService 'Microsoft.Search/searchServices@2023-11-01' = {
  name: '${prefix}-search-${location}'
  location: location
  tags: effectiveTags
  sku: {
    name: searchSku
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'disabled'
  }
}

// Private endpoint: AI Search
resource searchPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${prefix}-pe-search'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-search'
        properties: {
          privateLinkServiceId: searchService.id
          groupIds: [
            'searchService'
          ]
        }
      }
    ]
  }
}

resource searchDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: searchPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[5].id // privatelink.search.windows.net
        }
      }
    ]
  }
}

// ============================================================================
// AZURE OPENAI (private endpoint, public access disabled)
// ============================================================================

// BYO Azure OpenAI — existing reference scoped to customer RG. Customer is responsible
// for pre-existing chat + embedding deployments matching chatDeploymentName / embeddingDeploymentName.
// See SH_ENTERPRISE_BYO_INFRA §Rule 5. Used by effectiveOpenAiEndpoint (above) + byoOpenAiRole (below).
resource existingOpenAi 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = if (!empty(existingOpenAiResourceId)) {
  name: split(existingOpenAiResourceId, '/')[8]
  scope: resourceGroup(split(existingOpenAiResourceId, '/')[2], split(existingOpenAiResourceId, '/')[4])
}

resource cognitiveServices 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
  name: '${prefix}-openai-${location}'
  location: location
  tags: effectiveTags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Disabled'
    customSubDomainName: '${prefix}-openai-${location}'
    networkAcls: {
      defaultAction: 'Deny'
    }
  }
}

resource deploymentChat 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
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

resource deploymentMini 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
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

resource deploymentEmbedding 'Microsoft.CognitiveServices/accounts/deployments@2023-10-01-preview' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
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

// Private endpoint: OpenAI
resource openAiPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
  name: '${prefix}-pe-openai'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-openai'
        properties: {
          privateLinkServiceId: cognitiveServices.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
}

resource openAiDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
  parent: openAiPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[3].id // privatelink.openai.azure.com
        }
      }
    ]
  }
}

// Azure OpenAI diagnostics — SH_ENTERPRISE_BICEP_HARDENING §Rule 7 fanout.
// Captures Audit + RequestResponse + Trace categories. Only attaches to locally-deployed
// OpenAI; BYO OpenAI has customer-managed diagnostics in their RG.
resource openAiDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
  name: '${prefix}-openai-diagnostics'
  scope: cognitiveServices
  properties: {
    workspaceId: effectiveLawId
    logs: [
      { category: 'Audit', enabled: true }
      { category: 'RequestResponse', enabled: true }
      { category: 'Trace', enabled: true }
    ]
    metrics: [
      { category: 'AllMetrics', enabled: true }
    ]
  }
}

// ============================================================================
// DOCUMENT INTELLIGENCE (private endpoint, public access disabled)
// ============================================================================

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployDocumentIntelligence) {
  name: '${prefix}-docintel-${location}'
  location: location
  tags: effectiveTags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    publicNetworkAccess: 'Disabled'
    customSubDomainName: '${prefix}-docintel-${location}'
    networkAcls: {
      defaultAction: 'Deny'
    }
  }
}

// Private endpoint: Document Intelligence
resource docIntelPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (deployDocumentIntelligence) {
  name: '${prefix}-pe-docintel'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-docintel'
        properties: {
          privateLinkServiceId: documentIntelligence.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
}

resource docIntelDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (deployDocumentIntelligence) {
  parent: docIntelPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[4].id // privatelink.cognitiveservices.azure.com
        }
      }
    ]
  }
}

// ============================================================================
// AZURE AI VISION (private endpoint, public access disabled)
// ============================================================================

resource visionService 'Microsoft.CognitiveServices/accounts@2023-05-01' = if (deployVision) {
  name: '${prefix}-vision-${location}'
  location: location
  tags: effectiveTags
  kind: 'ComputerVision'
  sku: {
    name: 'S1'
  }
  properties: {
    publicNetworkAccess: 'Disabled'
    customSubDomainName: '${prefix}-vision-${location}'
    networkAcls: {
      defaultAction: 'Deny'
    }
  }
}

// Private endpoint: Azure AI Vision
resource visionPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (deployVision) {
  name: '${prefix}-pe-vision'
  location: location
  tags: effectiveTags
  properties: {
    subnet: {
      id: effectivePeSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${prefix}-plsc-vision'
        properties: {
          privateLinkServiceId: visionService.id
          groupIds: [
            'account'
          ]
        }
      }
    ]
  }
}

resource visionDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (deployVision) {
  parent: visionPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: dnsZones[4].id // privatelink.cognitiveservices.azure.com (shared with DocIntel)
        }
      }
    ]
  }
}

// ============================================================================
// ROLE ASSIGNMENTS (Managed Identity -> Azure Services)
// ============================================================================

// Built-in role definition IDs
// SH_ENTERPRISE_BICEP_HARDENING §Rule 3: downgraded from OpenAI Contributor (a001fd3d-...)
// to OpenAI User (5e0bd9bd-...) — principle of least privilege. User allows inference only;
// Contributor allowed deployment mutation (spinning up rogue deployments, changing model SKUs).
var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var acrPullRoleId = '7f951dda-4ed3-11e8-9c2d-fa7ae01bbebc'
var keyVaultCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'

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

// Search Service Contributor role REMOVED — runtime no longer needs schema-mutation rights
// (MI-swap: SearchIndexClient.CreateIndexAsync path is gated off at the app layer).
// Search Index Data Contributor (granted above) is sufficient for data-plane read/write.
// This closes the 2026-03 runtime-schema-drift incident. Keep var declaration so
// `no-unused-vars` doesn't trip; it's scoped for potential future use (e.g. index
// provisioning deployment script). Do NOT re-add this role assignment without a
// compensating control.

// OpenAI: Cognitive Services OpenAI User (least-privilege; was Contributor before hardening)
resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployOpenAI && empty(existingOpenAiResourceId)) {
  name: guid(cognitiveServices.id, managedIdentity.id, cognitiveServicesOpenAIUserRoleId)
  scope: cognitiveServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    principalId: managedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// BYO OpenAI: cross-resource role grant when customer supplies existingOpenAiResourceId.
// Customer OpenAI MUST be in the same Entra tenant (cross-tenant is out of scope per D1).
// Customer deployer SP needs Cognitive Services Contributor OR Owner on the target resource
// to emit this role assignment — documented in DOC_EnterpriseRunbook pre-grant section.
module byoOpenAiRole 'modules/cross-rg-role.bicep' = if (!empty(existingOpenAiResourceId)) {
  name: 'byo-openai-user-role'
  scope: resourceGroup(split(existingOpenAiResourceId, '/')[2], split(existingOpenAiResourceId, '/')[4])
  params: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    targetResourceId: existingOpenAiResourceId
  }
}

// External ACR (air-gapped / policy-restricted pulls): AcrPull role at customer ACR.
// Customer is responsible for:
//   1. Mirroring ghcr.io/knowz-io/knowz-{api,mcp,web}:<tag> into their ACR
//      (docs: `docker buildx imagetools create --tag <acr>.azurecr.io/knowz-io/<image>:<tag> ghcr.io/knowz-io/<image>:<tag>`)
//   2. Ensuring ACR is AAD-enabled (classic admin-only ACRs are D4 out of scope)
//   3. Pre-granting deployer SP `Contributor` on the ACR RG to allow role emission
// Matches `infrastructure/modules/external-acr-role.bicep` pattern from partner platform.
var effectiveAcrResourceId = !empty(externalAcrName)
  ? resourceId(subscription().subscriptionId, externalAcrResourceGroup, 'Microsoft.ContainerRegistry/registries', externalAcrName)
  : ''

module externalAcrPullRole 'modules/cross-rg-role.bicep' = if (!empty(externalAcrName)) {
  name: 'external-acr-pull-role'
  scope: resourceGroup(externalAcrResourceGroup)
  params: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    targetResourceId: effectiveAcrResourceId
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
// CONTAINER APPS ENVIRONMENT (VNet-injected, internal only)
// ============================================================================

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae'
  location: location
  tags: effectiveTags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: effectiveContainerAppsSubnetId
      internal: true
    }
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: effectiveLawCustomerId
        sharedKey: effectiveLawKey
      }
    }
    zoneRedundant: false
  }
}

// ============================================================================
// CONTAINER APPS (API, MCP, Web) — internal ingress, managed identity
// ============================================================================

// ---- API Container App ----
resource apiContainerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  tags: effectiveTags
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
        external: false
        targetPort: 8080
        transport: 'http'
      }
      registries: effectiveRegistries
      secrets: concat(effectiveRegistrySecrets, [
        {
          name: 'sql-connection'
          keyVaultUrl: '${effectiveKvUri}secrets/ConnectionStrings--McpDb'
          identity: managedIdentity.id
        }
        {
          name: 'openai-endpoint'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureOpenAI--Endpoint'
          identity: managedIdentity.id
        }
        {
          name: 'openai-apikey'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureOpenAI--ApiKey'
          identity: managedIdentity.id
        }
        {
          name: 'search-endpoint'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureAISearch--Endpoint'
          identity: managedIdentity.id
        }
        {
          name: 'search-apikey'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureAISearch--ApiKey'
          identity: managedIdentity.id
        }
        {
          // MI swap: AccountUrl instead of connection string; app uses DefaultAzureCredential.
          name: 'storage-account-url'
          keyVaultUrl: '${effectiveKvUri}secrets/Storage--Azure--AccountUrl'
          identity: managedIdentity.id
        }
        {
          name: 'selfhosted-apikey'
          keyVaultUrl: '${effectiveKvUri}secrets/SelfHosted--ApiKey'
          identity: managedIdentity.id
        }
        {
          name: 'selfhosted-jwtsecret'
          keyVaultUrl: '${effectiveKvUri}secrets/SelfHosted--JwtSecret'
          identity: managedIdentity.id
        }
        {
          name: 'selfhosted-adminpassword'
          keyVaultUrl: '${effectiveKvUri}secrets/SelfHosted--SuperAdminPassword'
          identity: managedIdentity.id
        }
        {
          name: 'docintel-endpoint'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureDocumentIntelligence--Endpoint'
          identity: managedIdentity.id
        }
        {
          name: 'docintel-apikey'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureDocumentIntelligence--ApiKey'
          identity: managedIdentity.id
        }
        {
          name: 'vision-endpoint'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureAIVision--Endpoint'
          identity: managedIdentity.id
        }
        {
          name: 'vision-apikey'
          keyVaultUrl: '${effectiveKvUri}secrets/AzureAIVision--ApiKey'
          identity: managedIdentity.id
        }
        {
          name: 'appinsights-connection'
          keyVaultUrl: '${effectiveKvUri}secrets/ApplicationInsights--ConnectionString'
          identity: managedIdentity.id
        }
      ])
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${registryPath}/knowz-selfhosted-api:${imageTag}'
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
              value: chatDeploymentName
            }
            {
              name: 'AzureOpenAI__EmbeddingDeploymentName'
              value: embeddingDeploymentName
            }
            {
              name: 'Embedding__ModelName'
              value: embeddingModelName
            }
            {
              name: 'Embedding__Dimensions'
              value: string(embeddingDimensions)
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
              // Post-MI-swap: app reads Storage:Azure:AccountUrl + uses DefaultAzureCredential.
              // Old Storage__Azure__ConnectionString binding removed (see Builder B commit 3a690a4c).
              name: 'Storage__Azure__AccountUrl'
              secretRef: 'storage-account-url'
            }
            {
              name: 'Storage__Azure__ContainerName'
              value: 'selfhosted-files'
            }
            {
              // Enterprise storage has publicNetworkAccess disabled; attachment reads
              // must be served by authenticated API stream endpoints, not direct SAS URLs.
              name: 'StorageAccess__AttachmentReadMode'
              value: 'ApiStream'
            }
            {
              // Data Protection key ring (Builder A commit 4b06cb3e4) reads this key.
              // Co-located with Storage:Azure:AccountUrl so the single storage account
              // hosts both files and DP keys (different containers).
              name: 'Storage__AzureBlob__AccountUrl'
              secretRef: 'storage-account-url'
            }
            {
              // VaultUri used by AddDataProtection() + BootstrapService + ConfigurationManagementService.
              name: 'AzureKeyVault__VaultUri'
              value: effectiveKvUri
            }
            {
              // DP key name — must match resource dpMasterKey above.
              name: 'AzureKeyVault__DataProtectionKeyName'
              value: 'selfhosted-dp-key'
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
              name: 'Database__AutoMigrate'
              value: 'true'
            }
            {
              name: 'MCP__ServiceKey'
              value: mcpServiceKey
            }
            {
              name: 'AzureDocumentIntelligence__Endpoint'
              secretRef: 'docintel-endpoint'
            }
            {
              name: 'AzureDocumentIntelligence__ApiKey'
              secretRef: 'docintel-apikey'
            }
            {
              name: 'AzureAIVision__Endpoint'
              secretRef: 'vision-endpoint'
            }
            {
              name: 'AzureAIVision__ApiKey'
              secretRef: 'vision-apikey'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: managedIdentity.properties.clientId
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              secretRef: 'appinsights-connection'
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
resource mcpContainerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-mcp'
  location: location
  tags: effectiveTags
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
        external: false
        targetPort: 8080
        transport: 'http'
      }
      registries: effectiveRegistries
      secrets: effectiveRegistrySecrets
    }
    template: {
      containers: [
        {
          name: 'mcp'
          image: '${registryPath}/knowz-selfhosted-mcp:${imageTag}'
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
resource webContainerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-web'
  location: location
  tags: effectiveTags
  properties: {
    managedEnvironmentId: containerAppsEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
      registries: effectiveRegistries
      secrets: effectiveRegistrySecrets
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${registryPath}/knowz-selfhosted-web:${imageTag}'
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
// AZURE FRONT DOOR (Standard tier with WAF)
// ============================================================================

resource wafPolicy 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: '${replace(prefix, '-', '')}waf'
  location: 'global'
  tags: effectiveTags
  sku: {
    name: 'Premium_AzureFrontDoor'
  }
  properties: {
    policySettings: {
      enabledState: 'Enabled'
      // SH_ENTERPRISE_BICEP_HARDENING §Rule 1: Prevention blocks attacks (not just logs).
      // Customers can temporarily flip back to Detection during incident triage by passing
      // wafMode='Detection' via bicepparam — no template rewrite needed.
      mode: wafMode
      requestBodyCheck: 'Enabled'
    }
    managedRules: {
      managedRuleSets: [
        {
          ruleSetType: 'Microsoft_DefaultRuleSet'
          ruleSetVersion: '2.1'
          ruleSetAction: 'Block'
        }
        {
          ruleSetType: 'Microsoft_BotManagerRuleSet'
          ruleSetVersion: '1.0'
        }
      ]
    }
  }
}

resource frontDoor 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: '${prefix}-fd'
  location: 'global'
  tags: effectiveTags
  sku: {
    name: 'Premium_AzureFrontDoor'
  }
  properties: {
    originResponseTimeoutSeconds: 60
  }
}

// Front Door endpoint
resource fdEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: frontDoor
  name: '${prefix}-endpoint'
  location: 'global'
  tags: effectiveTags
  properties: {
    enabledState: 'Enabled'
  }
}

// Origin group for Container Apps (Web)
resource fdOriginGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'container-apps'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    healthProbeSettings: {
      probePath: '/healthz'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 30
    }
    sessionAffinityState: 'Disabled'
  }
}

// Origin: Web (default, serves / paths)
resource fdOriginWeb 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: fdOriginGroup
  name: 'web-origin'
  properties: {
    hostName: webContainerApp.properties.configuration.ingress.fqdn
    httpPort: 80
    httpsPort: 443
    originHostHeader: webContainerApp.properties.configuration.ingress.fqdn
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    sharedPrivateLinkResource: {
      privateLink: {
        id: containerAppsEnv.id
      }
      privateLinkLocation: location
      groupId: 'managedEnvironments'
      requestMessage: 'Front Door private link to Container Apps'
    }
  }
}

// Origin group for API (separate to allow different health probe)
resource fdApiOriginGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'api-apps'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    healthProbeSettings: {
      probePath: '/health'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 30
    }
    sessionAffinityState: 'Disabled'
  }
}

// Origin: API
resource fdOriginApi 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: fdApiOriginGroup
  name: 'api-origin'
  properties: {
    hostName: apiContainerApp.properties.configuration.ingress.fqdn
    httpPort: 80
    httpsPort: 443
    originHostHeader: apiContainerApp.properties.configuration.ingress.fqdn
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    sharedPrivateLinkResource: {
      privateLink: {
        id: containerAppsEnv.id
      }
      privateLinkLocation: location
      groupId: 'managedEnvironments'
      requestMessage: 'Front Door private link to Container Apps API'
    }
  }
}

// Origin group for MCP (separate for MCP protocol paths)
resource fdMcpOriginGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: 'mcp-origin-group'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    healthProbeSettings: {
      probePath: '/health'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 30
    }
    sessionAffinityState: 'Disabled'
  }
}

// Origin: MCP
// NOTE: Private link connections from Front Door to Container Apps require manual approval
// after deployment. Use: az network private-endpoint-connection approve --id <connection-id>
// This applies to ALL origins using sharedPrivateLinkResource (web, API, MCP).
resource fdOriginMcp 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: fdMcpOriginGroup
  name: 'mcp-origin'
  properties: {
    hostName: mcpContainerApp.properties.configuration.ingress.fqdn
    httpPort: 80
    httpsPort: 443
    originHostHeader: mcpContainerApp.properties.configuration.ingress.fqdn
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    sharedPrivateLinkResource: {
      privateLink: {
        id: containerAppsEnv.id
      }
      privateLinkLocation: location
      groupId: 'managedEnvironments'
      requestMessage: 'Front Door private link to Container Apps MCP'
    }
  }
}

// Security policy: attach WAF to endpoint
resource fdSecurityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: frontDoor
  name: 'waf-policy'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: {
        id: wafPolicy.id
      }
      associations: [
        {
          domains: [
            {
              id: fdEndpoint.id
            }
          ]
          patternsToMatch: [
            '/*'
          ]
        }
      ]
    }
  }
}

// Route: /api/* -> API origin group
resource fdApiRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: fdEndpoint
  name: 'api-route'
  properties: {
    originGroup: {
      id: fdApiOriginGroup.id
    }
    supportedProtocols: [
      'Https'
    ]
    patternsToMatch: [
      '/api/*'
      '/health'
      '/swagger/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    fdOriginApi
  ]
}

// Route: /sse/*, /message/*, /mcp/* -> MCP origin group
resource fdMcpRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: fdEndpoint
  name: 'mcp-route'
  properties: {
    originGroup: {
      id: fdMcpOriginGroup.id
    }
    supportedProtocols: [
      'Https'
    ]
    patternsToMatch: [
      '/sse/*'
      '/message/*'
      '/mcp/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    fdOriginMcp
  ]
}

// Route: /* -> Web origin group (catch-all, lower priority)
resource fdWebRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: fdEndpoint
  name: 'web-route'
  properties: {
    originGroup: {
      id: fdOriginGroup.id
    }
    supportedProtocols: [
      'Https'
    ]
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    fdOriginWeb
    fdApiRoute
    fdMcpRoute
  ]
}

// Front Door diagnostics
resource fdDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${prefix}-fd-diagnostics'
  scope: frontDoor
  properties: {
    workspaceId: effectiveLawId
    logs: [
      {
        category: 'FrontDoorAccessLog'
        enabled: true
      }
      {
        category: 'FrontDoorWebApplicationFirewallLog'
        enabled: true
      }
      {
        category: 'FrontDoorHealthProbeLog'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ============================================================================
// OUTPUTS
// ============================================================================

// Azure AI Search
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'
output searchServiceName string = searchService.name
output searchIndexName string = 'knowledge'

// Azure OpenAI
output openAiEndpoint string = effectiveOpenAiEndpoint
output openAiResourceName string = !empty(existingOpenAiResourceId) ? split(existingOpenAiResourceId, '/')[8] : (deployOpenAI ? cognitiveServices.name : 'external')
output chatDeploymentNameOutput string = chatDeploymentName
output miniDeploymentName string = 'gpt-5.4-mini'
output embeddingDeploymentNameOutput string = embeddingDeploymentName

// Document Intelligence
output documentIntelligenceEndpoint string = deployDocumentIntelligence ? documentIntelligence.properties.endpoint : externalDocIntelEndpoint
output documentIntelligenceName string = deployDocumentIntelligence ? documentIntelligence.name : 'external'

// AI Configuration Summary (mode per service)
// NOTE: Enterprise template does not currently wire `existing*` params into resource resolution
// (private-endpoint + cross-RG complexity). The deploy script (selfhosted-deploy.ps1) performs
// the lookup and passes resolved endpoint/key via external* parameters. These params are
// accepted here so the portal UI can pass them without deployment failures.
output aiConfigurationSummary object = {
  openai: !empty(existingOpenAiResourceId) ? 'byo:${split(existingOpenAiResourceId, '/')[8]}' : (deployOpenAI ? 'deployed' : (existingOpenAiName != '' ? 'existing:${existingOpenAiName}' : 'external'))
  vision: deployVision ? 'deployed' : (existingVisionName != '' ? 'existing:${existingVisionName}' : 'external')
  docIntel: deployDocumentIntelligence ? 'deployed' : (existingDocIntelName != '' ? 'existing:${existingDocIntelName}' : 'external')
}

// SQL Database
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = 'McpKnowledge'
output sqlServerName string = sqlServerName

// Storage
output storageAccountName string = storageAccount.name
output storageBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output frontDoorId string = frontDoor.properties.frontDoorId

// Managed Identity
output managedIdentityId string = managedIdentity.id
output managedIdentityPrincipalId string = managedIdentity.properties.principalId
output managedIdentityClientId string = managedIdentity.properties.clientId

// Key Vault
output keyVaultName string = effectiveKvName
output keyVaultUri string = effectiveKvUri

// Monitoring
output logAnalyticsWorkspaceId string = effectiveLawId
output logAnalyticsWorkspaceName string = empty(centralLogAnalyticsId) ? logAnalytics.name : centralLawName
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey

// Container Apps
output apiContainerAppFqdn string = apiContainerApp.properties.configuration.ingress.fqdn
output mcpContainerAppFqdn string = mcpContainerApp.properties.configuration.ingress.fqdn
output webContainerAppFqdn string = webContainerApp.properties.configuration.ingress.fqdn

// Enterprise additions
output frontDoorEndpoint string = fdEndpoint.properties.hostName
output vnetName string = vnet.name
output vnetId string = effectiveVnetId
