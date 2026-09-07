# =============================================================================
# AZURE AI VISION / COMPUTER VISION (conditional)
# =============================================================================

resource "azurerm_cognitive_account" "vision" {
  count = var.deploy_vision ? 1 : 0

  name                          = "${var.prefix}-vision-${var.location}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  kind                          = "ComputerVision"
  sku_name                      = "S1"
  custom_subdomain_name         = "${var.prefix}-vision-${var.location}"
  public_network_access_enabled = true

  tags = local.tags
}
