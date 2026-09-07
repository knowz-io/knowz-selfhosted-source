# docintel.tf — Document Intelligence (conditional) + private endpoint

# -----------------------------------------------------------------------------
# Azure Document Intelligence (Form Recognizer)
# -----------------------------------------------------------------------------

resource "azurerm_cognitive_account" "docintel" {
  count                         = var.deploy_document_intelligence ? 1 : 0
  name                          = "${var.prefix}-docintel-${var.location}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  kind                          = "FormRecognizer"
  sku_name                      = "S0"
  custom_subdomain_name         = "${var.prefix}-docintel-${var.location}"
  public_network_access_enabled = false
  tags                          = local.effective_tags

  network_acls {
    default_action = "Deny"
  }
}

# -----------------------------------------------------------------------------
# Private Endpoint: Document Intelligence
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "docintel" {
  count               = var.deploy_document_intelligence ? 1 : 0
  name                = "${var.prefix}-pe-docintel"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-docintel"
    private_connection_resource_id = azurerm_cognitive_account.docintel[0].id
    subresource_names              = ["account"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["cogservices"].id]
  }
}
