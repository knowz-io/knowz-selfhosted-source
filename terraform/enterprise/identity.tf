# identity.tf — User-assigned managed identity and all role assignments

# -----------------------------------------------------------------------------
# User-Assigned Managed Identity
# -----------------------------------------------------------------------------

resource "azurerm_user_assigned_identity" "main" {
  name                = "${var.prefix}-identity"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.effective_tags
}

# -----------------------------------------------------------------------------
# Role Assignments
# -----------------------------------------------------------------------------

# Search: Index Data Contributor
resource "azurerm_role_assignment" "search_index_data_contributor" {
  scope                = azurerm_search_service.main.id
  role_definition_name = "Search Index Data Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# Search Service Contributor REMOVED — runtime no longer needs schema-mutation rights
# (Builder B commit 3a690a4c: SearchIndexClient.CreateIndexAsync path gated off at app
# layer). Search Index Data Contributor above is sufficient for data-plane read/write.
# Closes the 2026-03 runtime-schema-drift incident. Do NOT re-add without a compensating
# control — see SH_ENTERPRISE_BICEP_HARDENING commit 6 for context.

# OpenAI: Cognitive Services OpenAI User (least-privilege; was Contributor before hardening)
# SH_ENTERPRISE_BICEP_HARDENING §Rule 3 — User allows inference only. Contributor allowed
# deployment mutation (rogue deployments, SKU changes). Gated OFF when BYO OpenAI — then
# byo_openai_user module below emits the role cross-RG instead.
resource "azurerm_role_assignment" "openai_user" {
  count                = local.create_openai ? 1 : 0
  scope                = azurerm_cognitive_account.openai[0].id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# BYO OpenAI: cross-resource role grant at customer resource. Customer deployer SP
# needs Cognitive Services Contributor OR Owner on the target resource for this to apply.
resource "azurerm_role_assignment" "byo_openai_user" {
  count                = var.existing_openai_resource_id != "" ? 1 : 0
  scope                = var.existing_openai_resource_id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# External ACR (air-gapped / policy-restricted pulls): AcrPull role at customer ACR.
# Customer mirrors ghcr.io/knowz-io/knowz-{api,mcp,web}:<tag> into their ACR.
resource "azurerm_role_assignment" "external_acr_pull" {
  count                = var.external_acr_name != "" ? 1 : 0
  scope                = "/subscriptions/${data.azurerm_client_config.current.subscription_id}/resourceGroups/${var.external_acr_resource_group}/providers/Microsoft.ContainerRegistry/registries/${var.external_acr_name}"
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# Data Protection key ring wrap/unwrap on local KV — SH_ENTERPRISE_BICEP_HARDENING §Rule 9.
resource "azurerm_role_assignment" "dp_kv_crypto_user" {
  count                = var.byo_key_vault_id == "" ? 1 : 0
  scope                = azurerm_key_vault.main[0].id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# BYO KV variant of Crypto User.
resource "azurerm_role_assignment" "byo_dp_kv_crypto_user" {
  count                = var.byo_key_vault_id != "" ? 1 : 0
  scope                = var.byo_key_vault_id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# BYO KV variant of Secrets User (MI runtime read). Customer pre-grants deployer SP
# Key Vault Secrets Officer on the BYO vault so terraform doesn't try to emit it here.
resource "azurerm_role_assignment" "byo_kv_secrets_user" {
  count                = var.byo_key_vault_id != "" ? 1 : 0
  scope                = var.byo_key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# Storage: Blob Data Contributor
resource "azurerm_role_assignment" "storage_blob_data_contributor" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# Key Vault: Secrets User (managed identity — read secrets at runtime)
resource "azurerm_role_assignment" "keyvault_secrets_user" {
  count                = var.byo_key_vault_id == "" ? 1 : 0
  scope                = azurerm_key_vault.main[0].id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.main.principal_id
  principal_type       = "ServicePrincipal"
}

# Key Vault: Secrets Officer (deployer — create secrets at deploy time)
# RBAC-enabled vaults require explicit data-plane role assignment even for
# subscription Owners. Grant the deploying identity Secrets Officer so
# Terraform can create the secrets below. Wait for RBAC propagation (~45s).
resource "azurerm_role_assignment" "kv_deployer_secrets_officer" {
  count                = var.byo_key_vault_id == "" ? 1 : 0
  scope                = azurerm_key_vault.main[0].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "time_sleep" "wait_kv_rbac" {
  depends_on      = [azurerm_role_assignment.kv_deployer_secrets_officer]
  create_duration = "45s"
}
