# vision.tf — Azure AI Vision (conditional) + private endpoint

# -----------------------------------------------------------------------------
# Azure AI Vision (Computer Vision)
# -----------------------------------------------------------------------------

resource "azurerm_cognitive_account" "vision" {
  count                         = var.deploy_vision ? 1 : 0
  name                          = "${var.prefix}-vision-${var.location}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  kind                          = "ComputerVision"
  sku_name                      = "S1"
  custom_subdomain_name         = "${var.prefix}-vision-${var.location}"
  public_network_access_enabled = false
  tags                          = local.effective_tags

  network_acls {
    default_action = "Deny"
  }
}

# -----------------------------------------------------------------------------
# Private Endpoint: Azure AI Vision
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "vision" {
  count               = var.deploy_vision ? 1 : 0
  name                = "${var.prefix}-pe-vision"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-vision"
    private_connection_resource_id = azurerm_cognitive_account.vision[0].id
    subresource_names              = ["account"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["cogservices"].id]
  }
}
