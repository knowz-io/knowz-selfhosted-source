# =============================================================================
# AZURE AI SEARCH
# =============================================================================

resource "azurerm_search_service" "main" {
  name                = "${var.prefix}-search-${local.search_location}"
  resource_group_name = azurerm_resource_group.main.name
  location            = local.search_location
  sku                 = var.search_sku

  replica_count   = 1
  partition_count = 1
  hosting_mode    = "default"

  public_network_access_enabled = true

  tags = local.tags
}
