# =============================================================================
# SQL SERVER + DATABASE + FIREWALL RULES
# =============================================================================

resource "azurerm_mssql_server" "main" {
  name                          = "${var.prefix}-sql-${local.unique_suffix}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  version                       = "12.0"
  administrator_login           = var.sql_admin_username
  administrator_login_password  = var.sql_admin_password
  minimum_tls_version           = "1.2"
  public_network_access_enabled = var.sql_public_network_access_enabled

  tags = local.tags
}

# Allow Azure services to access SQL Server. The standard tier has no VNet or
# private endpoint (see network.tf), so the Container Apps reach SQL through
# the public endpoint — this rule is what permits that traffic. All other
# public access is denied unless explicitly listed in sql_allowed_cidrs.
resource "azurerm_mssql_firewall_rule" "allow_azure" {
  name             = "AllowAllAzureIps"
  server_id        = azurerm_mssql_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# Scoped opt-in public access (local dev, CI/CD, EF migrations). Each CIDR in
# sql_allowed_cidrs gets its own firewall rule; the default is an empty list,
# so no non-Azure public access exists unless a customer consciously supplies
# their own ranges. Internet-wide access (0.0.0.0/0) is rejected by validation.
resource "azurerm_mssql_firewall_rule" "allowed_cidrs" {
  for_each = toset(var.sql_allowed_cidrs)

  name             = "AllowedCidr-${replace(replace(each.value, ".", "-"), "/", "-")}"
  server_id        = azurerm_mssql_server.main.id
  start_ip_address = cidrhost(each.value, 0)
  end_ip_address   = cidrhost(each.value, pow(2, 32 - tonumber(split("/", each.value)[1])) - 1)
}

# Single database for self-hosted MCP (NOT the dual KnowzMaster/KnowzKnowledge pattern)
resource "azurerm_mssql_database" "mcp" {
  name      = "McpKnowledge"
  server_id = azurerm_mssql_server.main.id
  collation = "SQL_Latin1_General_CP1_CI_AS"
  sku_name  = "Basic"

  max_size_gb    = 2
  zone_redundant = false

  tags = local.tags
}
