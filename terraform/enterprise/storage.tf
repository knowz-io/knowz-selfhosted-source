# storage.tf — Storage account (GRS, network ACLs) + private endpoint + diagnostics

# -----------------------------------------------------------------------------
# Storage Account (hardened: GRS, no public blob, deny-all network)
# -----------------------------------------------------------------------------

resource "azurerm_storage_account" "main" {
  name                            = local.storage_account_name
  location                        = azurerm_resource_group.main.location
  resource_group_name             = azurerm_resource_group.main.name
  account_tier                    = "Standard"
  account_replication_type        = "GRS"
  account_kind                    = "StorageV2"
  access_tier                     = "Hot"
  https_traffic_only_enabled      = true
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  # MI-swap landed (Builder B commit 3a690a4c): BlobServiceClient uses DefaultAzureCredential
  # + Storage:Azure:AccountUrl (no AccountKey). Shared-key access disabled for enterprise.
  shared_access_key_enabled = false
  tags                      = local.effective_tags

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
  }
}

resource "azurerm_storage_container" "files" {
  name                  = "selfhosted-files"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# Data Protection key ring container — SH_ENTERPRISE_BICEP_HARDENING §Rule 9.
# App persists ASP.NET Core DP key ring here (wrapped by dp-master-key KV key).
resource "azurerm_storage_container" "dp_keys" {
  name                  = "dp-keys"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# -----------------------------------------------------------------------------
# Private Endpoint: Storage Blob
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "storage" {
  name                = "${var.prefix}-pe-storage"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-storage"
    private_connection_resource_id = azurerm_storage_account.main.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["blob"].id]
  }
}

# -----------------------------------------------------------------------------
# Diagnostic Settings: Storage Blob
# -----------------------------------------------------------------------------

resource "azurerm_monitor_diagnostic_setting" "storage" {
  name                       = "${var.prefix}-storage-diagnostics"
  target_resource_id         = "${azurerm_storage_account.main.id}/blobServices/default"
  log_analytics_workspace_id = local.effective_log_analytics_workspace_id

  enabled_log {
    category = "StorageRead"
  }

  enabled_log {
    category = "StorageWrite"
  }

  enabled_log {
    category = "StorageDelete"
  }

  enabled_metric {
    category = "Transaction"
  }
}
