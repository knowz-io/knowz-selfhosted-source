// selfhosted/infrastructure/modules/cross-rg-role.bicep
// Cross-resource-group role assignment module.
//
// Required because `resource ... = existing` cannot be used as a direct `scope:` target
// for `Microsoft.Authorization/roleAssignments@2022-04-01` when the target RG differs from
// the parent template RG. Call this module with `scope: resourceGroup(targetSubId, targetRgName)`
// to emit the role assignment into the target RG.
//
// Used by:
//   - BYO Key Vault  → Key Vault Secrets User role at customer KV
//   - BYO OpenAI     → Cognitive Services OpenAI User at customer AOAI
//   - External ACR   → AcrPull at customer ACR
//
// See SH_ENTERPRISE_BYO_INFRA.md §Rule 5 / §Rule 6 for role IDs and pre-grant prereqs.

targetScope = 'resourceGroup'

@description('Principal ID (managed identity principalId) that receives the role assignment.')
param principalId string

@description('Role definition ID (GUID). Must be a subscription-scope reference, e.g. subscriptionResourceId("Microsoft.Authorization/roleDefinitions", "<guid>").')
param roleDefinitionId string

@description('Target resource ID. Used only for deterministic name hashing — caller is responsible for scoping the module to the target RG.')
param targetResourceId string

@description('Principal type. Default ServicePrincipal — set to User only for deployer pre-grant scenarios.')
@allowed(['ServicePrincipal', 'User', 'Group'])
param principalType string = 'ServicePrincipal'

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(targetResourceId, principalId, roleDefinitionId)
  properties: {
    principalId: principalId
    roleDefinitionId: roleDefinitionId
    principalType: principalType
  }
}

output roleAssignmentId string = roleAssignment.id
