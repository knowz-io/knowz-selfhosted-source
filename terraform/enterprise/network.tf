# network.tf — VNet, subnets, and NSGs

locals {
  create_local_vnet        = var.auto_provision_vnet && var.byo_vnet_subnet_id == ""
  create_byo_pe_subnet     = var.byo_vnet_subnet_id != "" && var.byo_vnet_pe_subnet_id == "" && var.pe_subnet_address_prefix != ""
  byo_vnet_id              = var.byo_vnet_subnet_id != "" ? join("/", slice(split("/", var.byo_vnet_subnet_id), 0, 9)) : ""
  byo_vnet_name            = var.byo_vnet_subnet_id != "" ? split("/", var.byo_vnet_subnet_id)[8] : ""
  byo_vnet_rg_name         = var.byo_vnet_subnet_id != "" ? split("/", var.byo_vnet_subnet_id)[4] : ""
  vnet_id                  = var.byo_vnet_subnet_id != "" ? local.byo_vnet_id : azurerm_virtual_network.main[0].id
  container_apps_subnet_id = var.byo_vnet_subnet_id != "" ? var.byo_vnet_subnet_id : azurerm_subnet.container_apps[0].id
  private_endpoints_subnet_id = var.byo_vnet_pe_subnet_id != "" ? var.byo_vnet_pe_subnet_id : (
    local.create_byo_pe_subnet ? azurerm_subnet.private_endpoints_byo[0].id : azurerm_subnet.private_endpoints[0].id
  )
}

resource "terraform_data" "enterprise_input_guards" {
  lifecycle {
    precondition {
      condition     = var.auto_provision_vnet || var.byo_vnet_subnet_id != ""
      error_message = "Enterprise self-hosted: byo_vnet_subnet_id required when auto_provision_vnet=false."
    }

    precondition {
      condition     = var.byo_vnet_subnet_id == "" || var.byo_vnet_pe_subnet_id != "" || var.pe_subnet_address_prefix != ""
      error_message = "Enterprise self-hosted: byo_vnet_pe_subnet_id or pe_subnet_address_prefix required when byo_vnet_subnet_id is set."
    }

    precondition {
      condition     = var.external_acr_name == "" || var.external_acr_resource_group != ""
      error_message = "external_acr_resource_group is required when external_acr_name is set."
    }
  }
}

# -----------------------------------------------------------------------------
# Network Security Groups
# -----------------------------------------------------------------------------

resource "azurerm_network_security_group" "container_apps" {
  name                = "${var.prefix}-container-apps-nsg"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.effective_tags
}

resource "azurerm_network_security_rule" "container_apps_allow_frontdoor_inbound" {
  name                        = "AllowFrontDoorInbound"
  priority                    = 100
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "AzureFrontDoor.Backend"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.container_apps.name
}

resource "azurerm_network_security_rule" "container_apps_allow_all_outbound" {
  name                        = "AllowAllOutbound"
  priority                    = 100
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.container_apps.name
}

resource "azurerm_network_security_group" "private_endpoints" {
  name                = "${var.prefix}-private-endpoints-nsg"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.effective_tags
}

resource "azurerm_network_security_rule" "private_endpoints_deny_all_inbound" {
  name                        = "DenyAllInbound"
  priority                    = 4096
  direction                   = "Inbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.private_endpoints.name
}

resource "azurerm_network_security_rule" "private_endpoints_allow_all_outbound" {
  name                        = "AllowAllOutbound"
  priority                    = 100
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.private_endpoints.name
}

# -----------------------------------------------------------------------------
# Virtual Network
# -----------------------------------------------------------------------------

resource "azurerm_virtual_network" "main" {
  count               = local.create_local_vnet ? 1 : 0
  name                = "${var.prefix}-vnet"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  address_space       = ["10.0.0.0/16"]
  tags                = local.effective_tags
}

resource "azurerm_subnet" "container_apps" {
  count                = local.create_local_vnet ? 1 : 0
  name                 = "container-apps"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = ["10.0.0.0/23"]

  delegation {
    name = "Microsoft.App.environments"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet_network_security_group_association" "container_apps" {
  count                     = local.create_local_vnet ? 1 : 0
  subnet_id                 = azurerm_subnet.container_apps[0].id
  network_security_group_id = azurerm_network_security_group.container_apps.id
}

resource "azurerm_subnet" "private_endpoints" {
  count                = local.create_local_vnet ? 1 : 0
  name                 = "private-endpoints"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = ["10.0.2.0/24"]
}

resource "azurerm_subnet_network_security_group_association" "private_endpoints" {
  count                     = local.create_local_vnet ? 1 : 0
  subnet_id                 = azurerm_subnet.private_endpoints[0].id
  network_security_group_id = azurerm_network_security_group.private_endpoints.id
}

resource "azurerm_subnet" "private_endpoints_byo" {
  count                = local.create_byo_pe_subnet ? 1 : 0
  name                 = "${var.prefix}-private-endpoints"
  resource_group_name  = local.byo_vnet_rg_name
  virtual_network_name = local.byo_vnet_name
  address_prefixes     = [var.pe_subnet_address_prefix]
}

resource "azurerm_subnet_network_security_group_association" "private_endpoints_byo" {
  count                     = local.create_byo_pe_subnet ? 1 : 0
  subnet_id                 = azurerm_subnet.private_endpoints_byo[0].id
  network_security_group_id = azurerm_network_security_group.private_endpoints.id
}
