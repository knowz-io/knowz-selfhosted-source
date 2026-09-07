# frontdoor.tf — Front Door Premium + WAF policy + origin groups + routes

# -----------------------------------------------------------------------------
# WAF Policy (DRS 2.1 + Bot Manager — Prevention mode at enterprise tier)
# SH_ENTERPRISE_BICEP_HARDENING §Rule 1. Prevention blocks attacks (not just logs).
# Customers can flip to Detection during incident triage via var.waf_mode.
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_firewall_policy" "main" {
  name                       = "${replace(var.prefix, "-", "")}waf"
  resource_group_name        = azurerm_resource_group.main.name
  sku_name                   = "Premium_AzureFrontDoor"
  enabled                    = true
  mode                       = var.waf_mode
  request_body_check_enabled = true
  tags                       = local.effective_tags

  managed_rule {
    type    = "Microsoft_DefaultRuleSet"
    version = "2.1"
    action  = "Block"
  }

  managed_rule {
    type    = "Microsoft_BotManagerRuleSet"
    version = "1.0"
    action  = "Block"
  }
}

# -----------------------------------------------------------------------------
# Front Door Profile (Premium)
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_profile" "main" {
  name                     = "${var.prefix}-fd"
  resource_group_name      = azurerm_resource_group.main.name
  sku_name                 = "Premium_AzureFrontDoor"
  response_timeout_seconds = 60
  tags                     = local.effective_tags
}

# -----------------------------------------------------------------------------
# Front Door Endpoint
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_endpoint" "main" {
  name                     = "${var.prefix}-endpoint"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id
  enabled                  = true
  tags                     = local.effective_tags
}

# -----------------------------------------------------------------------------
# Origin Group: Web (default, serves / paths)
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_origin_group" "web" {
  name                     = "container-apps"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id
  session_affinity_enabled = false

  load_balancing {
    sample_size                        = 4
    successful_samples_required        = 3
    additional_latency_in_milliseconds = 50
  }

  health_probe {
    path                = "/healthz"
    request_type        = "HEAD"
    protocol            = "Https"
    interval_in_seconds = 30
  }
}

resource "azurerm_cdn_frontdoor_origin" "web" {
  name                           = "web-origin"
  cdn_frontdoor_origin_group_id  = azurerm_cdn_frontdoor_origin_group.web.id
  enabled                        = true
  host_name                      = azurerm_container_app.web.ingress[0].fqdn
  http_port                      = 80
  https_port                     = 443
  origin_host_header             = azurerm_container_app.web.ingress[0].fqdn
  priority                       = 1
  weight                         = 1000
  certificate_name_check_enabled = true

  private_link {
    location               = azurerm_resource_group.main.location
    private_link_target_id = azurerm_container_app_environment.main.id
    target_type            = "managedEnvironments"
    request_message        = "Front Door private link to Container Apps"
  }
}

# -----------------------------------------------------------------------------
# Origin Group: API
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_origin_group" "api" {
  name                     = "api-apps"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id
  session_affinity_enabled = false

  load_balancing {
    sample_size                        = 4
    successful_samples_required        = 3
    additional_latency_in_milliseconds = 50
  }

  health_probe {
    path                = "/health"
    request_type        = "HEAD"
    protocol            = "Https"
    interval_in_seconds = 30
  }
}

resource "azurerm_cdn_frontdoor_origin" "api" {
  name                           = "api-origin"
  cdn_frontdoor_origin_group_id  = azurerm_cdn_frontdoor_origin_group.api.id
  enabled                        = true
  host_name                      = azurerm_container_app.api.ingress[0].fqdn
  http_port                      = 80
  https_port                     = 443
  origin_host_header             = azurerm_container_app.api.ingress[0].fqdn
  priority                       = 1
  weight                         = 1000
  certificate_name_check_enabled = true

  private_link {
    location               = azurerm_resource_group.main.location
    private_link_target_id = azurerm_container_app_environment.main.id
    target_type            = "managedEnvironments"
    request_message        = "Front Door private link to Container Apps API"
  }
}

# -----------------------------------------------------------------------------
# Origin Group: MCP
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_origin_group" "mcp" {
  name                     = "mcp-origin-group"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id
  session_affinity_enabled = false

  load_balancing {
    sample_size                        = 4
    successful_samples_required        = 3
    additional_latency_in_milliseconds = 50
  }

  health_probe {
    path                = "/health"
    request_type        = "HEAD"
    protocol            = "Https"
    interval_in_seconds = 30
  }
}

# NOTE: Private link connections from Front Door to Container Apps require manual approval
# after deployment. Use: az network private-endpoint-connection approve --id <connection-id>
# This applies to ALL origins using private_link (web, API, MCP).
resource "azurerm_cdn_frontdoor_origin" "mcp" {
  name                           = "mcp-origin"
  cdn_frontdoor_origin_group_id  = azurerm_cdn_frontdoor_origin_group.mcp.id
  enabled                        = true
  host_name                      = azurerm_container_app.mcp.ingress[0].fqdn
  http_port                      = 80
  https_port                     = 443
  origin_host_header             = azurerm_container_app.mcp.ingress[0].fqdn
  priority                       = 1
  weight                         = 1000
  certificate_name_check_enabled = true

  private_link {
    location               = azurerm_resource_group.main.location
    private_link_target_id = azurerm_container_app_environment.main.id
    target_type            = "managedEnvironments"
    request_message        = "Front Door private link to Container Apps MCP"
  }
}

# -----------------------------------------------------------------------------
# Security Policy: WAF attached to endpoint
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_security_policy" "main" {
  name                     = "waf-policy"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.main.id

  security_policies {
    firewall {
      cdn_frontdoor_firewall_policy_id = azurerm_cdn_frontdoor_firewall_policy.main.id

      association {
        domain {
          cdn_frontdoor_domain_id = azurerm_cdn_frontdoor_endpoint.main.id
        }
        patterns_to_match = ["/*"]
      }
    }
  }
}

# -----------------------------------------------------------------------------
# Route: /api/*, /health, /swagger/* -> API origin group
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_route" "api" {
  name                          = "api-route"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.main.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.api.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.api.id]
  supported_protocols           = ["Https"]
  patterns_to_match             = ["/api/*", "/health", "/swagger/*"]
  forwarding_protocol           = "HttpsOnly"
  https_redirect_enabled        = true
  link_to_default_domain        = true
  enabled                       = true
}

# -----------------------------------------------------------------------------
# Route: /sse/*, /message/*, /mcp/* -> MCP origin group
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_route" "mcp" {
  name                          = "mcp-route"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.main.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.mcp.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.mcp.id]
  supported_protocols           = ["Https"]
  patterns_to_match             = ["/sse/*", "/message/*", "/mcp/*"]
  forwarding_protocol           = "HttpsOnly"
  https_redirect_enabled        = true
  link_to_default_domain        = true
  enabled                       = true
}

# -----------------------------------------------------------------------------
# Route: /* -> Web origin group (catch-all, lowest priority)
# -----------------------------------------------------------------------------

resource "azurerm_cdn_frontdoor_route" "web" {
  name                          = "web-route"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.main.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.web.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.web.id]
  supported_protocols           = ["Https"]
  patterns_to_match             = ["/*"]
  forwarding_protocol           = "HttpsOnly"
  https_redirect_enabled        = true
  link_to_default_domain        = true
  enabled                       = true

  depends_on = [
    azurerm_cdn_frontdoor_route.api,
    azurerm_cdn_frontdoor_route.mcp,
  ]
}
