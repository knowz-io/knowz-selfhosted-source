# monitoring.tf — Log Analytics + Application Insights + diagnostic settings

# -----------------------------------------------------------------------------
# Log Analytics Workspace
# -----------------------------------------------------------------------------

locals {
  central_law_name    = var.central_log_analytics_id != "" ? split("/", var.central_log_analytics_id)[8] : ""
  central_law_rg_name = var.central_log_analytics_id != "" ? split("/", var.central_log_analytics_id)[4] : ""
}

resource "azurerm_log_analytics_workspace" "main" {
  count                      = var.central_log_analytics_id == "" ? 1 : 0
  name                       = "${var.prefix}-logs"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  sku                        = "PerGB2018"
  retention_in_days          = 90
  daily_quota_gb             = 5
  internet_ingestion_enabled = true
  internet_query_enabled     = true
  tags                       = local.effective_tags
}

data "azurerm_log_analytics_workspace" "central" {
  count               = var.central_log_analytics_id != "" ? 1 : 0
  name                = local.central_law_name
  resource_group_name = local.central_law_rg_name
}

locals {
  effective_log_analytics_workspace_id   = var.central_log_analytics_id != "" ? var.central_log_analytics_id : azurerm_log_analytics_workspace.main[0].id
  effective_log_analytics_workspace_name = var.central_log_analytics_id != "" ? data.azurerm_log_analytics_workspace.central[0].name : azurerm_log_analytics_workspace.main[0].name
}

# -----------------------------------------------------------------------------
# Application Insights
# -----------------------------------------------------------------------------

resource "azurerm_application_insights" "main" {
  name                = "${var.prefix}-appinsights"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = local.effective_log_analytics_workspace_id
  application_type    = "web"
  tags                = local.effective_tags
}

# -----------------------------------------------------------------------------
# Diagnostic Settings: Key Vault
# -----------------------------------------------------------------------------

resource "azurerm_monitor_diagnostic_setting" "keyvault" {
  count                      = var.byo_key_vault_id == "" ? 1 : 0
  name                       = "${var.prefix}-kv-diagnostics"
  target_resource_id         = azurerm_key_vault.main[0].id
  log_analytics_workspace_id = local.effective_log_analytics_workspace_id

  enabled_log {
    category = "AuditEvent"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

# -----------------------------------------------------------------------------
# Diagnostic Settings: Front Door
# -----------------------------------------------------------------------------

resource "azurerm_monitor_diagnostic_setting" "frontdoor" {
  name                       = "${var.prefix}-fd-diagnostics"
  target_resource_id         = azurerm_cdn_frontdoor_profile.main.id
  log_analytics_workspace_id = local.effective_log_analytics_workspace_id

  enabled_log {
    category = "FrontDoorAccessLog"
  }

  enabled_log {
    category = "FrontDoorWebApplicationFirewallLog"
  }

  enabled_log {
    category = "FrontDoorHealthProbeLog"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
