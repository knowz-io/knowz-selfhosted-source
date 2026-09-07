# =============================================================================
# LOG ANALYTICS WORKSPACE + APPLICATION INSIGHTS (conditional)
# =============================================================================

resource "azurerm_log_analytics_workspace" "main" {
  count = var.deploy_monitoring ? 1 : 0

  name                = "${var.prefix}-logs"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = 1

  tags = local.tags
}

resource "azurerm_application_insights" "main" {
  count = var.deploy_monitoring ? 1 : 0

  name                = "${var.prefix}-appinsights"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  workspace_id        = azurerm_log_analytics_workspace.main[0].id
  application_type    = "web"

  internet_ingestion_enabled = true
  internet_query_enabled     = true

  tags = local.tags
}

# =============================================================================
# DIAGNOSTIC SETTINGS (Key Vault audit logs -> Log Analytics)
# =============================================================================

resource "azurerm_monitor_diagnostic_setting" "keyvault" {
  count = var.deploy_key_vault && var.deploy_monitoring ? 1 : 0

  name                       = "${var.prefix}-kv-diagnostics"
  target_resource_id         = azurerm_key_vault.main[0].id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main[0].id

  enabled_log {
    category = "AuditEvent"
  }
}
