# =============================================================================
# GENERAL
# =============================================================================

variable "prefix" {
  description = "Resource name prefix (used for all resource naming)"
  type        = string
  default     = "sh-test"
}

variable "location" {
  description = "Location for all resources"
  type        = string
  default     = "eastus2"
}

variable "resource_group_name" {
  description = "Name of the resource group to create"
  type        = string
  default     = "rg-knowz-selfhosted"
}

variable "tags" {
  description = "Additional tags to apply to all resources (merged with default tags)"
  type        = map(string)
  default     = {}
}

# =============================================================================
# SQL
# =============================================================================

variable "sql_admin_username" {
  description = "SQL Server administrator username"
  type        = string
  default     = "sqladmin"
  sensitive   = true
}

variable "sql_admin_password" {
  description = "SQL Server administrator password"
  type        = string
  sensitive   = true
}

variable "sql_public_network_access_enabled" {
  description = "Whether the SQL Server public endpoint is enabled. The standard tier has no VNet or private endpoint, so the Container Apps depend on the public endpoint (gated by the AllowAllAzureIps firewall rule) — set to false ONLY if you provide your own private connectivity, or use the enterprise tier which is private by default."
  type        = bool
  default     = true
}

variable "sql_allowed_cidrs" {
  description = "IPv4 CIDRs granted SQL Server firewall access (e.g. [\"203.0.113.7/32\"]) for local dev, CI/CD, or EF migrations. Default is empty: no public access beyond Azure services. Use /32 for single IPs. Internet-wide ranges are rejected."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for c in var.sql_allowed_cidrs : can(cidrhost(c, 0))])
    error_message = "Each sql_allowed_cidrs entry must be a valid IPv4 CIDR in prefix notation, e.g. \"203.0.113.0/24\" or \"203.0.113.7/32\"."
  }

  validation {
    condition     = alltrue([for c in var.sql_allowed_cidrs : tonumber(split("/", c)[1]) >= 8 if can(tonumber(split("/", c)[1]))])
    error_message = "sql_allowed_cidrs must be scoped ranges (prefix length >= 8). Internet-wide access such as 0.0.0.0/0 is not permitted."
  }
}

# =============================================================================
# AZURE OPENAI
# =============================================================================

variable "deploy_openai" {
  description = "Deploy Azure OpenAI (set to false when using external/shared OpenAI)"
  type        = bool
  default     = true
}

variable "external_openai_endpoint" {
  description = "External OpenAI endpoint (required if deploy_openai is false)"
  type        = string
  default     = ""
}

variable "external_openai_key" {
  description = "External OpenAI API key (required if deploy_openai is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- EXISTING RESOURCE LOOKUP (OpenAI) ---

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
  description = "Embedding model name (text-embedding-3-large or text-embedding-3-small)"
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

# =============================================================================
# AZURE AI VISION
# =============================================================================

variable "deploy_vision" {
  description = "Deploy Azure AI Vision for image/diagram analysis"
  type        = bool
  default     = true
}

variable "external_vision_endpoint" {
  description = "External Azure AI Vision endpoint (required if deploy_vision is false)"
  type        = string
  default     = ""
}

variable "external_vision_key" {
  description = "External Azure AI Vision API key (required if deploy_vision is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- EXISTING RESOURCE LOOKUP (Vision) ---

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

# =============================================================================
# DOCUMENT INTELLIGENCE
# =============================================================================

variable "deploy_document_intelligence" {
  description = "Deploy Azure Document Intelligence for advanced document extraction"
  type        = bool
  default     = true
}

variable "external_doc_intel_endpoint" {
  description = "External Document Intelligence endpoint (required if deploy_document_intelligence is false)"
  type        = string
  default     = ""
}

variable "external_doc_intel_key" {
  description = "External Document Intelligence API key (required if deploy_document_intelligence is false)"
  type        = string
  sensitive   = true
  default     = ""
}

# --- EXISTING RESOURCE LOOKUP (Document Intelligence) ---

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

# =============================================================================
# AI SEARCH
# =============================================================================

variable "search_sku" {
  description = "Azure AI Search SKU"
  type        = string
  default     = "basic"

  validation {
    condition     = contains(["free", "basic", "standard"], var.search_sku)
    error_message = "search_sku must be one of: free, basic, standard."
  }
}

variable "search_location" {
  description = "Location for AI Search (override if SKU unavailable in primary location). Uses var.location if empty."
  type        = string
  default     = ""
}

# =============================================================================
# KEY VAULT
# =============================================================================

variable "deploy_key_vault" {
  description = "Deploy Azure Key Vault for secret management (set to false for flat env var config)"
  type        = bool
  default     = true
}

# =============================================================================
# MONITORING
# =============================================================================

variable "deploy_monitoring" {
  description = "Deploy Log Analytics + Application Insights for monitoring"
  type        = bool
  default     = true
}

# =============================================================================
# STORAGE
# =============================================================================

variable "storage_allow_shared_key_access" {
  description = "Allow shared key access on storage account (set to false after migrating to Managed Identity)"
  type        = bool
  default     = true
}

# =============================================================================
# CONTAINER APPS
# =============================================================================

variable "deploy_container_apps" {
  description = "Deploy Container Apps for API, MCP, and Web"
  type        = bool
  default     = false
}

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
  description = "Container registry username (only needed for private GHCR images)"
  type        = string
  default     = ""
}

variable "registry_password" {
  description = "Container registry password (only needed for private GHCR images)"
  type        = string
  sensitive   = true
  default     = ""
}

variable "api_key" {
  description = "API key for selfhosted authentication (auto-generated if empty)"
  type        = string
  sensitive   = true
  default     = ""
}

variable "jwt_secret" {
  description = "JWT secret for selfhosted token signing (auto-generated if empty)"
  type        = string
  sensitive   = true
  default     = ""
}

variable "admin_password" {
  description = "SuperAdmin password for initial setup. Required — no default (fail-closed)."
  type        = string
  sensitive   = true
}

variable "ca_deployment_name" {
  description = "Chat model deployment name for Container Apps config (defaults to chat_deployment_name)"
  type        = string
  default     = ""
}

variable "ca_embedding_deployment_name" {
  description = "Embedding deployment name for Container Apps config"
  type        = string
  default     = "text-embedding-3-large"
}

variable "ca_embedding_model_name" {
  description = "Embedding model name for Container Apps config (Embedding__ModelName). Defaults to embedding_model_name."
  type        = string
  default     = ""
}

variable "ca_embedding_dimensions" {
  description = "Embedding vector dimensions for Container Apps config (Embedding__Dimensions). 0 = inherit embedding_dimensions."
  type        = number
  default     = 0
}
