# sql.tf — SQL Server (AAD-only) + database + Defender + auditing + backup + private endpoint

# -----------------------------------------------------------------------------
# SQL Server (AAD-only authentication, no SQL admin password)
# -----------------------------------------------------------------------------

resource "azurerm_mssql_server" "main" {
  name                          = local.sql_server_name
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = false
  tags                          = local.effective_tags

  azuread_administrator {
    login_username              = azurerm_user_assigned_identity.main.name
    object_id                   = azurerm_user_assigned_identity.main.principal_id
    azuread_authentication_only = true
    tenant_id                   = data.azurerm_client_config.current.tenant_id
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.main.id]
  }

  primary_user_assigned_identity_id = azurerm_user_assigned_identity.main.id
}

# -----------------------------------------------------------------------------
# Database
# -----------------------------------------------------------------------------

# SH_ENTERPRISE_BICEP_HARDENING §Rule 2: Basic@2GB → S1@250GB (parameterized).
# Basic@2GB dies at ~5 concurrent enrichment jobs. Customers can opt UP to P1 or
# DOWN to Basic via var.sql_database_sku_name.
resource "azurerm_mssql_database" "main" {
  name           = "McpKnowledge"
  server_id      = azurerm_mssql_server.main.id
  collation      = "SQL_Latin1_General_CP1_CI_AS"
  max_size_gb    = floor(var.sql_database_max_size_bytes / 1073741824)
  sku_name       = var.sql_database_sku_name
  zone_redundant = false
  tags           = local.effective_tags

  short_term_retention_policy {
    retention_days = 35
  }

  long_term_retention_policy {
    weekly_retention = "P26W"
  }
}

# -----------------------------------------------------------------------------
# SQL Server Auditing
# -----------------------------------------------------------------------------

resource "azurerm_mssql_server_extended_auditing_policy" "main" {
  server_id              = azurerm_mssql_server.main.id
  log_monitoring_enabled = true
}

# -----------------------------------------------------------------------------
# Defender for SQL (Advanced Threat Protection)
# -----------------------------------------------------------------------------

resource "azurerm_mssql_server_security_alert_policy" "main" {
  resource_group_name = azurerm_resource_group.main.name
  server_name         = azurerm_mssql_server.main.name
  state               = "Enabled"
}

# SQL Vulnerability Assessment (requires Defender/Security Alert Policy)
resource "azurerm_mssql_server_vulnerability_assessment" "main" {
  server_security_alert_policy_id = azurerm_mssql_server_security_alert_policy.main.id
  storage_container_path          = "${azurerm_storage_account.main.primary_blob_endpoint}vulnerability-assessments"
  storage_account_access_key      = azurerm_storage_account.main.primary_access_key

  recurring_scans {
    enabled                   = true
    email_subscription_admins = true
  }
}

# -----------------------------------------------------------------------------
# Deployment Script: Grant user-provided AAD admin access to SQL database
# Uses ARM template deployment since Terraform has no native deployment script resource.
# The managed identity is the SQL AAD admin (for migrations), so it runs a script
# that grants db_owner to the user-specified admin group/user.
# -----------------------------------------------------------------------------

resource "azurerm_resource_group_template_deployment" "sql_permissions" {
  name                = "${var.prefix}-sql-permission-script"
  resource_group_name = azurerm_resource_group.main.name
  deployment_mode     = "Incremental"

  parameters_content = jsonencode({
    prefix              = { value = var.prefix }
    location            = { value = azurerm_resource_group.main.location }
    managedIdentityId   = { value = azurerm_user_assigned_identity.main.id }
    sqlServerFqdn       = { value = azurerm_mssql_server.main.fully_qualified_domain_name }
    aadAdminObjectId    = { value = var.aad_admin_object_id }
    aadAdminDisplayName = { value = var.aad_admin_display_name }
    tags                = { value = local.effective_tags }
  })

  template_content = jsonencode({
    "$schema"      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"
    contentVersion = "1.0.0.0"
    parameters = {
      prefix              = { type = "string" }
      location            = { type = "string" }
      managedIdentityId   = { type = "string" }
      sqlServerFqdn       = { type = "string" }
      aadAdminObjectId    = { type = "string" }
      aadAdminDisplayName = { type = "string" }
      tags                = { type = "object" }
    }
    resources = [
      {
        type       = "Microsoft.Resources/deploymentScripts"
        apiVersion = "2023-08-01"
        name       = "[concat(parameters('prefix'), '-sql-permission-script')]"
        location   = "[parameters('location')]"
        tags       = "[parameters('tags')]"
        kind       = "AzurePowerShell"
        identity = {
          type = "UserAssigned"
          userAssignedIdentities = {
            "[parameters('managedIdentityId')]" = {}
          }
        }
        properties = {
          azPowerShellVersion = "11.0"
          retentionInterval   = "PT1H"
          timeout             = "PT10M"
          arguments           = "[format('-SqlServerFqdn {0} -DatabaseName McpKnowledge -AadAdminObjectId {1} -AadAdminDisplayName ''{2}''', parameters('sqlServerFqdn'), parameters('aadAdminObjectId'), parameters('aadAdminDisplayName'))]"
          scriptContent       = <<-SCRIPT
            param($SqlServerFqdn, $DatabaseName, $AadAdminObjectId, $AadAdminDisplayName)
            Install-Module -Name SqlServer -Force -AllowClobber -Scope CurrentUser
            $token = (Get-AzAccessToken -ResourceUrl "https://database.windows.net/").Token
            $query = @"
              IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$AadAdminDisplayName')
              BEGIN
                CREATE USER [$AadAdminDisplayName] WITH SID = $(CONVERT(varbinary(16), '$AadAdminObjectId')), TYPE = E;
                ALTER ROLE db_owner ADD MEMBER [$AadAdminDisplayName];
              END
"@
            Invoke-Sqlcmd -ServerInstance $SqlServerFqdn -Database $DatabaseName -AccessToken $token -Query $query
          SCRIPT
        }
      }
    ]
  })

  depends_on = [azurerm_mssql_database.main]
}

# -----------------------------------------------------------------------------
# Private Endpoint: SQL Server
# -----------------------------------------------------------------------------

resource "azurerm_private_endpoint" "sql" {
  name                = "${var.prefix}-pe-sql"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = local.private_endpoints_subnet_id
  tags                = local.effective_tags

  private_service_connection {
    name                           = "${var.prefix}-plsc-sql"
    private_connection_resource_id = azurerm_mssql_server.main.id
    subresource_names              = ["sqlServer"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.zones["sql"].id]
  }
}

# -----------------------------------------------------------------------------
# Diagnostic Settings: SQL Database
# -----------------------------------------------------------------------------

resource "azurerm_monitor_diagnostic_setting" "sql_db" {
  name                       = "${var.prefix}-sqldb-diagnostics"
  target_resource_id         = azurerm_mssql_database.main.id
  log_analytics_workspace_id = local.effective_log_analytics_workspace_id

  enabled_log {
    category = "SQLSecurityAuditEvents"
  }

  enabled_log {
    category = "QueryStoreRuntimeStatistics"
  }

  enabled_log {
    category = "Errors"
  }

  enabled_metric {
    category = "Basic"
  }
}
