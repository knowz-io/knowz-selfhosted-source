# search.tf — AI Search (standard+) + private endpoint

# -----------------------------------------------------------------------------
# Azure AI Search (private endpoint, public access disabled)
# -----------------------------------------------------------------------------

resource "azurerm_search_service" "main" {
  name                          = "${var.prefix}-search-${var.location}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  sku                           = var.search_sku
  replica_count                 = 1
  partition_count               = 1
  hosting_mode                  = "default"
  public_network_access_enabled = false
  tags                          = local.effective_tags
}

# -----------------------------------------------------------------------------
# Private Endpoint: AI Search
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "search" {
  name                = "${var.prefix}-pe-search"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-search"
    private_connection_resource_id = azurerm_search_service.main.id
    subresource_names              = ["searchService"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["search"].id]
  }
}
