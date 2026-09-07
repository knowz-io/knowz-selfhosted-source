# =============================================================================
# CONTAINER APPS ENVIRONMENT + API / MCP / WEB (conditional)
# =============================================================================

# --- Container Apps Environment ---

resource "azurerm_container_app_environment" "main" {
  count = var.deploy_container_apps ? 1 : 0

  name                       = "${var.prefix}-cae"
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  log_analytics_workspace_id = var.deploy_monitoring ? azurerm_log_analytics_workspace.main[0].id : null

  tags = local.tags
}

# =============================================================================
# API Container App
# =============================================================================

resource "azurerm_container_app" "api" {
  count = var.deploy_container_apps ? 1 : 0

  name                         = "${var.prefix}-api"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main[0].id
  revision_mode                = "Single"

  tags = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.main.id]
  }

  dynamic "registry" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      server               = var.registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  # --- Secrets ---
  dynamic "secret" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  secret {
    name  = "sql-connection"
    value = local.sql_connection_string
  }

  secret {
    name  = "openai-endpoint"
    value = local.effective_openai_endpoint
  }

  secret {
    name  = "openai-apikey"
    value = local.effective_openai_key
  }

  secret {
    name  = "search-endpoint"
    value = "https://${azurerm_search_service.main.name}.search.windows.net"
  }

  secret {
    name  = "search-apikey"
    value = azurerm_search_service.main.primary_key
  }

  secret {
    name  = "storage-connection"
    value = local.storage_connection_string
  }

  secret {
    name  = "selfhosted-apikey"
    value = local.api_key
  }

  secret {
    name  = "selfhosted-jwtsecret"
    value = local.jwt_secret
  }

  secret {
    name  = "selfhosted-adminpassword"
    value = var.admin_password
  }

  # --- Ingress ---
  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  # --- Template ---
  template {
    min_replicas = 0
    max_replicas = 3

    container {
      name   = "api"
      image  = "${var.registry_server}/knowz-io/knowz-selfhosted-api:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__McpDb"
        secret_name = "sql-connection"
      }

      env {
        name        = "AzureOpenAI__Endpoint"
        secret_name = "openai-endpoint"
      }

      env {
        name        = "AzureOpenAI__ApiKey"
        secret_name = "openai-apikey"
      }

      env {
        name  = "AzureOpenAI__DeploymentName"
        value = local.effective_ca_deployment_name
      }

      env {
        name  = "AzureOpenAI__EmbeddingDeploymentName"
        value = var.ca_embedding_deployment_name
      }

      env {
        name  = "Embedding__ModelName"
        value = local.effective_ca_embedding_model_name
      }

      env {
        name  = "Embedding__Dimensions"
        value = tostring(local.effective_ca_embedding_dimensions)
      }

      env {
        name        = "AzureAISearch__Endpoint"
        secret_name = "search-endpoint"
      }

      env {
        name        = "AzureAISearch__ApiKey"
        secret_name = "search-apikey"
      }

      env {
        name  = "AzureAISearch__IndexName"
        value = "knowledge"
      }

      env {
        name  = "Storage__Provider"
        value = "AzureBlob"
      }

      env {
        name        = "Storage__Azure__ConnectionString"
        secret_name = "storage-connection"
      }

      env {
        name  = "Storage__Azure__ContainerName"
        value = "selfhosted-files"
      }

      env {
        name        = "SelfHosted__ApiKey"
        secret_name = "selfhosted-apikey"
      }

      env {
        name        = "SelfHosted__JwtSecret"
        secret_name = "selfhosted-jwtsecret"
      }

      env {
        name        = "SelfHosted__SuperAdminPassword"
        secret_name = "selfhosted-adminpassword"
      }

      env {
        name  = "Database__AutoMigrate"
        value = "true"
      }

      env {
        name  = "MCP__ServiceKey"
        value = local.mcp_service_key
      }

      env {
        name  = "AzureDocumentIntelligence__Endpoint"
        value = local.effective_doc_intel_endpoint
      }

      env {
        name  = "AzureDocumentIntelligence__ApiKey"
        value = local.effective_doc_intel_key
      }

      env {
        name  = "AzureAIVision__Endpoint"
        value = local.effective_vision_endpoint
      }

      env {
        name  = "AzureAIVision__ApiKey"
        value = local.effective_vision_key
      }
    }
  }
}

# =============================================================================
# MCP Container App
# =============================================================================

resource "azurerm_container_app" "mcp" {
  count = var.deploy_container_apps ? 1 : 0

  name                         = "${var.prefix}-mcp"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main[0].id
  revision_mode                = "Single"

  tags = local.tags

  dynamic "registry" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      server               = var.registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  dynamic "secret" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  # --- Ingress ---
  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  # --- Template ---
  template {
    min_replicas = 0
    max_replicas = 2

    container {
      name   = "mcp"
      image  = "${var.registry_server}/knowz-io/knowz-selfhosted-mcp:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "Knowz__BaseUrl"
        value = "https://${azurerm_container_app.api[0].ingress[0].fqdn}"
      }

      env {
        name  = "MCP__BackendMode"
        value = "selfhosted"
      }

      env {
        name  = "MCP__ApiKeyValidationEndpoint"
        value = "/api/vaults"
      }

      env {
        name  = "Authentication__ValidateApiKey"
        value = "true"
      }

      env {
        name  = "MCP__ServiceKey"
        value = local.mcp_service_key
      }
    }
  }
}

# =============================================================================
# Web Container App
# =============================================================================

resource "azurerm_container_app" "web" {
  count = var.deploy_container_apps ? 1 : 0

  name                         = "${var.prefix}-web"
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main[0].id
  revision_mode                = "Single"

  tags = local.tags

  dynamic "registry" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      server               = var.registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  dynamic "secret" {
    for_each = var.registry_username != "" ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  # --- Ingress ---
  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  # --- Template ---
  template {
    min_replicas = 0
    max_replicas = 2

    container {
      name   = "web"
      image  = "${var.registry_server}/knowz-io/knowz-selfhosted-web:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "API_UPSTREAM"
        value = azurerm_container_app.api[0].ingress[0].fqdn
      }

      env {
        name  = "API_PROTOCOL"
        value = "https"
      }
    }
  }
}
