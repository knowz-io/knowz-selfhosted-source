# =============================================================================
# STORAGE ACCOUNT + BLOB CONTAINER
# =============================================================================

resource "azurerm_storage_account" "main" {
  name                       = local.storage_account_name
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  account_tier               = "Standard"
  account_replication_type   = "LRS"
  account_kind               = "StorageV2"
  access_tier                = "Hot"
  min_tls_version            = "TLS1_2"
  https_traffic_only_enabled = true

  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = var.storage_allow_shared_key_access

  tags = local.tags
}

resource "azurerm_storage_container" "files" {
  name                  = "selfhosted-files"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}
