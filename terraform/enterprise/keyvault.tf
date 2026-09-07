# keyvault.tf — Key Vault (purge protection, RBAC, private endpoint) + secrets

# -----------------------------------------------------------------------------
# Key Vault (hardened: purge protection, RBAC auth, private endpoint)
# -----------------------------------------------------------------------------

locals {
  byo_key_vault_name    = var.byo_key_vault_id != "" ? split("/", var.byo_key_vault_id)[8] : ""
  byo_key_vault_rg_name = var.byo_key_vault_id != "" ? split("/", var.byo_key_vault_id)[4] : ""
}

resource "azurerm_key_vault" "main" {
  count                         = var.byo_key_vault_id == "" ? 1 : 0
  name                          = local.key_vault_name
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  soft_delete_retention_days    = 90
  purge_protection_enabled      = true
  public_network_access_enabled = false
  tags                          = local.effective_tags

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
  }
}

data "azurerm_key_vault" "byo" {
  count               = var.byo_key_vault_id != "" ? 1 : 0
  name                = local.byo_key_vault_name
  resource_group_name = local.byo_key_vault_rg_name
}

locals {
  effective_key_vault_id   = var.byo_key_vault_id != "" ? var.byo_key_vault_id : azurerm_key_vault.main[0].id
  effective_key_vault_name = var.byo_key_vault_id != "" ? data.azurerm_key_vault.byo[0].name : azurerm_key_vault.main[0].name
  effective_key_vault_uri  = var.byo_key_vault_id != "" ? data.azurerm_key_vault.byo[0].vault_uri : azurerm_key_vault.main[0].vault_uri
}

# -----------------------------------------------------------------------------
# Private Endpoint: Key Vault
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "keyvault" {
  count               = var.byo_key_vault_id == "" ? 1 : 0
  name                = "${var.prefix}-pe-kv"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-kv"
    private_connection_resource_id = azurerm_key_vault.main[0].id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["keyvault"].id]
  }
}

# -----------------------------------------------------------------------------
# Key Vault Secrets
# -----------------------------------------------------------------------------

resource "azurerm_key_vault_secret" "sql_connection" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "ConnectionStrings--McpDb"
  value        = local.sql_connection_string
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "search_endpoint" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureAISearch--Endpoint"
  value        = "https://${azurerm_search_service.main.name}.search.windows.net"
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "search_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureAISearch--ApiKey"
  value        = azurerm_search_service.main.primary_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_endpoint" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureOpenAI--Endpoint"
  value        = local.effective_openai_endpoint
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "openai_key" {
  count        = var.byo_key_vault_id == "" && !local.openai_use_mi ? 1 : 0
  name         = "AzureOpenAI--ApiKey"
  value        = local.effective_openai_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "docintel_endpoint" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureDocumentIntelligence--Endpoint"
  value        = local.effective_docintel_endpoint
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "docintel_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureDocumentIntelligence--ApiKey"
  value        = local.effective_docintel_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "vision_endpoint" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureAIVision--Endpoint"
  value        = local.effective_vision_endpoint
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "vision_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "AzureAIVision--ApiKey"
  value        = local.effective_vision_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

# SH_ENTERPRISE_BICEP_HARDENING §MI-swap: Storage--Azure--ConnectionString RETIRED.
# App reads Storage--Azure--AccountUrl + uses DefaultAzureCredential.
resource "azurerm_key_vault_secret" "storage_account_url" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "Storage--Azure--AccountUrl"
  value        = azurerm_storage_account.main.primary_blob_endpoint
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "appinsights_connection" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "ApplicationInsights--ConnectionString"
  value        = azurerm_application_insights.main.connection_string
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "api_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "SelfHosted--ApiKey"
  value        = local.effective_api_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "jwt_secret" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "SelfHosted--JwtSecret"
  value        = local.effective_jwt_secret
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

resource "azurerm_key_vault_secret" "admin_password" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "SelfHosted--SuperAdminPassword"
  value        = var.admin_password
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

# MCP service key KV secret — the random_uuid resource itself is declared in main.tf
# so local.mcp_service_key can reference it. SH_ENTERPRISE_BICEP_HARDENING §Rule 4.
# Previous pattern was deterministic (uniqueString per RG name → predictable).
resource "azurerm_key_vault_secret" "mcp_service_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "MCP--ServiceKey"
  value        = local.mcp_service_key
  key_vault_id = local.effective_key_vault_id
  depends_on   = [azurerm_role_assignment.keyvault_secrets_user, time_sleep.wait_kv_rbac]
}

# Data Protection master key — SH_ENTERPRISE_BICEP_HARDENING §Rule 9.
# Wraps the ASP.NET Core DP key ring persisted to the dp-keys blob container.
# Name MUST match app-side default at selfhosted/src/Knowz.SelfHosted.API/Program.cs:50.
resource "azurerm_key_vault_key" "dp_master_key" {
  count        = var.byo_key_vault_id == "" ? 1 : 0
  name         = "selfhosted-dp-key"
  key_vault_id = local.effective_key_vault_id
  key_type     = "RSA"
  key_size     = 2048
  key_opts     = ["wrapKey", "unwrapKey"]

  rotation_policy {
    automatic {
      time_after_creation = "P90D"
    }
    expire_after         = "P2Y"
    notify_before_expiry = "P30D"
  }

  depends_on = [
    azurerm_role_assignment.kv_deployer_secrets_officer,
    time_sleep.wait_kv_rbac,
  ]
}
