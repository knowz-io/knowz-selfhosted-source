# =============================================================================
# Pester tests for selfhosted-deploy.ps1 (NodeID 1C)
# =============================================================================
# Validates:
#   1. New parameters exist (ExistingOpenAi/Vision/DocIntel Name + ResourceGroup)
#   2. Pre-deployment validation (Step 0.5) exists and checks paired name/RG
#   3. Bicep deployment body includes 6 new existing* parameters
#   4. RBAC retry loop replaces the 10-second sleep
#   5. Secret retrieval uses 3-tier pattern for OpenAI, Vision, DocIntel
#   6. Vision and DocIntel failures throw (instead of warnings)
#   7. Header display shows existing resource info
#
# Compatible with Pester 5.x.
# =============================================================================

Describe "selfhosted-deploy.ps1 - NodeID 1C (existing AI resources, RBAC retry)" {
    BeforeAll {
        $script:scriptPath = Join-Path $PSScriptRoot "..\selfhosted-deploy.ps1"
        $script:scriptText = ""
        $script:ast = $null
        $script:parseErrors = @()
        $script:paramNames = @()
        $script:codeText = ""
        $script:bicepPath = Join-Path $PSScriptRoot "..\selfhosted-enterprise.bicep"
        $script:bicepText = ""

        if (Test-Path $script:scriptPath) {
            $tokens = $null
            $errors = $null
            $script:ast = [System.Management.Automation.Language.Parser]::ParseFile($script:scriptPath, [ref]$tokens, [ref]$errors)
            $script:parseErrors = @($errors)
            $script:scriptText = Get-Content $script:scriptPath -Raw

            $paramBlock = $script:ast.ParamBlock
            if (-not $paramBlock) {
                # Top-level param block may live in the first script block statement.
                $paramBlock = $script:ast.Find({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $true)
            }

            if ($paramBlock) {
                $script:paramNames = $paramBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
            }

            # Strip comments before searching for Get-Random so the rule-documentation
            # lines above the helper function don't register as code uses of Get-Random.
            $lines = Get-Content $script:scriptPath
            $codeLines = $lines | Where-Object { $_ -notmatch '^\s*#' }
            $script:codeText = ($codeLines -join "`n")
        }
        else {
            $script:parseErrors = @("Script file not found: $($script:scriptPath)")
        }

        if (Test-Path $script:bicepPath) {
            $script:bicepText = Get-Content $script:bicepPath -Raw
        }
    }

    Context "Script file exists" {
        It "Should_ExistAtExpectedPath_WhenTestsRun" {
            Test-Path $script:scriptPath | Should -Be $true
        }
    }

    Context "VERIFY 1: Six new parameters exist in param block" {
        It "Should_DeclareExistingOpenAiName_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingOpenAiName" | Should -Be $true
        }
        It "Should_DeclareExistingOpenAiResourceGroup_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingOpenAiResourceGroup" | Should -Be $true
        }
        It "Should_DeclareExistingVisionName_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingVisionName" | Should -Be $true
        }
        It "Should_DeclareExistingVisionResourceGroup_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingVisionResourceGroup" | Should -Be $true
        }
        It "Should_DeclareExistingDocIntelName_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingDocIntelName" | Should -Be $true
        }
        It "Should_DeclareExistingDocIntelResourceGroup_WhenParamBlockParsed" {
            $script:paramNames -contains "ExistingDocIntelResourceGroup" | Should -Be $true
        }
    }

    Context "VERIFY 2: Pre-deployment validation (Step 0.5)" {
        It "Should_IncludeStep0_5Header_WhenScriptRendered" {
            ($script:scriptText -match "\[0\.5/7\]") | Should -Be $true
        }
        It "Should_ThrowWhenExistingOpenAiNameWithoutResourceGroup_WhenValidationRuns" {
            # pattern: if ($ExistingOpenAiName -and -not $ExistingOpenAiResourceGroup) { throw ... }
            ($script:scriptText -match '\$ExistingOpenAiName\s+-and\s+-not\s+\$ExistingOpenAiResourceGroup') | Should -Be $true
        }
        It "Should_ThrowWhenExistingVisionNameWithoutResourceGroup_WhenValidationRuns" {
            ($script:scriptText -match '\$ExistingVisionName\s+-and\s+-not\s+\$ExistingVisionResourceGroup') | Should -Be $true
        }
        It "Should_ThrowWhenExistingDocIntelNameWithoutResourceGroup_WhenValidationRuns" {
            ($script:scriptText -match '\$ExistingDocIntelName\s+-and\s+-not\s+\$ExistingDocIntelResourceGroup') | Should -Be $true
        }
        It "Should_CallAzCognitiveservicesAccountShow_ToValidateExistingOpenAi" {
            # Must validate accessibility before deployment
            ($script:scriptText -match 'az cognitiveservices account show --name \$ExistingOpenAiName') | Should -Be $true
        }
        It "Should_CallAzCognitiveservicesAccountShow_ToValidateExistingVision" {
            ($script:scriptText -match 'az cognitiveservices account show --name \$ExistingVisionName') | Should -Be $true
        }
        It "Should_CallAzCognitiveservicesAccountShow_ToValidateExistingDocIntel" {
            ($script:scriptText -match 'az cognitiveservices account show --name \$ExistingDocIntelName') | Should -Be $true
        }
    }

    Context "VERIFY 3: Bicep deployment body includes 6 new existing* parameters" {
        It "Should_PassExistingOpenAiName_InDeploymentBody" {
            ($script:scriptText -match 'existingOpenAiName\s*=\s*@\{\s*value\s*=\s*\$ExistingOpenAiName\s*\}') | Should -Be $true
        }
        It "Should_PassExistingOpenAiResourceGroup_InDeploymentBody" {
            ($script:scriptText -match 'existingOpenAiResourceGroup\s*=\s*@\{\s*value\s*=\s*\$ExistingOpenAiResourceGroup\s*\}') | Should -Be $true
        }
        It "Should_PassExistingVisionName_InDeploymentBody" {
            ($script:scriptText -match 'existingVisionName\s*=\s*@\{\s*value\s*=\s*\$ExistingVisionName\s*\}') | Should -Be $true
        }
        It "Should_PassExistingVisionResourceGroup_InDeploymentBody" {
            ($script:scriptText -match 'existingVisionResourceGroup\s*=\s*@\{\s*value\s*=\s*\$ExistingVisionResourceGroup\s*\}') | Should -Be $true
        }
        It "Should_PassExistingDocIntelName_InDeploymentBody" {
            ($script:scriptText -match 'existingDocIntelName\s*=\s*@\{\s*value\s*=\s*\$ExistingDocIntelName\s*\}') | Should -Be $true
        }
        It "Should_PassExistingDocIntelResourceGroup_InDeploymentBody" {
            ($script:scriptText -match 'existingDocIntelResourceGroup\s*=\s*@\{\s*value\s*=\s*\$ExistingDocIntelResourceGroup\s*\}') | Should -Be $true
        }
    }

    Context "VERIFY 4: Key Vault RBAC retry loop replaces 10-second sleep" {
        It "Should_NotContainOriginalTenSecondSleep_InKeyVaultStep" {
            # Previous code had a hard-coded Start-Sleep -Seconds 10 immediately
            # before the KV verification. After the fix, the retry block uses 15s per attempt.
            # Ensure the single "Start-Sleep -Seconds 10" line used for KV is gone.
            $kvSection = $null
            if ($script:scriptText -match "(?s)Verifying Key Vault secrets.*?Set-Content") {
                $kvSection = $Matches[0]
            }
            $kvSection | Should -Not -BeNullOrEmpty
            ($kvSection -match 'Start-Sleep\s+-Seconds\s+10\b') | Should -Be $false
        }
        It "Should_ContainKvRetryCounter_WhenRetryLoopImplemented" {
            ($script:scriptText -match '\$kvRetryCount') | Should -Be $true
        }
        It "Should_ContainKvMaxRetries_WhenRetryLoopImplemented" {
            ($script:scriptText -match '\$kvMaxRetries') | Should -Be $true
        }
        It "Should_ContainKvVerifiedFlag_WhenRetryLoopImplemented" {
            ($script:scriptText -match '\$kvVerified') | Should -Be $true
        }
        It "Should_AssignKeyVaultSecretsOfficerRole_WhenVerificationFails" {
            ($script:scriptText -match 'Key Vault Secrets Officer') | Should -Be $true
        }
    }

    Context "VERIFY 5: Secret retrieval uses 3-tier pattern" {
        It "Should_HaveOpenAiThreeTier_DeployedExistingExternal" {
            # Tier 2: ExistingOpenAiName branch with elseif
            ($script:scriptText -match 'elseif\s*\(\s*\$ExistingOpenAiName\s*\)') | Should -Be $true
        }
        It "Should_HaveVisionThreeTier_DeployedExistingExternal" {
            ($script:scriptText -match 'elseif\s*\(\s*\$ExistingVisionName\s*\)') | Should -Be $true
        }
        It "Should_HaveDocIntelThreeTier_DeployedExistingExternal" {
            ($script:scriptText -match 'elseif\s*\(\s*\$ExistingDocIntelName\s*\)') | Should -Be $true
        }
        It "Should_QueryKeysForExistingOpenAi_WithSpecificResourceGroup" {
            ($script:scriptText -match 'az cognitiveservices account keys list --name \$ExistingOpenAiName --resource-group \$ExistingOpenAiResourceGroup') | Should -Be $true
        }
        It "Should_QueryKeysForExistingVision_WithSpecificResourceGroup" {
            ($script:scriptText -match 'az cognitiveservices account keys list --name \$ExistingVisionName --resource-group \$ExistingVisionResourceGroup') | Should -Be $true
        }
        It "Should_QueryKeysForExistingDocIntel_WithSpecificResourceGroup" {
            ($script:scriptText -match 'az cognitiveservices account keys list --name \$ExistingDocIntelName --resource-group \$ExistingDocIntelResourceGroup') | Should -Be $true
        }
    }

    Context "VERIFY 6: Vision and DocIntel failures are errors (throw)" {
        It "Should_ThrowOnVisionKeyRetrievalFailure_FromDeployedResource" {
            # The deployed-resource branch for vision should throw, not just warn
            ($script:scriptText -match 'throw\s+"Failed to retrieve Vision key from deployed resource"') | Should -Be $true
        }
        It "Should_ThrowOnDocIntelKeyRetrievalFailure_FromDeployedResource" {
            ($script:scriptText -match 'throw\s+"Failed to retrieve (Document Intelligence|Doc Intel|DocIntel) key from deployed resource"') | Should -Be $true
        }
        It "Should_ThrowOnVisionKeyRetrievalFailure_FromExistingResource" {
            $needle = 'throw "Failed to retrieve key from existing Vision resource'
            $script:scriptText.Contains($needle) | Should -Be $true
        }
        It "Should_ThrowOnDocIntelKeyRetrievalFailure_FromExistingResource" {
            # accept any of: Document Intelligence / Doc Intel / DocIntel naming
            $found =
                $script:scriptText.Contains('throw "Failed to retrieve key from existing Document Intelligence resource') -or
                $script:scriptText.Contains('throw "Failed to retrieve key from existing Doc Intel resource') -or
                $script:scriptText.Contains('throw "Failed to retrieve key from existing DocIntel resource')
            $found | Should -Be $true
        }
    }

    Context "VERIFY 7: Header displays existing resource info" {
        It "Should_DisplayExistingOpenAiInHeader_WhenExistingOpenAiNameProvided" {
            ($script:scriptText -match 'OpenAI:\s+Existing\s+\(\$ExistingOpenAiName in \$ExistingOpenAiResourceGroup\)') | Should -Be $true
        }
        It "Should_DisplayExistingVisionInHeader_WhenExistingVisionNameProvided" {
            ($script:scriptText -match 'Vision:\s+Existing\s+\(\$ExistingVisionName in \$ExistingVisionResourceGroup\)') | Should -Be $true
        }
        It "Should_DisplayExistingDocIntelInHeader_WhenExistingDocIntelNameProvided" {
            ($script:scriptText -match 'Doc Intel:\s+Existing\s+\(\$ExistingDocIntelName in \$ExistingDocIntelResourceGroup\)') | Should -Be $true
        }
    }

    Context "VERIFY 8: No syntactic regressions" {
        It "Should_ParseWithoutErrors_WhenAstParsed" {
            @($script:parseErrors).Count | Should -Be 0
        }
        It "Should_KeepExistingRequiredParameter_SqlPassword" {
            $script:paramNames -contains "SqlPassword" | Should -Be $true
        }
        It "Should_KeepExistingRequiredParameter_AdminPassword" {
            $script:paramNames -contains "AdminPassword" | Should -Be $true
        }
        It "Should_KeepSevenStepProgressMarkers_WhenScriptIntact" {
            # Steps 1/7 .. 7/7 still present in progress markers
            ($script:scriptText.Contains('[1/7]')) | Should -Be $true
            ($script:scriptText.Contains('[2/7]')) | Should -Be $true
            ($script:scriptText.Contains('[3/7]')) | Should -Be $true
            ($script:scriptText.Contains('[4/7]')) | Should -Be $true
            ($script:scriptText.Contains('[5/7]')) | Should -Be $true
            ($script:scriptText.Contains('[6/7]')) | Should -Be $true
            ($script:scriptText.Contains('[7/7]')) | Should -Be $true
        }
    }

    Context "VERIFY SEC_P0Triage §Rule 6: crypto-safe RNG for secret generation" {
        It "Should_NotInvokeGetRandom_InExecutableCode" {
            # Get-Random draws from System.Random — not cryptographic. Must not be
            # used anywhere in executable paths of the deploy script.
            $script:codeText | Should -Not -Match '(?<!#.*)Get-Random'
        }

        It "Should_DefineNewCryptoSecretHelper_WhenScriptParsed" {
            # Canonical helper must exist so every secret-mint site uses the OS CSPRNG.
            $script:scriptText | Should -Match 'function New-CryptoSecret'
        }

        It "Should_UseRandomNumberGeneratorFill_InHelper" {
            $script:scriptText | Should -Match 'RandomNumberGenerator\]::Fill'
        }

        It "Should_CallNewCryptoSecret_AtLeastTwice_WhenSecretsGenerated" {
            # Both the Container Apps JWT override path AND the appsettings.Local.json
            # template must use the crypto helper. Grep for the two canonical call
            # sites — regenerations of this script should keep both.
            $count = ([regex]::Matches($script:codeText, 'New-CryptoSecret')).Count
            $count | Should -BeGreaterThan 1
        }
    }

    Context "VERIFY SEC_P0Triage §Rule 5: parameterized SQL in enterprise Bicep" {
        It "Should_ExistAtExpectedPath_WhenTestsRun" {
            Test-Path $script:bicepPath | Should -Be $true
        }

        It "Should_NotInterpolateAadAdminDisplayNameDirectlyIntoTSql" {
            # Legacy pattern was: CREATE USER [$AadAdminDisplayName] WITH SID = ...
            # Post-fix: identifier comes through QUOTENAME($(AadAdminDisplayName))
            # and the value is supplied via -Variable. The raw `[$AadAdminDisplayName]`
            # pattern inside a CREATE USER statement is forbidden.
            $script:bicepText | Should -Not -Match 'CREATE USER \[\$AadAdminDisplayName\]'
        }

        It "Should_PassAadVariables_ViaInvokeSqlcmdVariable" {
            $script:bicepText | Should -Match 'Invoke-Sqlcmd'
            $script:bicepText | Should -Match '-Variable'
            $script:bicepText | Should -Match 'AadAdminDisplayName='
            $script:bicepText | Should -Match 'AadAdminObjectId='
        }

        It "Should_UseQuoteName_ForIdentifierConstruction" {
            $script:bicepText | Should -Match 'QUOTENAME'
        }

        It "Should_ValidateInputFormat_BeforeTouchingSql" {
            # The defensive regex guard against '] and other T-SQL metacharacters.
            $script:bicepText | Should -Match '\$AadAdminDisplayName -notmatch'
            $script:bicepText | Should -Match '\$AadAdminObjectId -notmatch'
        }
    }
}
