# =============================================================================
# OUTPUTS (non-secret values only)
# =============================================================================

# --- Azure AI Search ---

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

# --- Azure OpenAI ---

output "openai_endpoint" {
  description = "Effective OpenAI endpoint (local or external)"
  value       = local.effective_openai_endpoint
}

output "openai_resource_name" {
  description = "Azure OpenAI resource name (or 'external')"
  value       = var.deploy_openai ? azurerm_cognitive_account.openai[0].name : "external"
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

# --- Document Intelligence ---

output "document_intelligence_endpoint" {
  description = "Effective Document Intelligence endpoint (local or external)"
  value       = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].endpoint : var.external_doc_intel_endpoint
}

output "document_intelligence_name" {
  description = "Document Intelligence resource name (or 'external')"
  value       = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].name : "external"
}

# --- Azure AI Vision ---

output "vision_endpoint" {
  description = "Effective Azure AI Vision endpoint (local or external)"
  value       = var.deploy_vision ? azurerm_cognitive_account.vision[0].endpoint : var.external_vision_endpoint
}

output "vision_name" {
  description = "Azure AI Vision resource name (or 'external')"
  value       = var.deploy_vision ? azurerm_cognitive_account.vision[0].name : "external"
}

# --- SQL ---

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

# --- Storage ---

output "storage_account_name" {
  description = "Storage account name"
  value       = azurerm_storage_account.main.name
}

output "storage_blob_endpoint" {
  description = "Storage blob endpoint URL"
  value       = azurerm_storage_account.main.primary_blob_endpoint
}

# --- Managed Identity ---

output "managed_identity_id" {
  description = "User-assigned managed identity resource ID"
  value       = azurerm_user_assigned_identity.main.id
}

output "managed_identity_principal_id" {
  description = "User-assigned managed identity principal ID"
  value       = azurerm_user_assigned_identity.main.principal_id
}

output "managed_identity_client_id" {
  description = "User-assigned managed identity client ID"
  value       = azurerm_user_assigned_identity.main.client_id
}

# --- Key Vault ---

output "key_vault_name" {
  description = "Key Vault name (empty if not deployed)"
  value       = var.deploy_key_vault ? azurerm_key_vault.main[0].name : ""
}

output "key_vault_uri" {
  description = "Key Vault URI (empty if not deployed)"
  value       = var.deploy_key_vault ? azurerm_key_vault.main[0].vault_uri : ""
}

# --- Monitoring ---

output "log_analytics_workspace_id" {
  description = "Log Analytics workspace resource ID (empty if not deployed)"
  value       = var.deploy_monitoring ? azurerm_log_analytics_workspace.main[0].id : ""
}

output "log_analytics_workspace_name" {
  description = "Log Analytics workspace name (empty if not deployed)"
  value       = var.deploy_monitoring ? azurerm_log_analytics_workspace.main[0].name : ""
}

output "app_insights_name" {
  description = "Application Insights name (empty if not deployed)"
  value       = var.deploy_monitoring ? azurerm_application_insights.main[0].name : ""
}

output "app_insights_connection_string" {
  description = "Application Insights connection string (empty if not deployed)"
  value       = var.deploy_monitoring ? azurerm_application_insights.main[0].connection_string : ""
  sensitive   = true
}

output "app_insights_instrumentation_key" {
  description = "Application Insights instrumentation key (empty if not deployed)"
  value       = var.deploy_monitoring ? azurerm_application_insights.main[0].instrumentation_key : ""
  sensitive   = true
}

# --- Container Apps ---

output "api_container_app_fqdn" {
  description = "API Container App FQDN (empty if not deployed)"
  value       = var.deploy_container_apps ? azurerm_container_app.api[0].ingress[0].fqdn : ""
}

output "mcp_container_app_fqdn" {
  description = "MCP Container App FQDN (empty if not deployed)"
  value       = var.deploy_container_apps ? azurerm_container_app.mcp[0].ingress[0].fqdn : ""
}

output "web_container_app_fqdn" {
  description = "Web Container App FQDN (empty if not deployed)"
  value       = var.deploy_container_apps ? azurerm_container_app.web[0].ingress[0].fqdn : ""
}

# --- AI Configuration Summary ---

output "ai_configuration_summary" {
  description = "Summary of AI service configuration modes (deployed / existing / external)"
  value = {
    openai   = var.deploy_openai ? "deployed:${azurerm_cognitive_account.openai[0].name}" : (var.existing_openai_resource_name != "" ? "existing:${var.existing_openai_resource_name}" : "external")
    vision   = var.deploy_vision ? "deployed:${azurerm_cognitive_account.vision[0].name}" : (var.existing_vision_resource_name != "" ? "existing:${var.existing_vision_resource_name}" : "external")
    docintel = var.deploy_document_intelligence ? "deployed:${azurerm_cognitive_account.docintel[0].name}" : (var.existing_docintel_resource_name != "" ? "existing:${var.existing_docintel_resource_name}" : "external")
  }
}
