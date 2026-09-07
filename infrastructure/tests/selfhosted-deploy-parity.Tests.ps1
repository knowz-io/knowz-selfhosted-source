# =============================================================================
# Pester tests for self-hosted deployment parity fixes
# =============================================================================
# Guards the remaining review findings from kc-fix-selfhosted-deploy-parity:
#   - Enterprise Bicep BYO Key Vault must use effective KV URI and skip local
#     secret writes in BYO mode.
#   - Enterprise Terraform BYO inputs must be consumed by resources, not only
#     declared as variables.
#   - Enterprise Terraform external ACR must drive registry and image paths.
#   - deploy-selfhosted Claude skills must route --enterprise --terraform to
#     terraform/enterprise.
#   - selfhosted-test.bicepparam and direct docs must be buildable as written.
#
# Compatible with Pester 5.x syntax.
# =============================================================================

Describe "Self-hosted deployment parity" {

    BeforeAll {
        $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path

        $script:enterpriseBicepPath = Join-Path $script:repoRoot "selfhosted\infrastructure\selfhosted-enterprise.bicep"
        $script:terraformRoot       = Join-Path $script:repoRoot "selfhosted\terraform\enterprise"
        $script:rootSkillPath       = Join-Path $script:repoRoot ".claude\skills\deploy-selfhosted\SKILL.md"
        $script:selfhostedSkillPath = Join-Path $script:repoRoot "selfhosted\.claude\skills\deploy-selfhosted\SKILL.md"
        $script:testParamPath       = Join-Path $script:repoRoot "selfhosted\infrastructure\selfhosted-test.bicepparam"
        $script:testBicepPath       = Join-Path $script:repoRoot "selfhosted\infrastructure\selfhosted-test.bicep"
        $script:guidePath           = Join-Path $script:repoRoot "docs\SELFHOSTED_DEPLOYMENT_GUIDE.md"
        $script:readmePath          = Join-Path $script:repoRoot "selfhosted\README.md"
        $script:deployScriptPath    = Join-Path $script:repoRoot "selfhosted\infrastructure\selfhosted-deploy.ps1"

        $script:enterpriseBicepText = Get-Content $script:enterpriseBicepPath -Raw
        $script:networkText         = Get-Content (Join-Path $script:terraformRoot "network.tf") -Raw
        $script:keyVaultText        = Get-Content (Join-Path $script:terraformRoot "keyvault.tf") -Raw
        $script:monitoringText      = Get-Content (Join-Path $script:terraformRoot "monitoring.tf") -Raw
        $script:containerText       = Get-Content (Join-Path $script:terraformRoot "containers.tf") -Raw
        $script:mainText            = Get-Content (Join-Path $script:terraformRoot "main.tf") -Raw
        $script:allTfText           = (Get-ChildItem $script:terraformRoot -Filter *.tf | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
        $script:rootSkill           = Get-Content $script:rootSkillPath -Raw
        $script:selfSkill           = Get-Content $script:selfhostedSkillPath -Raw
        $script:paramText           = Get-Content $script:testParamPath -Raw
        $script:testBicepText       = Get-Content $script:testBicepPath -Raw
        $script:guideText           = Get-Content $script:guidePath -Raw
        $script:readmeText          = Get-Content $script:readmePath -Raw
        $script:deployScriptText    = Get-Content $script:deployScriptPath -Raw
    }

    Context "Enterprise Bicep BYO Key Vault" {
        It "Should_UseEffectiveKvUri_ForContainerAppSecretReferences" {
            $script:enterpriseBicepText | Should -Not -Match 'keyVault\.properties\.vaultUri\}secrets/'
            $script:enterpriseBicepText | Should -Match '\$\{effectiveKvUri\}secrets/'
        }

        It "Should_GateLocalKeyVaultSecretResources_WhenByoKeyVaultIdSet" {
            $secretDeclarations = [regex]::Matches(
                $script:enterpriseBicepText,
                "(?m)^resource\s+secret\w+\s+'Microsoft\.KeyVault/vaults/secrets@2023-07-01'.*$"
            )
            $ungatedSecretDeclarations = @(
                $secretDeclarations |
                    Where-Object { $_.Value -notmatch "=\s*if\s*\(\s*empty\(byoKeyVaultId\)\s*\)" }
            )
            $ungatedSecretDeclarations.Count | Should -Be 0
        }
    }

    Context "Enterprise Terraform BYO infrastructure parity" {
        It "Should_UseEffectiveSubnetLocals_InsteadOfHardcodedCreatedSubnets" {
            $script:networkText | Should -Match 'container_apps_subnet_id'
            $script:networkText | Should -Match 'private_endpoints_subnet_id'
            $script:containerText | Should -Match 'local\.container_apps_subnet_id'
            $script:allTfText | Should -Not -Match 'azurerm_subnet\.private_endpoints\.id'
        }

        It "Should_ConditionallyCreateLocalKeyVault_AndUseEffectiveKvLocals" {
            $script:keyVaultText | Should -Match 'count\s*=\s*var\.byo_key_vault_id\s*==\s*""\s*\?\s*1\s*:\s*0'
            $script:keyVaultText | Should -Match 'effective_key_vault_id'
            $script:containerText | Should -Match 'local\.effective_key_vault_uri'
        }

        It "Should_UseEffectiveLogAnalyticsWorkspace_WhenCentralLawProvided" {
            $script:monitoringText | Should -Match 'effective_log_analytics_workspace_id'
            $script:containerText | Should -Match 'local\.effective_log_analytics_workspace_id'
        }

        It "Should_ResolveExistingOpenAiResourceId_OutsideIdentityRoleOnly" {
            $script:mainText | Should -Match 'existing_openai_resource_id'
            $script:mainText | Should -Match 'effective_openai_endpoint'
            $script:allTfText | Should -Match 'data\s+"azurerm_cognitive_account"\s+"openai_existing_by_id"'
        }

        It "Should_SuppressOpenAiApiKeySecretAndEnv_WhenUsingManagedIdentity" {
            $script:mainText | Should -Match 'openai_use_mi\s*='
            $script:mainText | Should -Match 'effective_openai_key\s*=\s*local\.openai_use_mi\s*\?\s*""\s*:\s*var\.external_openai_key'
            $script:keyVaultText | Should -Match 'count\s*=\s*var\.byo_key_vault_id\s*==\s*""\s*&&\s*!local\.openai_use_mi\s*\?\s*1\s*:\s*0'
            $script:containerText | Should -Match 'dynamic\s+"secret"\s*\{\s*for_each\s*=\s*local\.openai_use_mi\s*\?\s*\[\]\s*:\s*\[1\]'
            $script:containerText | Should -Match 'dynamic\s+"env"\s*\{\s*for_each\s*=\s*local\.openai_use_mi\s*\?\s*\[\]\s*:\s*\[1\]'
            $script:containerText | Should -Not -Match '(?s)secret\s*\{\s*name\s*=\s*"openai-apikey"'
            $script:containerText | Should -Not -Match '(?s)env\s*\{\s*name\s*=\s*"AzureOpenAI__ApiKey"'
        }

        It "Should_UseExternalAcrLocals_ForRegistryAndImages" {
            $script:containerText | Should -Match 'local\.effective_registry_server'
            $script:containerText | Should -Match 'local\.registry_path'
            $script:containerText | Should -Not -Match '\$\{var\.registry_server\}/knowz-io/'
        }
    }

    Context "deploy-selfhosted skill routing" {
        It "Should_RouteEnterpriseTerraform_ToEnterpriseDirectory_InRootSkill" {
            $script:rootSkill | Should -Match 'terraform/enterprise'
            $script:rootSkill | Should -Match 'ENTERPRISE.*terraform/enterprise|terraform/enterprise.*ENTERPRISE'
            $script:rootSkill | Should -Not -Match 'cd\s+\$REPO_ROOT/terraform/standard'
        }

        It "Should_RouteEnterpriseTerraform_ToEnterpriseDirectory_InSelfHostedSkillCopy" {
            $script:selfSkill | Should -Match 'terraform/enterprise'
            $script:selfSkill | Should -Match 'ENTERPRISE.*terraform/enterprise|terraform/enterprise.*ENTERPRISE'
            $script:selfSkill | Should -Not -Match 'cd\s+\$REPO_ROOT/terraform/standard'
        }

        It "Should_ContainGuardAgainstEnterpriseTerraformStandardFallback_InBothCopies" {
            $script:rootSkill | Should -Match 'enterprise.*standard|standard.*enterprise'
            $script:selfSkill | Should -Match 'enterprise.*standard|standard.*enterprise'
        }
    }

    Context "Standard self-hosted params and docs" {
        It "Should_NotUseDependentEnvironmentFallback_ForCaEmbeddingDimensions" {
            $script:paramText | Should -Not -Match "readEnvironmentVariable\('SH_CA_EMBEDDING_DIMENSIONS',\s*string\(embeddingDimensions\)\)"
            $script:paramText | Should -Match "SH_CA_EMBEDDING_DIMENSIONS"
        }

        It "Should_DocumentBothRequiredPasswords_ForDirectBicepDeploys" {
            $script:testBicepText | Should -Match 'adminPassword'
            $script:testBicepText | Should -Match 'sqlAdminPassword'
            $script:guideText     | Should -Match 'adminPassword'
            $script:guideText     | Should -Match 'sqlAdminPassword'
            # selfhosted/README.md must NOT mention these Bicep params.
            # F1-A (SH_AzureDeployPathHidden VERIFY-A9) hid Azure deploy from the
            # limited-edition README; leftover templates stay in-tree but are not
            # a documented product path. Compose SuperAdmin is ADMIN_PASSWORD.
            $script:readmeText    | Should -Not -Match 'adminPassword'
            $script:readmeText    | Should -Not -Match 'sqlAdminPassword'
        }

        It "Should_LeadCustomerInstallWithKnowzUp_NotUngatedGitClone" {
            $script:readmeText | Should -Match 'npx @knowzai/cli'
            $script:readmeText | Should -Match 'knowz up'
            $cliIndex = $script:readmeText.IndexOf('npx @knowzai/cli')
            $cloneIndex = $script:readmeText.IndexOf('git clone https://github.com/knowz-io/knowz-selfhosted-source.git')
            $cliIndex | Should -BeGreaterThan -1
            $cloneIndex | Should -BeGreaterThan $cliIndex
            $script:readmeText | Should -Match 'operators only|not.*customer install'
        }

        It "Should_KeepDeployScriptPassingBothSecurePasswords" {
            $script:deployScriptText | Should -Match 'sqlAdminPassword\s*=\s*@\{\s*value\s*=\s*\$SqlPassword\s*\}'
            $script:deployScriptText | Should -Match 'adminPassword\s*=\s*@\{\s*value\s*=\s*\$AdminPassword\s*\}'
        }
    }
}
