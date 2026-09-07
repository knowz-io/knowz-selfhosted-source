# infrastructure/selfhosted-update.ps1
# Updates Knowz Self-Hosted container apps to a new version.
#
# Usage:
#   .\infrastructure\selfhosted-update.ps1 -ResourceGroup "rg-knowz-selfhosted"
#   .\infrastructure\selfhosted-update.ps1 -ResourceGroup "rg-my-knowz" -Version "0.6.0"
#   .\infrastructure\selfhosted-update.ps1 -ResourceGroup "rg-my-knowz" -Version "latest"
#
# Prerequisites:
#   - Azure CLI installed and logged in (az login)
#   - Correct subscription selected (az account set --subscription "...")

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroup,

    [string]$Version = "latest",

    [string]$Prefix = "",  # Auto-detect if not provided

    [switch]$SkipHealthCheck,

    [switch]$DryRun
)

$ErrorActionPreference = "Continue"

# Pre-flight checks
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Azure CLI (az) not found. Install: https://learn.microsoft.com/cli/azure/install-azure-cli" -ForegroundColor Red
    exit 1
}

$accountInfo = az account show --query name -o tsv 2>$null
if (-not $accountInfo) {
    Write-Host "Error: Not logged in to Azure. Run: az login" -ForegroundColor Red
    exit 1
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Knowz Self-Hosted Update" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Subscription:   $accountInfo"
Write-Host "Resource Group: $ResourceGroup"
Write-Host "Target Version: $Version"
Write-Host ""

# Step 1: Discover container apps in the resource group
Write-Host "[1/4] Discovering container apps..." -ForegroundColor Yellow

$apps = az containerapp list --resource-group $ResourceGroup --query "[].{name:name, image:properties.template.containers[0].image}" -o json 2>$null | ConvertFrom-Json

if (-not $apps -or $apps.Count -eq 0) {
    throw "No container apps found in resource group '$ResourceGroup'"
}

# Identify API, Web, MCP apps by naming convention or image
$apiApp = $apps | Where-Object { $_.name -match "api" -or $_.image -match "selfhosted-api" } | Select-Object -First 1
$webApp = $apps | Where-Object { $_.name -match "web" -or $_.image -match "selfhosted-web" } | Select-Object -First 1
$mcpApp = $apps | Where-Object { $_.name -match "mcp" -or $_.image -match "selfhosted-mcp" } | Select-Object -First 1

if (-not $apiApp) { Write-Host "  Warning: API container app not found" -ForegroundColor DarkYellow }
if (-not $webApp) { Write-Host "  Warning: Web container app not found" -ForegroundColor DarkYellow }
if (-not $mcpApp) { Write-Host "  Warning: MCP container app not found" -ForegroundColor DarkYellow }

Write-Host "  Found:" -ForegroundColor Green
if ($apiApp) { Write-Host "    API: $($apiApp.name) (current: $($apiApp.image))" }
if ($webApp) { Write-Host "    Web: $($webApp.name) (current: $($webApp.image))" }
if ($mcpApp) { Write-Host "    MCP: $($mcpApp.name) (current: $($mcpApp.image))" }
Write-Host ""

# Step 2: Build target images
Write-Host "[2/4] Preparing update..." -ForegroundColor Yellow

$targetImages = @{
    api = "ghcr.io/knowz-io/knowz-selfhosted-api:$Version"
    web = "ghcr.io/knowz-io/knowz-selfhosted-web:$Version"
    mcp = "ghcr.io/knowz-io/knowz-selfhosted-mcp:$Version"
}

Write-Host "  Target images:" -ForegroundColor DarkGray
foreach ($key in $targetImages.Keys) {
    Write-Host "    $key -> $($targetImages[$key])" -ForegroundColor DarkGray
}
Write-Host ""

if ($DryRun) {
    Write-Host "DRY RUN -- no changes made." -ForegroundColor DarkYellow
    Write-Host "Would update:" -ForegroundColor DarkYellow
    if ($apiApp) { Write-Host "  $($apiApp.name) -> $($targetImages.api)" }
    if ($webApp) { Write-Host "  $($webApp.name) -> $($targetImages.web)" }
    if ($mcpApp) { Write-Host "  $($mcpApp.name) -> $($targetImages.mcp)" }
    exit 0
}

# Step 3: Update container apps
Write-Host "[3/4] Updating container apps..." -ForegroundColor Yellow

$updateCount = 0

if ($apiApp) {
    Write-Host "  Updating $($apiApp.name)..." -ForegroundColor DarkGray
    az containerapp update --name $apiApp.name --resource-group $ResourceGroup --image $targetImages.api --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    $($apiApp.name) updated." -ForegroundColor Green
        $updateCount++
    } else {
        Write-Host "    Failed to update $($apiApp.name)" -ForegroundColor Red
    }
}

if ($webApp) {
    Write-Host "  Updating $($webApp.name)..." -ForegroundColor DarkGray
    az containerapp update --name $webApp.name --resource-group $ResourceGroup --image $targetImages.web --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    $($webApp.name) updated." -ForegroundColor Green
        $updateCount++
    } else {
        Write-Host "    Failed to update $($webApp.name)" -ForegroundColor Red
    }
}

if ($mcpApp) {
    Write-Host "  Updating $($mcpApp.name)..." -ForegroundColor DarkGray
    az containerapp update --name $mcpApp.name --resource-group $ResourceGroup --image $targetImages.mcp --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "    $($mcpApp.name) updated." -ForegroundColor Green
        $updateCount++
    } else {
        Write-Host "    Failed to update $($mcpApp.name)" -ForegroundColor Red
    }
}

Write-Host ""

# Step 4: Health check
if (-not $SkipHealthCheck -and $updateCount -gt 0) {
    Write-Host "[4/4] Running health checks..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10  # Wait for containers to restart

    if ($apiApp) {
        $apiFqdn = (az containerapp show --name $apiApp.name --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv 2>$null)
        if ($apiFqdn) {
            try {
                $response = Invoke-WebRequest -Uri "https://$apiFqdn/api/v1/health" -TimeoutSec 15 -ErrorAction SilentlyContinue
                Write-Host "  API: $($response.StatusCode)" -ForegroundColor Green
            } catch {
                # 401 means auth is working — API is healthy
                if ($_.Exception.Response.StatusCode.value__ -eq 401) {
                    Write-Host "  API: 401 (auth working)" -ForegroundColor Green
                } else {
                    Write-Host "  API: Unhealthy or starting up" -ForegroundColor DarkYellow
                }
            }
        }
    }

    if ($webApp) {
        $webFqdn = (az containerapp show --name $webApp.name --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv 2>$null)
        if ($webFqdn) {
            try {
                $response = Invoke-WebRequest -Uri "https://$webFqdn/" -TimeoutSec 15 -ErrorAction SilentlyContinue
                Write-Host "  Web: $($response.StatusCode)" -ForegroundColor Green
            } catch {
                Write-Host "  Web: Unhealthy or starting up" -ForegroundColor DarkYellow
            }
        }
    }

    if ($mcpApp) {
        $mcpFqdn = (az containerapp show --name $mcpApp.name --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv 2>$null)
        if ($mcpFqdn) {
            try {
                $response = Invoke-WebRequest -Uri "https://$mcpFqdn/health" -TimeoutSec 15 -ErrorAction SilentlyContinue
                Write-Host "  MCP: $($response.StatusCode)" -ForegroundColor Green
            } catch {
                Write-Host "  MCP: Unhealthy or starting up" -ForegroundColor DarkYellow
            }
        }
    }
} else {
    Write-Host "[4/4] Health check skipped." -ForegroundColor DarkGray
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Update Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Updated $updateCount container app(s) to version $Version" -ForegroundColor White
Write-Host ""
Write-Host "Database migrations run automatically on API restart." -ForegroundColor DarkGray
Write-Host "If you encounter issues, check logs:" -ForegroundColor DarkGray
if ($apiApp) {
    Write-Host "  az containerapp logs show --name $($apiApp.name) --resource-group $ResourceGroup --tail 50" -ForegroundColor DarkGray
}
Write-Host ""
