# =============================================================================
# DOCUMENT INTELLIGENCE / FORM RECOGNIZER (conditional)
# =============================================================================

resource "azurerm_cognitive_account" "docintel" {
  count = var.deploy_document_intelligence ? 1 : 0

  name                          = "${var.prefix}-docintel-${var.location}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  kind                          = "FormRecognizer"
  sku_name                      = "S0"
  custom_subdomain_name         = "${var.prefix}-docintel-${var.location}"
  public_network_access_enabled = true

  tags = local.tags
}
