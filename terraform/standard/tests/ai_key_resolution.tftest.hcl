# =============================================================================
# Tests for 3-tier AI key resolution (standard module)
# =============================================================================
# Validates: new variables, data sources, 3-tier locals, check blocks, output
#
# Tier 1: deploy_* = true  -> use deployed resource
# Tier 2: deploy_* = false + existing_*_resource_name set -> use data source
# Tier 3: deploy_* = false + external_* set -> use external endpoint/key
# =============================================================================

# --- Mock providers (no real Azure calls) ---

mock_provider "azurerm" {
  # Provide deterministic mock data for fields that require specific formats.
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id       = "00000000-0000-0000-0000-000000000000"
      subscription_id = "00000000-0000-0000-0000-000000000001"
      client_id       = "00000000-0000-0000-0000-000000000002"
      object_id       = "00000000-0000-0000-0000-000000000003"
    }
  }
}

mock_provider "random" {}

# =============================================================================
# Test: Variables exist with correct defaults
# =============================================================================

# Should_HaveEmptyDefault_WhenExistingOpenAIResourceNameDeclared
run "existing_openai_vars_default_empty" {
  command = plan

  variables {
    sql_admin_password = "test-password-123!"
  }

  # These variables should exist and default to ""
  assert {
    condition     = var.existing_openai_resource_name == ""
    error_message = "existing_openai_resource_name should default to empty string"
  }

  assert {
    condition     = var.existing_openai_resource_group == ""
    error_message = "existing_openai_resource_group should default to empty string"
  }
}

# Should_HaveEmptyDefault_WhenExistingVisionResourceNameDeclared
run "existing_vision_vars_default_empty" {
  command = plan

  variables {
    sql_admin_password = "test-password-123!"
  }

  assert {
    condition     = var.existing_vision_resource_name == ""
    error_message = "existing_vision_resource_name should default to empty string"
  }

  assert {
    condition     = var.existing_vision_resource_group == ""
    error_message = "existing_vision_resource_group should default to empty string"
  }
}

# Should_HaveEmptyDefault_WhenExistingDocIntelResourceNameDeclared
run "existing_docintel_vars_default_empty" {
  command = plan

  variables {
    sql_admin_password = "test-password-123!"
  }

  assert {
    condition     = var.existing_docintel_resource_name == ""
    error_message = "existing_docintel_resource_name should default to empty string"
  }

  assert {
    condition     = var.existing_docintel_resource_group == ""
    error_message = "existing_docintel_resource_group should default to empty string"
  }
}

# =============================================================================
# Test: AI configuration summary output exists
# =============================================================================

# Should_OutputDeployedMode_WhenAllServicesDeployed
run "ai_config_summary_all_deployed" {
  command = plan

  variables {
    sql_admin_password           = "test-password-123!"
    deploy_openai                = true
    deploy_vision                = true
    deploy_document_intelligence = true
  }

  assert {
    condition     = output.ai_configuration_summary != null
    error_message = "ai_configuration_summary output must exist"
  }
}

# Should_OutputExternalMode_WhenExternalEndpointsProvided
run "ai_config_summary_all_external" {
  command = plan

  variables {
    sql_admin_password           = "test-password-123!"
    deploy_openai                = false
    deploy_vision                = false
    deploy_document_intelligence = false
    external_openai_endpoint     = "https://ext-openai.example.com"
    external_openai_key          = "ext-openai-key"
    external_vision_endpoint     = "https://ext-vision.example.com"
    external_vision_key          = "ext-vision-key"
    external_doc_intel_endpoint  = "https://ext-docintel.example.com"
    external_doc_intel_key       = "ext-docintel-key"
  }

  assert {
    condition     = output.ai_configuration_summary.openai == "external"
    error_message = "OpenAI should report 'external' mode when using external endpoint"
  }

  assert {
    condition     = output.ai_configuration_summary.vision == "external"
    error_message = "Vision should report 'external' mode when using external endpoint"
  }

  assert {
    condition     = output.ai_configuration_summary.docintel == "external"
    error_message = "DocIntel should report 'external' mode when using external endpoint"
  }
}

# Should_OutputExistingMode_WhenExistingResourceNamesProvided
run "ai_config_summary_existing_resources" {
  command = plan

  variables {
    sql_admin_password               = "test-password-123!"
    deploy_openai                    = false
    deploy_vision                    = false
    deploy_document_intelligence     = false
    existing_openai_resource_name    = "my-openai"
    existing_openai_resource_group   = "rg-shared"
    existing_vision_resource_name    = "my-vision"
    existing_vision_resource_group   = "rg-shared"
    existing_docintel_resource_name  = "my-docintel"
    existing_docintel_resource_group = "rg-shared"
  }

  assert {
    condition     = output.ai_configuration_summary.openai == "existing:my-openai"
    error_message = "OpenAI should report 'existing:my-openai' mode"
  }

  assert {
    condition     = output.ai_configuration_summary.vision == "existing:my-vision"
    error_message = "Vision should report 'existing:my-vision' mode"
  }

  assert {
    condition     = output.ai_configuration_summary.docintel == "existing:my-docintel"
    error_message = "DocIntel should report 'existing:my-docintel' mode"
  }
}
