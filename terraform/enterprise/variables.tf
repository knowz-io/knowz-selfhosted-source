# variables.tf — All input variables for enterprise self-hosted deployment

# -----------------------------------------------------------------------------
# Core
# -----------------------------------------------------------------------------

variable "prefix" {
  description = "Resource name prefix (2-8 chars, used for all resource naming)"
  type        = string

  validation {
    condition     = length(var.prefix) >= 2 && length(var.prefix) <= 8
    error_message = "Prefix must be between 2 and 8 characters."
  }
}

variable "location" {
  description = "Azure region for all resources"
  type        = string
}

variable "tags" {
  description = "Tags applied to all resources (merged with default tags)"
  type        = map(string)
  default     = {}
}

# -----------------------------------------------------------------------------
# AAD / SQL Administration
# -----------------------------------------------------------------------------

variable "aad_admin_object_id" {
  description = "AAD Object ID for SQL administrator (user or group)"
  type        = string
}

variable "aad_admin_display_name" {
  description = "AAD display name for SQL administrator"
  type        = string
}

# -----------------------------------------------------------------------------
# Application Secrets
# -----------------------------------------------------------------------------

variable "admin_password" {
  description = "SuperAdmin password for Knowz application initial setup"
  type        = string
  sensitive   = true
}

variable "api_key" {
  description = "API key for the selfhosted API. Auto-generated if empty."
  type        = string
  sensitive   = true
  default     = ""
}

variable "jwt_secret" {
  description = "JWT signing secret (min 32 chars). Auto-generated if empty."
  type        = string
  sensitive   = true
  default     = ""
}

# -----------------------------------------------------------------------------
# Azure OpenAI
# -----------------------------------------------------------------------------

variable "deploy_openai" {
  description = "Deploy Azure OpenAI (false = use external OpenAI endpoint)"
  type        = bool
  default     = true
}

variable "external_openai_endpoint" {
  description = "External OpenAI endpoint (required when deploy_openai is false)"
  type        = string
  default     = ""
}

variable "external_openai_key" {
  description = "External OpenAI API key (required when deploy_openai is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- Existing Resource Lookup (OpenAI) ---

variable "existing_openai_resource_name" {
  description = "Name of an existing Azure OpenAI/AIServices resource to reuse (alternative to deploying new). Leave empty to deploy new or use external endpoint."
  type        = string
  default     = ""
}

variable "existing_openai_resource_group" {
  description = "Resource group of the existing OpenAI resource (required if existing_openai_resource_name is set)"
  type        = string
  default     = ""
}

variable "chat_deployment_name" {
  description = "Chat model deployment name (must match appsettings DeploymentName)"
  type        = string
  default     = "gpt-5.4"
}

variable "embedding_deployment_name" {
  description = "Embedding deployment name (must match appsettings EmbeddingDeploymentName)"
  type        = string
  default     = "text-embedding-3-large"
}

variable "embedding_model_name" {
  description = "Embedding model name"
  type        = string
  default     = "text-embedding-3-large"
}

variable "embedding_dimensions" {
  description = "Embedding vector dimensions — MUST match the deployed model (1536 for -3-small / ada-002, 3072 for -3-large). Propagated to Container App as Embedding__Dimensions. See ARCH_EmbeddingConfigOwnership."
  type        = number
  default     = 3072
  validation {
    condition     = var.embedding_dimensions > 0
    error_message = "embedding_dimensions must be a positive integer matching the embedding model output size."
  }
}

# -----------------------------------------------------------------------------
# Azure AI Vision
# -----------------------------------------------------------------------------

variable "deploy_vision" {
  description = "Deploy Azure AI Vision for image/diagram analysis (caption, tags, objects, OCR)"
  type        = bool
  default     = true
}

variable "external_vision_endpoint" {
  description = "External Azure AI Vision endpoint (required when deploy_vision is false)"
  type        = string
  default     = ""
}

variable "external_vision_key" {
  description = "External Azure AI Vision API key (required when deploy_vision is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- Existing Resource Lookup (Vision) ---

variable "existing_vision_resource_name" {
  description = "Name of an existing Azure AI Vision resource to reuse (alternative to deploying new). Leave empty to deploy new or use external endpoint."
  type        = string
  default     = ""
}

variable "existing_vision_resource_group" {
  description = "Resource group of the existing Vision resource (required if existing_vision_resource_name is set)"
  type        = string
  default     = ""
}

# -----------------------------------------------------------------------------
# Document Intelligence
# -----------------------------------------------------------------------------

variable "deploy_document_intelligence" {
  description = "Deploy Azure Document Intelligence for advanced document extraction"
  type        = bool
  default     = true
}

variable "external_docintel_endpoint" {
  description = "External Document Intelligence endpoint (required when deploy_document_intelligence is false)"
  type        = string
  default     = ""
}

variable "external_docintel_key" {
  description = "External Document Intelligence API key (required when deploy_document_intelligence is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- Existing Resource Lookup (Document Intelligence) ---

variable "existing_docintel_resource_name" {
  description = "Name of an existing Azure Document Intelligence resource to reuse (alternative to deploying new). Leave empty to deploy new or use external endpoint."
  type        = string
  default     = ""
}

variable "existing_docintel_resource_group" {
  description = "Resource group of the existing Document Intelligence resource (required if existing_docintel_resource_name is set)"
  type        = string
  default     = ""
}

# -----------------------------------------------------------------------------
# Azure AI Search
# -----------------------------------------------------------------------------

variable "search_sku" {
  description = "Azure AI Search SKU (enterprise requires standard or higher for private endpoint support)"
  type        = string
  default     = "standard"

  validation {
    condition     = contains(["standard", "standard2", "standard3"], var.search_sku)
    error_message = "Enterprise search SKU must be standard, standard2, or standard3 (private endpoints require standard+)."
  }
}

# -----------------------------------------------------------------------------
# Container Apps
# -----------------------------------------------------------------------------

variable "image_tag" {
  description = "Container image tag (e.g., latest, v1.0.0)"
  type        = string
  default     = "latest"
}

variable "registry_server" {
  description = "Container registry server (GHCR)"
  type        = string
  default     = "ghcr.io"
}

variable "registry_username" {
  description = "Container registry username (empty = public GHCR)"
  type        = string
  default     = ""
}

variable "registry_password" {
  description = "Container registry password (empty = public GHCR)"
  type        = string
  sensitive   = true
  default     = ""
}

# -----------------------------------------------------------------------------
# BYO Infrastructure (optional — preserves back-compat when all empty)
# See SH_ENTERPRISE_BYO_INFRA.md for spec rationale.
# -----------------------------------------------------------------------------

variable "byo_vnet_subnet_id" {
  description = "BYO Container Apps delegated subnet resource ID. Empty = auto-provision VNet."
  type        = string
  default     = ""
}

variable "byo_vnet_pe_subnet_id" {
  description = "BYO non-delegated PE subnet resource ID. Empty AND pe_subnet_address_prefix empty = auto-provision inside BYO VNet."
  type        = string
  default     = ""
}

variable "pe_subnet_address_prefix" {
  description = "CIDR to auto-create PE subnet inside BYO VNet (used when byo_vnet_pe_subnet_id empty)."
  type        = string
  default     = ""
}

variable "auto_provision_vnet" {
  description = "Auto-provision VNet when BYO inputs absent. Set false for strict BYO mode with fail-fast asserts."
  type        = bool
  default     = true
}

variable "byo_key_vault_id" {
  description = "BYO Key Vault resource ID (full /subscriptions/.../Microsoft.KeyVault/vaults/<name>). Empty = create new per-env KV."
  type        = string
  default     = ""
}

variable "central_log_analytics_id" {
  description = "Central Log Analytics workspace resource ID. Empty = per-env LAW."
  type        = string
  default     = ""
}

variable "existing_openai_resource_id" {
  description = "Customer-provisioned Azure OpenAI resource ID. Empty = deploy local OpenAI."
  type        = string
  default     = ""
}

variable "external_acr_name" {
  description = "External ACR name (not FQDN) for air-gapped pulls. Empty = pull from ghcr.io."
  type        = string
  default     = ""
}

variable "external_acr_resource_group" {
  description = "External ACR resource group (required when external_acr_name non-empty)."
  type        = string
  default     = ""
}

# -----------------------------------------------------------------------------
# Enterprise Hardening (SH_ENTERPRISE_BICEP_HARDENING.md)
# -----------------------------------------------------------------------------

variable "waf_mode" {
  description = "WAF policy mode. Default Prevention for enterprise tier (blocks attacks instead of only logging)."
  type        = string
  default     = "Prevention"

  validation {
    condition     = contains(["Detection", "Prevention"], var.waf_mode)
    error_message = "waf_mode must be Detection or Prevention."
  }
}

variable "sql_database_sku_name" {
  description = "SQL database SKU name. Default S1 for enterprise workload."
  type        = string
  default     = "S1"

  validation {
    condition     = contains(["Basic", "S0", "S1", "S2", "S3", "P1", "P2"], var.sql_database_sku_name)
    error_message = "sql_database_sku_name must be one of: Basic, S0, S1, S2, S3, P1, P2."
  }
}

variable "sql_database_max_size_bytes" {
  description = "SQL database max size in bytes. Default 250GB for S1 tier."
  type        = number
  default     = 268435456000
}

variable "image_repository_prefix" {
  description = "Container image registry prefix (e.g., knowz-io for ghcr.io/knowz-io/*)."
  type        = string
  default     = "knowz-io"
}

variable "strict_ingestion" {
  description = "Enforce strict ingestion PE (AMPLS) for App Insights. Default false — AMPLS deferred."
  type        = bool
  default     = false
}

variable "mcp_service_key" {
  description = "MCP service key. Empty = auto-generate (random_uuid) on first apply; pass from KV on reruns for idempotency."
  type        = string
  sensitive   = true
  default     = ""
}
