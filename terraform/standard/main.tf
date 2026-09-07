# =============================================================================
# Knowz Self-Hosted — Standard Terraform Module
# =============================================================================
# Translated from: selfhosted/infrastructure/selfhosted-test.bicep
#
# Provisions: AI Search, Azure OpenAI + model deployments, SQL + McpKnowledge DB,
# Blob Storage, Managed Identity + RBAC, Key Vault + secrets, Log Analytics +
# App Insights, Container Apps (API/MCP/Web).
#
# Usage:
#   terraform init
#   terraform plan -var sql_admin_password='<secure>'
#   terraform apply -var sql_admin_password='<secure>'
# =============================================================================

# --- Data Sources ---

data "azurerm_client_config" "current" {}

# --- Resource Group ---

resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

# --- Locals ---

locals {
  # Unique suffix derived from resource group ID (mirrors Bicep uniqueString)
  unique_suffix = substr(sha256(azurerm_resource_group.main.id), 0, 13)

  # Effective search location (use primary location if not overridden)
  search_location = var.search_location != "" ? var.search_location : var.location

  # Storage account naming (must be globally unique, lowercase, no hyphens)
  storage_prefix       = lower(substr(replace(var.prefix, "-", ""), 0, min(8, length(replace(var.prefix, "-", "")))))
  storage_account_name = lower("${local.storage_prefix}st${substr(local.unique_suffix, 0, 12)}")

  # Key Vault naming (globally unique, 3-24 chars, alphanumeric + hyphens)
  kv_prefix      = lower(substr(replace(var.prefix, "-", ""), 0, min(8, length(replace(var.prefix, "-", "")))))
  key_vault_name = "${local.kv_prefix}kv${substr(local.unique_suffix, 0, 8)}"

  # MCP service key (deterministic from resource group)
  mcp_service_key = "selfhosted-test-mcp-service-key-${local.unique_suffix}"

  # Connection strings
  sql_connection_string = "Server=tcp:${azurerm_mssql_server.main.fully_qualified_domain_name},1433;Initial Catalog=McpKnowledge;Persist Security Info=False;User ID=${var.sql_admin_username};Password=${var.sql_admin_password};MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

  storage_connection_string = "DefaultEndpointsProtocol=https;EndpointSuffix=core.windows.net;AccountName=${azurerm_storage_account.main.name};AccountKey=${azurerm_storage_account.main.primary_access_key}"

  # Effective OpenAI endpoint — 3-tier resolution: deployed -> existing -> external
  effective_openai_endpoint = var.deploy_openai ? azurerm_cognitive_account.openai[0].endpoint : (
    var.existing_openai_resource_name != "" ? data.azurerm_cognitive_account.openai_existing[0].endpoint : var.external_openai_endpoint
  )

  # Effective OpenAI key — 3-tier resolution: deployed -> existing -> external
  effective_openai_key = var.deploy_openai ? azurerm_cognitive_account.openai[0].primary_access_key : (
    var.existing_openai_resource_name != "" ? data.azurerm_cognitive_account.openai_existing[0].primary_access_key : var.external_openai_key
  )

  # Effective Document Intelligence endpoint — 3-tier resolution: deployed -> existing -> external
  effective_doc_intel_endpoint = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].endpoint : (
    var.existing_docintel_resource_name != "" ? data.azurerm_cognitive_account.docintel_existing[0].endpoint : var.external_doc_intel_endpoint
  )

  # Effective Document Intelligence key — 3-tier resolution: deployed -> existing -> external
  effective_doc_intel_key = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].primary_access_key : (
    var.existing_docintel_resource_name != "" ? data.azurerm_cognitive_account.docintel_existing[0].primary_access_key : var.external_doc_intel_key
  )

  # Effective Vision endpoint — 3-tier resolution: deployed -> existing -> external
  effective_vision_endpoint = var.deploy_vision ? azurerm_cognitive_account.vision[0].endpoint : (
    var.existing_vision_resource_name != "" ? data.azurerm_cognitive_account.vision_existing[0].endpoint : var.external_vision_endpoint
  )

  # Effective Vision key — 3-tier resolution: deployed -> existing -> external
  effective_vision_key = var.deploy_vision ? azurerm_cognitive_account.vision[0].primary_access_key : (
    var.existing_vision_resource_name != "" ? data.azurerm_cognitive_account.vision_existing[0].primary_access_key : var.external_vision_key
  )

  # App Insights connection string (empty when monitoring not deployed)
  effective_app_insights_connection_string = var.deploy_monitoring ? azurerm_application_insights.main[0].connection_string : ""

  # Container Apps deployment name defaults
  effective_ca_deployment_name = var.ca_deployment_name != "" ? var.ca_deployment_name : var.chat_deployment_name

  # Embedding model/dim for Container Apps (fall back to the deploy-time values).
  effective_ca_embedding_model_name = var.ca_embedding_model_name != "" ? var.ca_embedding_model_name : var.embedding_model_name
  effective_ca_embedding_dimensions = var.ca_embedding_dimensions > 0 ? var.ca_embedding_dimensions : var.embedding_dimensions

  # API key / JWT secret (auto-generate if empty)
  api_key    = var.api_key != "" ? var.api_key : "sh-${random_password.api_key[0].result}"
  jwt_secret = var.jwt_secret != "" ? var.jwt_secret : random_password.jwt_secret[0].result

  # Default tags (merged with user-provided tags)
  default_tags = {
    project      = "knowz-selfhosted"
    environment  = var.prefix
    "managed-by" = "terraform"
  }
  tags = merge(local.default_tags, var.tags)
}

# --- Auto-generated secrets ---

resource "random_password" "api_key" {
  count   = var.api_key == "" ? 1 : 0
  length  = 32
  special = false
}

resource "random_password" "jwt_secret" {
  count   = var.jwt_secret == "" ? 1 : 0
  length  = 64
  special = true
}

# --- Data sources for existing AI resources ---
# Look up pre-existing Cognitive / AIServices resources to reuse (tier-2 mode).
# Enabled when deploy_* = false AND existing_*_resource_name is set.

data "azurerm_cognitive_account" "openai_existing" {
  count               = (!var.deploy_openai && var.existing_openai_resource_name != "") ? 1 : 0
  name                = var.existing_openai_resource_name
  resource_group_name = var.existing_openai_resource_group
}

data "azurerm_cognitive_account" "vision_existing" {
  count               = (!var.deploy_vision && var.existing_vision_resource_name != "") ? 1 : 0
  name                = var.existing_vision_resource_name
  resource_group_name = var.existing_vision_resource_group
}

data "azurerm_cognitive_account" "docintel_existing" {
  count               = (!var.deploy_document_intelligence && var.existing_docintel_resource_name != "") ? 1 : 0
  name                = var.existing_docintel_resource_name
  resource_group_name = var.existing_docintel_resource_group
}

# --- AI service configuration validation ---
# Ensures at least one credential source is configured per AI service.
# Check blocks emit warnings rather than hard errors (unlike variable validation).

check "openai_credentials" {
  assert {
    condition     = var.deploy_openai || var.existing_openai_resource_name != "" || (var.external_openai_endpoint != "" && var.external_openai_key != "")
    error_message = "When deploy_openai=false, provide either existing_openai_resource_name+resource_group OR external_openai_endpoint+key."
  }
}

check "vision_credentials" {
  assert {
    condition     = var.deploy_vision || var.existing_vision_resource_name != "" || (var.external_vision_endpoint != "" && var.external_vision_key != "")
    error_message = "When deploy_vision=false, provide either existing_vision_resource_name+resource_group OR external_vision_endpoint+key."
  }
}

check "docintel_credentials" {
  assert {
    condition     = var.deploy_document_intelligence || var.existing_docintel_resource_name != "" || (var.external_doc_intel_endpoint != "" && var.external_doc_intel_key != "")
    error_message = "When deploy_document_intelligence=false, provide either existing_docintel_resource_name+resource_group OR external_doc_intel_endpoint+key."
  }
}
