# main.tf — Provider configuration, resource group, and shared locals

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy = false
    }
  }
}

# -----------------------------------------------------------------------------
# Resource Group
# -----------------------------------------------------------------------------

resource "azurerm_resource_group" "main" {
  name     = "rg-${var.prefix}"
  location = var.location
  tags     = local.effective_tags
}

# -----------------------------------------------------------------------------
# Shared Locals
# -----------------------------------------------------------------------------

resource "random_string" "unique" {
  length  = 13
  lower   = true
  upper   = false
  numeric = true
  special = false
}

resource "random_uuid" "api_key" {
  count = var.api_key == "" ? 1 : 0
}

resource "random_password" "jwt_secret" {
  count   = var.jwt_secret == "" ? 1 : 0
  length  = 64
  special = false
}

# MCP service key — SH_ENTERPRISE_BICEP_HARDENING §Rule 4.
# Unconditional random_uuid so `local.mcp_service_key` can reference .result without `[0]`.
# When var.mcp_service_key is set (idempotent rerun from KV), random_uuid.result is ignored.
resource "random_uuid" "mcp_service_key" {}

locals {
  unique_suffix        = random_string.unique.result
  sql_server_name      = "${var.prefix}-sql-${local.unique_suffix}"
  storage_prefix       = lower(substr(replace(var.prefix, "-", ""), 0, 8))
  storage_account_name = lower("${local.storage_prefix}st${substr(local.unique_suffix, 0, 12)}")
  kv_prefix            = lower(substr(replace(var.prefix, "-", ""), 0, 8))
  key_vault_name       = "${local.kv_prefix}kv${substr(local.unique_suffix, 0, 8)}"
  # SH_ENTERPRISE_BICEP_HARDENING §Rule 4: mcp_service_key was deterministic per RG name —
  # now sourced from var.mcp_service_key (caller-supplied) or random_uuid (first-deploy default).
  mcp_service_key = var.mcp_service_key != "" ? var.mcp_service_key : random_uuid.mcp_service_key.result

  effective_api_key    = var.api_key != "" ? var.api_key : random_uuid.api_key[0].result
  effective_jwt_secret = var.jwt_secret != "" ? var.jwt_secret : random_password.jwt_secret[0].result

  create_openai                        = var.deploy_openai && var.existing_openai_resource_id == ""
  existing_openai_by_id_name           = var.existing_openai_resource_id != "" ? split("/", var.existing_openai_resource_id)[8] : ""
  existing_openai_by_id_resource_group = var.existing_openai_resource_id != "" ? split("/", var.existing_openai_resource_id)[4] : ""
  # MI-first auth: deployed/existing Azure OpenAI resources use the UAMI data-plane role.
  # Keep AzureOpenAI:ApiKey absent in that mode so AddSelfHostedOpenAI uses TokenCredential.
  openai_use_mi = local.create_openai || var.existing_openai_resource_id != "" || var.existing_openai_resource_name != ""

  # Effective endpoints — 3-tier resolution: deployed -> existing -> external
  effective_openai_endpoint = local.create_openai ? azurerm_cognitive_account.openai[0].endpoint : (
    var.existing_openai_resource_id != "" ? data.azurerm_cognitive_account.openai_existing_by_id[0].endpoint : (
      var.existing_openai_resource_name != "" ? data.azurerm_cognitive_account.openai_existing[0].endpoint : var.external_openai_endpoint
    )
  )
  effective_openai_key = local.openai_use_mi ? "" : var.external_openai_key
  effective_docintel_endpoint = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].endpoint : (
    var.existing_docintel_resource_name != "" ? data.azurerm_cognitive_account.docintel_existing[0].endpoint : var.external_docintel_endpoint
  )
  effective_docintel_key = var.deploy_document_intelligence ? azurerm_cognitive_account.docintel[0].primary_access_key : (
    var.existing_docintel_resource_name != "" ? data.azurerm_cognitive_account.docintel_existing[0].primary_access_key : var.external_docintel_key
  )
  effective_vision_endpoint = var.deploy_vision ? azurerm_cognitive_account.vision[0].endpoint : (
    var.existing_vision_resource_name != "" ? data.azurerm_cognitive_account.vision_existing[0].endpoint : var.external_vision_endpoint
  )
  effective_vision_key = var.deploy_vision ? azurerm_cognitive_account.vision[0].primary_access_key : (
    var.existing_vision_resource_name != "" ? data.azurerm_cognitive_account.vision_existing[0].primary_access_key : var.external_vision_key
  )

  # SQL connection string using AAD Managed Identity authentication (no password)
  sql_connection_string = "Server=tcp:${azurerm_mssql_server.main.fully_qualified_domain_name},1433;Initial Catalog=McpKnowledge;Authentication=Active Directory Managed Identity;User Id=${azurerm_user_assigned_identity.main.client_id};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

  # MI-swap landed (Builder B commit 3a690a4c): BlobServiceClient uses DefaultAzureCredential
  # + Storage:Azure:AccountUrl (no AccountKey). The connection-string local is RETIRED.
  # storage_connection_string = REMOVED — use azurerm_storage_account.main.primary_blob_endpoint instead.

  # Effective registry path — switches to customer ACR when external_acr_name provided.
  effective_registry_server = var.external_acr_name != "" ? "${var.external_acr_name}.azurecr.io" : "ghcr.io"
  registry_path             = "${local.effective_registry_server}/${var.image_repository_prefix}"

  default_tags = {
    project      = "knowz-selfhosted"
    environment  = var.prefix
    tier         = "enterprise"
    "managed-by" = "terraform"
  }
  effective_tags = merge(local.default_tags, var.tags)
}

# Current Azure client info (for tenant_id, etc.)
data "azurerm_client_config" "current" {}

# -----------------------------------------------------------------------------
# Data sources for existing AI resources
# -----------------------------------------------------------------------------
# Look up pre-existing Cognitive / AIServices resources to reuse (tier-2 mode).
# Enabled when deploy_* = false AND existing_*_resource_name is set.

data "azurerm_cognitive_account" "openai_existing" {
  count               = (!local.create_openai && var.existing_openai_resource_id == "" && var.existing_openai_resource_name != "") ? 1 : 0
  name                = var.existing_openai_resource_name
  resource_group_name = var.existing_openai_resource_group
}

data "azurerm_cognitive_account" "openai_existing_by_id" {
  count               = var.existing_openai_resource_id != "" ? 1 : 0
  name                = local.existing_openai_by_id_name
  resource_group_name = local.existing_openai_by_id_resource_group
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

# -----------------------------------------------------------------------------
# AI service configuration validation
# -----------------------------------------------------------------------------
# Ensures at least one credential source is configured per AI service.
# Check blocks emit warnings rather than hard errors (unlike variable validation).

check "openai_credentials" {
  assert {
    condition     = local.create_openai || var.existing_openai_resource_id != "" || var.existing_openai_resource_name != "" || (var.external_openai_endpoint != "" && var.external_openai_key != "")
    error_message = "When deploy_openai=false, provide existing_openai_resource_id, existing_openai_resource_name+resource_group, OR external_openai_endpoint+key."
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
    condition     = var.deploy_document_intelligence || var.existing_docintel_resource_name != "" || (var.external_docintel_endpoint != "" && var.external_docintel_key != "")
    error_message = "When deploy_document_intelligence=false, provide either existing_docintel_resource_name+resource_group OR external_docintel_endpoint+key."
  }
}
