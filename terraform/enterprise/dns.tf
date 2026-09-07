# dns.tf — Private DNS zones (6) and VNet links

locals {
  private_dns_zones = {
    sql         = "privatelink.database.windows.net"
    blob        = "privatelink.blob.core.windows.net"
    keyvault    = "privatelink.vaultcore.azure.net"
    openai      = "privatelink.openai.azure.com"
    cogservices = "privatelink.cognitiveservices.azure.com"
    search      = "privatelink.search.windows.net"
  }
}

resource "azurerm_private_dns_zone" "zones" {
  for_each            = local.private_dns_zones
  name                = each.value
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.effective_tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "links" {
  for_each              = local.private_dns_zones
  name                  = "${var.prefix}-link-${each.key}"
  resource_group_name   = azurerm_resource_group.main.name
  private_dns_zone_name = azurerm_private_dns_zone.zones[each.key].name
  virtual_network_id    = local.vnet_id
  registration_enabled  = false
  tags                  = local.effective_tags
}
