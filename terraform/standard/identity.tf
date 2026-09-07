# =============================================================================
# MANAGED IDENTITY + ROLE ASSIGNMENTS
# =============================================================================

resource "azurerm_user_assigned_identity" "main" {
  name                = "${var.prefix}-identity"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

# --- Built-in Role Definition IDs ---

locals {
  role_cognitive_services_openai_contributor = "a001fd3d-188f-4b5d-821b-7da978bf7442"
  role_search_index_data_contributor         = "8ebe5a00-799e-43f5-93ac-243d3dce84a7"
  role_search_service_contributor            = "7ca78c08-252a-4471-8644-bb5ff32d4ba0"
  role_storage_blob_data_contributor         = "ba92f5b4-2d11-453d-a403-e96b0029c9fe"
  role_key_vault_secrets_user                = "4633458b-17de-408a-b874-0445c86b69e6"
}

# --- Search: Index Data Contributor ---

resource "azurerm_role_assignment" "search_index_data" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# --- Search: Service Contributor ---

resource "azurerm_role_assignment" "search_service" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Service Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# --- OpenAI: Cognitive Services OpenAI Contributor (only when deploying OpenAI locally) ---

resource "azurerm_role_assignment" "openai" {
  count = var.deploy_openai ? 1 : 0

  scope                = azurerm_cognitive_account.openai[0].id
  role_definition_name = "Cognitive Services OpenAI Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# --- Storage: Blob Data Contributor ---

resource "azurerm_role_assignment" "storage_blob" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# --- Key Vault: Secrets User (only when deploying Key Vault) ---

resource "azurerm_role_assignment" "kv_secrets_user" {
  count = var.deploy_key_vault ? 1 : 0

  scope                = azurerm_key_vault.main[0].id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}
