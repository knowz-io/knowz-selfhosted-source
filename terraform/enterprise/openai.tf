# openai.tf — Azure OpenAI (conditional) + private endpoint + model deployments

# -----------------------------------------------------------------------------
# Azure OpenAI Account (private endpoint, public access disabled)
# -----------------------------------------------------------------------------

resource "azurerm_cognitive_account" "openai" {
  count                         = local.create_openai ? 1 : 0
  name                          = "${var.prefix}-openai-${var.location}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = "${var.prefix}-openai-${var.location}"
  public_network_access_enabled = false
  tags                          = local.effective_tags

  network_acls {
    default_action = "Deny"
  }
}

# -----------------------------------------------------------------------------
# Model Deployments
# -----------------------------------------------------------------------------

resource "azurerm_cognitive_deployment" "chat" {
  count                = local.create_openai ? 1 : 0
  name                 = var.chat_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }

  model {
    format  = "OpenAI"
    name    = "gpt-5.4"
    version = "2026-03-05"
  }
}

resource "azurerm_cognitive_deployment" "mini" {
  count                = local.create_openai ? 1 : 0
  name                 = "gpt-5.4-mini"
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }

  model {
    format  = "OpenAI"
    name    = "gpt-5.4-mini"
    version = "2026-03-17"
  }

  depends_on = [azurerm_cognitive_deployment.chat]
}

resource "azurerm_cognitive_deployment" "embedding" {
  count                = local.create_openai ? 1 : 0
  name                 = var.embedding_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  sku {
    name     = "Standard"
    capacity = 5
  }

  model {
    format  = "OpenAI"
    name    = var.embedding_model_name
    version = "1"
  }

  depends_on = [azurerm_cognitive_deployment.mini]
}

# -----------------------------------------------------------------------------
# Private Endpoint: OpenAI
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "openai" {
  count               = local.create_openai ? 1 : 0
  name                = "${var.prefix}-pe-openai"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-openai"
    private_connection_resource_id = azurerm_cognitive_account.openai[0].id
    subresource_names              = ["account"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["openai"].id]
  }
}
