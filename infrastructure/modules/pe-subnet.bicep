// selfhosted/infrastructure/modules/pe-subnet.bicep
// Auto-creates a private-endpoint subnet inside an existing BYO VNet, scoped to the VNet's
// resource group. Used when the enterprise deploy caller provides a CIDR (peSubnetAddressPrefix)
// instead of a pre-existing byoVnetPeSubnetId.
//
// Scope: the caller MUST invoke this module with `scope: resourceGroup(vnetResourceGroup)`
// because the parent VNet lives in the customer's networking RG, which may differ from the
// deployment RG.
//
// Notes:
// - Subnet is NOT delegated — PEs must live in an undelegated subnet.
// - privateEndpointNetworkPolicies is explicitly Disabled (required for PE deployment per
//   https://learn.microsoft.com/azure/private-link/disable-private-endpoint-network-policy).
// - Adding a subnet to an existing VNet via the child-resource syntax (`parent: vnet`) appends
//   to the VNet's subnet list without taking over declarative control of the parent.
//
// Ported verbatim from infrastructure/modules/pe-subnet.bicep per SH_ENTERPRISE_BYO_INFRA.md §D6.

@description('Name of the BYO VNet that will host the PE subnet. The caller scopes this module to the VNet\'s resource group via `scope: resourceGroup(vnetResourceGroup)`.')
param vnetName string

@description('Name of the PE subnet to create. Default: pe-subnet.')
param subnetName string = 'pe-subnet'

@description('CIDR address prefix for the PE subnet (e.g., 10.0.10.0/27). Must not conflict with existing subnets in the VNet.')
param addressPrefix string

resource vnet 'Microsoft.Network/virtualNetworks@2023-09-01' existing = {
  name: vnetName
}

resource peSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-09-01' = {
  parent: vnet
  name: subnetName
  properties: {
    addressPrefix: addressPrefix
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}

output subnetId string = peSubnet.id
output subnetName string = peSubnet.name
