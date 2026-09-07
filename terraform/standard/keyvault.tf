# =============================================================================
# KEY VAULT (conditional) + SECRETS
# =============================================================================

resource "azurerm_key_vault" "main" {
  count = var.deploy_key_vault ? 1 : 0

  name                = local.key_vault_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled    = true
  soft_delete_retention_days    = 90
  purge_protection_enabled      = true
  public_network_access_enabled = true

  network_acls {
    bypass         = "AzureServices"
    default_action = "Allow"
  }

  tags = local.tags
}

# =============================================================================
# DEPLOYER RBAC (Key Vault Secrets Officer)
# =============================================================================
# RBAC-enabled Key Vaults require explicit data-plane role assignment even for
# subscription Owners. Grant the deploying identity Key Vault Secrets Officer
# so Terraform can create the secrets below. Wait for RBAC propagation (~45s).

resource "azurerm_role_assignment" "kv_deployer_secrets_officer" {
  count = var.deploy_key_vault ? 1 : 0

  scope                = azurerm_key_vault.main[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "time_sleep" "wait_kv_rbac" {
  count = var.deploy_key_vault ? 1 : 0

  depends_on      = [azurerm_role_assignment.kv_deployer_secrets_officer]
  create_duration = "45s"
}

# =============================================================================
# KEY VAULT SECRETS (IConfiguration hierarchy naming: -- maps to :)
# =============================================================================

resource "azurerm_key_vault_secret" "sql_connection" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "ConnectionStrings--McpDb"
  value        = local.sql_connection_string
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "search_endpoint" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureAISearch--Endpoint"
  value        = "https://${azurerm_search_service.main.name}.search.windows.net"
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "search_key" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureAISearch--ApiKey"
  value        = azurerm_search_service.main.primary_key
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_endpoint" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureOpenAI--Endpoint"
  value        = local.effective_openai_endpoint
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_key" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureOpenAI--ApiKey"
  value        = local.effective_openai_key
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_deployment_name" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureOpenAI--DeploymentName"
  value        = var.chat_deployment_name
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_embedding_deployment_name" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureOpenAI--EmbeddingDeploymentName"
  value        = var.embedding_deployment_name
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "doc_intel_endpoint" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureDocumentIntelligence--Endpoint"
  value        = local.effective_doc_intel_endpoint
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "doc_intel_key" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureDocumentIntelligence--ApiKey"
  value        = local.effective_doc_intel_key
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "vision_endpoint" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureAIVision--Endpoint"
  value        = local.effective_vision_endpoint
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "vision_key" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "AzureAIVision--ApiKey"
  value        = local.effective_vision_key
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "storage_connection" {
  count = var.deploy_key_vault ? 1 : 0

  name         = "Storage--Azure--ConnectionString"
  value        = local.storage_connection_string
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "app_insights" {
  count = var.deploy_key_vault && var.deploy_monitoring ? 1 : 0

  name         = "ApplicationInsights--ConnectionString"
  value        = local.effective_app_insights_connection_string
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

# --- Container Apps secrets (conditional on both Key Vault and Container Apps) ---

resource "azurerm_key_vault_secret" "selfhosted_api_key" {
  count = var.deploy_container_apps && var.deploy_key_vault ? 1 : 0

  name         = "SelfHosted--ApiKey"
  value        = local.api_key
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "selfhosted_jwt_secret" {
  count = var.deploy_container_apps && var.deploy_key_vault ? 1 : 0

  name         = "SelfHosted--JwtSecret"
  value        = local.jwt_secret
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "selfhosted_admin_password" {
  count = var.deploy_container_apps && var.deploy_key_vault ? 1 : 0

  name         = "SelfHosted--SuperAdminPassword"
  value        = var.admin_password
  key_vault_id = azurerm_key_vault.main[0].id

  depends_on = [time_sleep.wait_kv_rbac]
}
