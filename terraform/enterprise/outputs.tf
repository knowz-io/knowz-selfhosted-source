# outputs.tf — All outputs including Front Door endpoint

# -----------------------------------------------------------------------------
# Azure AI Search
# -----------------------------------------------------------------------------

output "search_endpoint" {
  description = "Azure AI Search endpoint URL"
  value       = "https://${azurerm_search_service.main.name}.search.windows.net"
}

output "search_service_name" {
  description = "Azure AI Search service name"
  value       = azurerm_search_service.main.name
}

output "search_index_name" {
  description = "Default search index name"
  value       = "knowledge"
}

# -----------------------------------------------------------------------------
# Azure OpenAI
# -----------------------------------------------------------------------------

output "openai_endpoint" {
  description = "Effective OpenAI endpoint (local or external)"
  value       = local.effective_openai_endpoint
}

output "openai_resource_name" {
  description = "Azure OpenAI resource name (or 'external')"
  value       = local.create_openai ? azurerm_cognitive_account.openai[0].name : (var.existing_openai_resource_id != "" ? local.existing_openai_by_id_name : (var.existing_openai_resource_name != "" ? var.existing_openai_resource_name : "external"))
}

output "chat_deployment_name" {
  description = "Chat model deployment name"
  value       = var.chat_deployment_name
}

output "mini_deployment_name" {
  description = "Mini model deployment name"
  value       = "gpt-5.4-mini"
}

output "embedding_deployment_name" {
  description = "Embedding model deployment name"
  value       = var.embedding_deployment_name
}

# -----------------------------------------------------------------------------
# Document Intelligence
# -----------------------------------------------------------------------------

output "document_intelligence_endpoint" {
  description = "Document Intelligence endpoint (local or external)"
  value       = local.effective_docintel_endpoint
}

output "document_intelligence_name" {
  description = "Document Intelligence resource name (or 'external')"
  value       = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].name : "external"
}

# -----------------------------------------------------------------------------
# Azure AI Vision
# -----------------------------------------------------------------------------

output "vision_endpoint" {
  description = "Effective Azure AI Vision endpoint (local or external)"
  value       = var.deploy_vision ? azurerm_cognitive_account.vision[0].endpoint : var.external_vision_endpoint
}

output "vision_name" {
  description = "Azure AI Vision resource name (or 'external')"
  value       = var.deploy_vision ? azurerm_cognitive_account.vision[0].name : "external"
}

# -----------------------------------------------------------------------------
# SQL Database
# -----------------------------------------------------------------------------

output "sql_server_fqdn" {
  description = "SQL Server fully qualified domain name"
  value       = azurerm_mssql_server.main.fully_qualified_domain_name
}

output "sql_database_name" {
  description = "SQL database name"
  value       = "McpKnowledge"
}

output "sql_server_name" {
  description = "SQL Server resource name"
  value       = azurerm_mssql_server.main.name
}

# -----------------------------------------------------------------------------
# Storage
# -----------------------------------------------------------------------------

output "storage_account_name" {
  description = "Storage account name"
  value       = azurerm_storage_account.main.name
}

output "storage_blob_endpoint" {
  description = "Storage blob endpoint URL"
  value       = azurerm_storage_account.main.primary_blob_endpoint
}

# -----------------------------------------------------------------------------
# Managed Identity
# -----------------------------------------------------------------------------

output "managed_identity_id" {
  description = "User-assigned managed identity resource ID"
  value       = azurerm_user_assigned_identity.main.id
}

output "managed_identity_principal_id" {
  description = "User-assigned managed identity principal (object) ID"
  value       = azurerm_user_assigned_identity.main.principal_id
}

output "managed_identity_client_id" {
  description = "User-assigned managed identity client ID"
  value       = azurerm_user_assigned_identity.main.client_id
}

# -----------------------------------------------------------------------------
# Key Vault
# -----------------------------------------------------------------------------

output "key_vault_name" {
  description = "Key Vault resource name"
  value       = local.effective_key_vault_name
}

output "key_vault_uri" {
  description = "Key Vault URI"
  value       = local.effective_key_vault_uri
}

# -----------------------------------------------------------------------------
# Monitoring
# -----------------------------------------------------------------------------

output "log_analytics_workspace_id" {
  description = "Log Analytics workspace resource ID"
  value       = local.effective_log_analytics_workspace_id
}

output "log_analytics_workspace_name" {
  description = "Log Analytics workspace name"
  value       = local.effective_log_analytics_workspace_name
}

output "app_insights_name" {
  description = "Application Insights resource name"
  value       = azurerm_application_insights.main.name
}

output "app_insights_connection_string" {
  description = "Application Insights connection string"
  value       = azurerm_application_insights.main.connection_string
  sensitive   = true
}

output "app_insights_instrumentation_key" {
  description = "Application Insights instrumentation key"
  value       = azurerm_application_insights.main.instrumentation_key
  sensitive   = true
}

# -----------------------------------------------------------------------------
# Container Apps
# -----------------------------------------------------------------------------

output "api_container_app_fqdn" {
  description = "API container app internal FQDN"
  value       = azurerm_container_app.api.ingress[0].fqdn
}

output "mcp_container_app_fqdn" {
  description = "MCP container app internal FQDN"
  value       = azurerm_container_app.mcp.ingress[0].fqdn
}

output "web_container_app_fqdn" {
  description = "Web container app internal FQDN"
  value       = azurerm_container_app.web.ingress[0].fqdn
}

# -----------------------------------------------------------------------------
# Enterprise Additions
# -----------------------------------------------------------------------------

output "front_door_endpoint" {
  description = "Front Door endpoint hostname (the public entry point)"
  value       = azurerm_cdn_frontdoor_endpoint.main.host_name
}

output "front_door_id" {
  description = "Front Door profile ID (use for X-Azure-FDID header validation)"
  value       = azurerm_cdn_frontdoor_profile.main.resource_guid
}

output "vnet_name" {
  description = "Virtual network name"
  value       = var.byo_vnet_subnet_id != "" ? local.byo_vnet_name : azurerm_virtual_network.main[0].name
}

output "vnet_id" {
  description = "Virtual network resource ID"
  value       = local.vnet_id
}

# -----------------------------------------------------------------------------
# AI Configuration Summary
# -----------------------------------------------------------------------------

output "ai_configuration_summary" {
  description = "Summary of AI service configuration modes (deployed / existing / external)"
  value = {
    openai   = local.create_openai ? "deployed:${azurerm_cognitive_account.openai[0].name}" : (var.existing_openai_resource_id != "" ? "existing:${local.existing_openai_by_id_name}" : (var.existing_openai_resource_name != "" ? "existing:${var.existing_openai_resource_name}" : "external"))
    vision   = var.deploy_vision ? "deployed:${azurerm_cognitive_account.vision[0].name}" : (var.existing_vision_resource_name != "" ? "existing:${var.existing_vision_resource_name}" : "external")
    docintel = var.deploy_document_intelligence ? "deployed:${azurerm_cognitive_account.docintel[0].name}" : (var.existing_docintel_resource_name != "" ? "existing:${var.existing_docintel_resource_name}" : "external")
  }
}
