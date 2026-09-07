# infrastructure/selfhosted-teardown.ps1
# Tears down the self-hosted Knowz resource group and all resources within it.
#
# Usage:
#   .\infrastructure\selfhosted-teardown.ps1
#   .\infrastructure\selfhosted-teardown.ps1 -ResourceGroup "rg-my-selfhosted" -Force

[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-knowz-selfhosted",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "`n========================================" -ForegroundColor Red
Write-Host " Knowz Self-Hosted Teardown" -ForegroundColor Red
Write-Host "========================================" -ForegroundColor Red
Write-Host "Resource Group: $ResourceGroup"
Write-Host ""

# Check if resource group exists
$exists = az group exists --name $ResourceGroup --output tsv 2>&1
if ($exists -ne "true") {
    Write-Host "Resource group '$ResourceGroup' does not exist. Nothing to do." -ForegroundColor DarkGray
    exit 0
}

if (-not $Force) {
    $confirm = Read-Host "This will DELETE all resources in '$ResourceGroup'. Type 'yes' to confirm"
    if ($confirm -ne "yes") {
        Write-Host "Aborted." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Deleting resource group '$ResourceGroup'..." -ForegroundColor Yellow
az group delete --name $ResourceGroup --yes --no-wait
if ($LASTEXITCODE -ne 0) { throw "Failed to initiate resource group deletion" }

Write-Host "Deletion initiated (async). Waiting for completion..." -ForegroundColor Yellow

# Poll until deleted
$maxWait = 600  # 10 minutes
$elapsed = 0
while ($elapsed -lt $maxWait) {
    Start-Sleep -Seconds 15
    $elapsed += 15
    $still = az group exists --name $ResourceGroup --output tsv 2>&1
    if ($still -ne "true") {
        Write-Host "`nResource group '$ResourceGroup' deleted successfully." -ForegroundColor Green
        exit 0
    }
    Write-Host "  Still deleting... ($elapsed s)" -ForegroundColor DarkGray
}

Write-Host "Resource group deletion is still in progress after ${maxWait}s. Check Azure portal." -ForegroundColor DarkYellow
