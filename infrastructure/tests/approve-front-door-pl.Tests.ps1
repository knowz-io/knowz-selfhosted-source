# =============================================================================
# Pester tests for approve-front-door-pl.ps1 (NodeID INFRA_FrontDoorPlApproval)
# =============================================================================
# Validates:
#   1. Script parses without syntax errors.
#   2. Mandatory ResourceGroup parameter declared.
#   3. Optional CaeName + TimeoutSeconds parameters declared with expected defaults/range.
#   4. Script references expected az subcommands for listing and approving PE connections.
#   5. Final validation rejects any non-Approved state.
#
# Compatible with Pester 5.x.
# =============================================================================

Describe "approve-front-door-pl.ps1 - INFRA_FrontDoorPlApproval" {
    BeforeAll {
        $script:scriptPath = Join-Path $PSScriptRoot "..\post-deploy\approve-front-door-pl.ps1"
        $script:scriptText = ""
        $script:ast = $null
        $script:parseErrors = @()
        $script:paramBlock = $null
        $script:paramNames = @()

        if (Test-Path $script:scriptPath) {
            $tokens = $null
            $errors = $null
            $script:ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $script:scriptPath, [ref]$tokens, [ref]$errors)
            $script:parseErrors = @($errors)
            $script:scriptText = Get-Content $script:scriptPath -Raw
            $script:paramBlock = $script:ast.Find(
                { param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $true)

            if ($script:paramBlock) {
                $script:paramNames = $script:paramBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }
            }
        }
        else {
            $script:parseErrors = @("Script file not found: $($script:scriptPath)")
        }
    }

    Context "Script file exists and parses" {
        It "Should_ExistAtExpectedPath_WhenTestsRun" {
            Test-Path $script:scriptPath | Should -Be $true
        }

        It "Should_ParseWithoutErrors_WhenAstLoaded" {
            @($script:parseErrors).Count | Should -Be 0
        }
    }

    Context "VERIFY 1: Parameter contract" {
        It "Should_DeclareResourceGroup_WhenParamBlockParsed" {
            $script:paramNames -contains "ResourceGroup" | Should -Be $true
        }
        It "Should_DeclareCaeName_WhenParamBlockParsed" {
            $script:paramNames -contains "CaeName" | Should -Be $true
        }
        It "Should_DeclareTimeoutSeconds_WhenParamBlockParsed" {
            $script:paramNames -contains "TimeoutSeconds" | Should -Be $true
        }

        It "Should_MarkResourceGroupMandatory_WhenParameterAttributesParsed" {
            $rg = $script:paramBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "ResourceGroup" }
            $mandatoryAttr = $rg.Attributes | Where-Object {
                $_ -is [System.Management.Automation.Language.AttributeAst] -and
                $_.TypeName.Name -eq "Parameter"
            }
            ($mandatoryAttr.NamedArguments | Where-Object { $_.ArgumentName -eq "Mandatory" }).Argument.Extent.Text `
                | Should -Be '$true'
        }

        It "Should_DefaultTimeoutTo180Seconds_WhenParameterDefaultParsed" {
            $script:scriptText -match '\[int\]\s*\$TimeoutSeconds\s*=\s*180' | Should -Be $true
        }
    }

    Context "VERIFY 2: Script references required az subcommands" {
        It "Should_CallPrivateEndpointConnectionList_WhenDiscoveringPending" {
            $script:scriptText -match "private-endpoint-connection.*list" | Should -Be $true
        }

        It "Should_CallPrivateEndpointConnectionApprove_WhenApprovingPending" {
            $script:scriptText -match "private-endpoint-connection.*approve" | Should -Be $true
        }

        It "Should_DiscoverContainerAppsEnvironment_WhenCaeNameOmitted" {
            $script:scriptText -match "containerapp\s+env\s+list" | Should -Be $true
        }
    }

    Context "VERIFY 3: Traffic-safety gates" {
        It "Should_RejectFinalStateContainingPending_WhenValidating" {
            $script:scriptText -match "Pending\|Disconnected\|Rejected" | Should -Be $true
        }

        It "Should_ThrowWhenNoPendingConnectionsFound_BeforeTimeout" {
            $script:scriptText -match "No Pending Front Door private-link connections appeared" | Should -Be $true
        }

        It "Should_PollWithStartSleep_WhileWaitingForPending" {
            $script:scriptText -match "Start-Sleep\s+-Seconds" | Should -Be $true
        }
    }

    Context "VERIFY 4: Exits non-zero on failure paths" {
        It "Should_SetErrorActionPreferenceToStop_WhenScriptLoads" {
            $script:scriptText -match "\`$ErrorActionPreference\s*=\s*'Stop'" | Should -Be $true
        }

        It "Should_ThrowOnAzCliNonZeroExit_WhenInvokeAzCliFails" {
            $script:scriptText -match "throw ""az.*failed" | Should -Be $true
        }
    }
}
