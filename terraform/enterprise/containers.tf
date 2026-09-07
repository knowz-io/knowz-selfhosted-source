# containers.tf — Container Apps Environment (VNet-injected, internal) + API/Web/MCP apps

# -----------------------------------------------------------------------------
# Container Apps Environment (VNet-injected, internal only)
# -----------------------------------------------------------------------------

resource "azurerm_container_app_environment" "main" {
  name                           = "${var.prefix}-cae"
  location                       = azurerm_resource_group.main.location
  resource_group_name            = azurerm_resource_group.main.name
  log_analytics_workspace_id     = local.effective_log_analytics_workspace_id
  infrastructure_subnet_id       = local.container_apps_subnet_id
  internal_load_balancer_enabled = true
  zone_redundancy_enabled        = false
  tags                           = local.effective_tags
}

# -----------------------------------------------------------------------------
# API Container App
# -----------------------------------------------------------------------------

resource "azurerm_container_app" "api" {
  name                         = "${var.prefix}-api"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.effective_tags

  depends_on = [
    azurerm_key_vault_secret.sql_connection,
    azurerm_key_vault_secret.openai_endpoint,
    azurerm_key_vault_secret.openai_key,
    azurerm_key_vault_secret.search_endpoint,
    azurerm_key_vault_secret.search_key,
    azurerm_key_vault_secret.storage_account_url,
    azurerm_key_vault_secret.api_key,
    azurerm_key_vault_secret.jwt_secret,
    azurerm_key_vault_secret.admin_password,
    azurerm_key_vault_secret.docintel_endpoint,
    azurerm_key_vault_secret.docintel_key,
    azurerm_key_vault_secret.vision_endpoint,
    azurerm_key_vault_secret.vision_key,
    azurerm_key_vault_secret.appinsights_connection,
  ]

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.main.id]
  }

  dynamic "registry" {
    for_each = var.external_acr_name != "" ? [1] : []
    content {
      server   = local.effective_registry_server
      identity = azurerm_user_assigned_identity.main.id
    }
  }

  dynamic "registry" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      server               = local.effective_registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  # --- Secrets (Key Vault references via managed identity) ---

  dynamic "secret" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  secret {
    name                = "sql-connection"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/ConnectionStrings--McpDb"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "openai-endpoint"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureOpenAI--Endpoint"
    identity            = azurerm_user_assigned_identity.main.id
  }

  dynamic "secret" {
    for_each = local.openai_use_mi ? [] : [1]
    content {
      name                = "openai-apikey"
      key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureOpenAI--ApiKey"
      identity            = azurerm_user_assigned_identity.main.id
    }
  }

  secret {
    name                = "search-endpoint"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureAISearch--Endpoint"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "search-apikey"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureAISearch--ApiKey"
    identity            = azurerm_user_assigned_identity.main.id
  }

  # MI-swap: AccountUrl + DefaultAzureCredential replaces the connection-string flow.
  secret {
    name                = "storage-account-url"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/Storage--Azure--AccountUrl"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "selfhosted-apikey"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/SelfHosted--ApiKey"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "selfhosted-jwtsecret"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/SelfHosted--JwtSecret"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "selfhosted-adminpassword"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/SelfHosted--SuperAdminPassword"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "docintel-endpoint"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureDocumentIntelligence--Endpoint"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "docintel-apikey"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureDocumentIntelligence--ApiKey"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "vision-endpoint"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureAIVision--Endpoint"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "vision-apikey"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/AzureAIVision--ApiKey"
    identity            = azurerm_user_assigned_identity.main.id
  }

  secret {
    name                = "appinsights-connection"
    key_vault_secret_id = "${local.effective_key_vault_uri}secrets/ApplicationInsights--ConnectionString"
    identity            = azurerm_user_assigned_identity.main.id
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "api"
      image  = "${local.registry_path}/knowz-selfhosted-api:${var.image_tag}"
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
      dynamic "env" {
        for_each = local.openai_use_mi ? [] : [1]
        content {
          name        = "AzureOpenAI__ApiKey"
          secret_name = "openai-apikey"
        }
      }
      env {
        name  = "AzureOpenAI__DeploymentName"
        value = var.chat_deployment_name
      }
      env {
        name  = "AzureOpenAI__EmbeddingDeploymentName"
        value = var.embedding_deployment_name
      }
      env {
        name  = "Embedding__ModelName"
        value = var.embedding_model_name
      }
      env {
        name  = "Embedding__Dimensions"
        value = tostring(var.embedding_dimensions)
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
        # MI-swap: app reads AccountUrl + uses DefaultAzureCredential.
        name        = "Storage__Azure__AccountUrl"
        secret_name = "storage-account-url"
      }
      env {
        name  = "Storage__Azure__ContainerName"
        value = "selfhosted-files"
      }
      env {
        # Data Protection key ring (Builder A commit 4b06cb3e4) reads this.
        name        = "Storage__AzureBlob__AccountUrl"
        secret_name = "storage-account-url"
      }
      env {
        name  = "AzureKeyVault__VaultUri"
        value = local.effective_key_vault_uri
      }
      env {
        name  = "AzureKeyVault__DataProtectionKeyName"
        value = "selfhosted-dp-key"
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
        name        = "AzureDocumentIntelligence__Endpoint"
        secret_name = "docintel-endpoint"
      }
      env {
        name        = "AzureDocumentIntelligence__ApiKey"
        secret_name = "docintel-apikey"
      }
      env {
        name        = "AzureAIVision__Endpoint"
        secret_name = "vision-endpoint"
      }
      env {
        name        = "AzureAIVision__ApiKey"
        secret_name = "vision-apikey"
      }
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.main.client_id
      }
      env {
        name        = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        secret_name = "appinsights-connection"
      }
    }
  }
}

# -----------------------------------------------------------------------------
# MCP Container App
# -----------------------------------------------------------------------------

resource "azurerm_container_app" "mcp" {
  name                         = "${var.prefix}-mcp"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.effective_tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.main.id]
  }

  dynamic "registry" {
    for_each = var.external_acr_name != "" ? [1] : []
    content {
      server   = local.effective_registry_server
      identity = azurerm_user_assigned_identity.main.id
    }
  }

  dynamic "registry" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      server               = local.effective_registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  dynamic "secret" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "mcp"
      image  = "${local.registry_path}/knowz-selfhosted-mcp:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "Knowz__BaseUrl"
        value = "https://${azurerm_container_app.api.ingress[0].fqdn}"
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

# -----------------------------------------------------------------------------
# Web Container App
# -----------------------------------------------------------------------------

resource "azurerm_container_app" "web" {
  name                         = "${var.prefix}-web"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  tags                         = local.effective_tags

  dynamic "registry" {
    for_each = var.external_acr_name != "" ? [1] : []
    content {
      server   = local.effective_registry_server
      identity = azurerm_user_assigned_identity.main.id
    }
  }

  dynamic "registry" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      server               = local.effective_registry_server
      username             = var.registry_username
      password_secret_name = "registry-password"
    }
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.main.id]
  }

  dynamic "secret" {
    for_each = (var.external_acr_name == "" && var.registry_username != "") ? [1] : []
    content {
      name  = "registry-password"
      value = var.registry_password
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "http"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "web"
      image  = "${local.registry_path}/knowz-selfhosted-web:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "API_UPSTREAM"
        value = azurerm_container_app.api.ingress[0].fqdn
      }
      env {
        name  = "API_PROTOCOL"
        value = "https"
      }
    }
  }
}
