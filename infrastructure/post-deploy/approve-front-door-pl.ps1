#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Auto-approves Front Door shared private-link connections on a
    Container Apps Environment after an enterprise self-hosted deploy.

.DESCRIPTION
    Enterprise Bicep creates Front Door shared-private-link resources
    pointing at the CAE. Those connections land in 'Pending' state on
    the CAE side and block FD->CAE traffic until approved. This script
    polls for Pending connections (up to TimeoutSeconds) and approves
    them, then asserts that no Pending/Disconnected/Rejected connection
    remains.

    Runs as part of /deploy-selfhosted --enterprise Phase 6.6, after
    Phase 6.5 post-deploy-smoke.sh passes (Rule 6 in SH_ENTERPRISE_SMOKE_TEST.md:
    never enable traffic on a broken stack).

    Spec: knowzcode/specs/SH_ENTERPRISE_SMOKE_TEST.md Section 2.2
    NodeID: INFRA_FrontDoorPlApproval

.PARAMETER ResourceGroup
    The enterprise resource group containing the CAE.

.PARAMETER CaeName
    Optional. Container Apps Environment name. Auto-discovered via
    `az containerapp env list` if omitted.

.PARAMETER TimeoutSeconds
    Max seconds to wait for a Pending PL connection to appear. Default 180.

.EXAMPLE
    ./approve-front-door-pl.ps1 -ResourceGroup rg-sh-enterprise-smoke-20260418

.EXAMPLE
    ./approve-front-door-pl.ps1 -ResourceGroup rg-x -CaeName cae-knowz -TimeoutSeconds 300
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroup,

    [string]$CaeName = '',

    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

function Write-Info {
    param([string]$Message)
    Write-Host "[fd-pl] $Message"
}

function Invoke-AzCli {
    param([string[]]$Arguments)
    $output = & az @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') failed (exit $LASTEXITCODE): $output"
    }
    return $output
}

# ----- Resolve CAE -----------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($CaeName)) {
    Write-Info "Discovering Container Apps Environment in $ResourceGroup..."
    $CaeName = Invoke-AzCli @('containerapp', 'env', 'list',
        '-g', $ResourceGroup, '--query', '[0].name', '-o', 'tsv')
    $CaeName = ($CaeName | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($CaeName)) {
        throw "No Container Apps Environment found in resource group '$ResourceGroup'"
    }
}

$caeId = Invoke-AzCli @('containerapp', 'env', 'show',
    '-n', $CaeName, '-g', $ResourceGroup, '--query', 'id', '-o', 'tsv')
$caeId = ($caeId | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($caeId)) {
    throw "Unable to resolve CAE resource ID for '$CaeName'"
}

Write-Info "CAE: $caeId"
Write-Info "Polling for Pending FD private-link connections (timeout ${TimeoutSeconds}s)..."

# ----- Poll for Pending connections -----------------------------------------
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$approvedCount = 0

while ((Get-Date) -lt $deadline) {
    $pendingJson = Invoke-AzCli @('network', 'private-endpoint-connection', 'list',
        '--id', $caeId,
        '--query', "[?properties.privateLinkServiceConnectionState.status=='Pending']",
        '-o', 'json')

    $pending = @()
    $joined = ($pendingJson | Out-String).Trim()
    if (-not [string]::IsNullOrWhiteSpace($joined)) {
        try {
            $parsed = $joined | ConvertFrom-Json
            if ($null -ne $parsed) {
                $pending = @($parsed)
            }
        }
        catch {
            throw "Unable to parse private-endpoint-connection list JSON: $_"
        }
    }

    if ($pending.Count -gt 0) {
        foreach ($conn in $pending) {
            $peId = if ($conn.properties.privateEndpoint) { $conn.properties.privateEndpoint.id } else { '<unknown>' }
            Write-Info "Approving PL connection '$($conn.name)' (from $peId)"
            $descr = "Approved by approve-front-door-pl.ps1 on $(Get-Date -Format o)"
            Invoke-AzCli @('network', 'private-endpoint-connection', 'approve',
                '--id', $conn.id,
                '--description', $descr) | Out-Null
            $approvedCount++
        }
        break
    }

    Start-Sleep -Seconds 10
}

if ($approvedCount -eq 0) {
    throw "No Pending Front Door private-link connections appeared within $TimeoutSeconds seconds on $caeId"
}

# ----- Validate final state --------------------------------------------------
$finalStatuses = Invoke-AzCli @('network', 'private-endpoint-connection', 'list',
    '--id', $caeId,
    '--query', '[].properties.privateLinkServiceConnectionState.status',
    '-o', 'tsv')
$finalText = ($finalStatuses | Out-String).Trim()
Write-Info "Final PL statuses: $([Environment]::NewLine)$finalText"

if ($finalText -match 'Pending|Disconnected|Rejected') {
    throw "One or more PL connections did not reach Approved state (statuses: $finalText)"
}

Write-Info "OK: $approvedCount Front Door PL connection(s) approved."
exit 0
