# =============================================================================
# AZURE OPENAI + MODEL DEPLOYMENTS (conditional)
# =============================================================================

resource "azurerm_cognitive_account" "openai" {
  count = var.deploy_openai ? 1 : 0

  name                          = "${var.prefix}-openai-${var.location}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = "${var.prefix}-openai-${var.location}"
  public_network_access_enabled = true

  tags = local.tags
}

# --- Chat model deployment ---

resource "azurerm_cognitive_deployment" "chat" {
  count = var.deploy_openai ? 1 : 0

  name                 = var.chat_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  model {
    format  = "OpenAI"
    name    = "gpt-5.4"
    version = "2026-03-05"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }
}

# --- Mini model deployment (sequential — Azure OpenAI doesn't allow parallel deployments) ---

resource "azurerm_cognitive_deployment" "mini" {
  count = var.deploy_openai ? 1 : 0

  name                 = "gpt-5.4-mini"
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  model {
    format  = "OpenAI"
    name    = "gpt-5.4-mini"
    version = "2026-03-17"
  }

  sku {
    name     = "GlobalStandard"
    capacity = 10
  }

  depends_on = [azurerm_cognitive_deployment.chat]
}

# --- Embedding model deployment (sequential) ---

resource "azurerm_cognitive_deployment" "embedding" {
  count = var.deploy_openai ? 1 : 0

  name                 = var.embedding_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai[0].id

  model {
    format  = "OpenAI"
    name    = var.embedding_model_name
    version = "1"
  }

  sku {
    name     = "Standard"
    capacity = 5
  }

  depends_on = [azurerm_cognitive_deployment.mini]
}
