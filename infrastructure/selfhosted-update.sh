#!/bin/bash
# infrastructure/selfhosted-update.sh
# Updates Knowz Self-Hosted container apps to a new version.
#
# Usage:
#   ./infrastructure/selfhosted-update.sh --resource-group "rg-knowz-selfhosted"
#   ./infrastructure/selfhosted-update.sh --resource-group "rg-my-knowz" --version "0.6.0"
#   ./infrastructure/selfhosted-update.sh --resource-group "rg-my-knowz" --version "latest"
#   ./infrastructure/selfhosted-update.sh --resource-group "rg-my-knowz" --dry-run
#
# Prerequisites:
#   - Azure CLI installed and logged in (az login)
#   - Correct subscription selected (az account set --subscription "...")

set -euo pipefail

# Defaults
RESOURCE_GROUP=""
VERSION="latest"
DRY_RUN=false
SKIP_HEALTH_CHECK=false

# Colors
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
RED='\033[0;31m'
GRAY='\033[0;90m'
NC='\033[0m' # No Color

usage() {
    echo "Usage: $0 --resource-group <name> [--version <tag>] [--dry-run] [--skip-health-check]"
    echo ""
    echo "Options:"
    echo "  --resource-group, -g   (required) Azure resource group name"
    echo "  --version, -v          Container image tag (default: latest)"
    echo "  --dry-run              Preview changes without applying"
    echo "  --skip-health-check    Skip post-update health checks"
    echo "  --help, -h             Show this help"
    exit 1
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --resource-group|-g)
            RESOURCE_GROUP="$2"
            shift 2
            ;;
        --version|-v)
            VERSION="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        --skip-health-check)
            SKIP_HEALTH_CHECK=true
            shift
            ;;
        --help|-h)
            usage
            ;;
        *)
            echo "Unknown option: $1"
            usage
            ;;
    esac
done

if [[ -z "$RESOURCE_GROUP" ]]; then
    echo "Error: --resource-group is required"
    usage
fi

# Pre-flight checks
if ! command -v az &> /dev/null; then
    echo -e "${RED}Error: Azure CLI (az) not found. Install: https://learn.microsoft.com/cli/azure/install-azure-cli${NC}"
    exit 1
fi

if ! az account show &> /dev/null; then
    echo -e "${RED}Error: Not logged in to Azure. Run: az login${NC}"
    exit 1
fi

if ! command -v python3 &> /dev/null; then
    echo -e "${RED}Error: python3 required for JSON parsing${NC}"
    exit 1
fi

CURRENT_SUB=$(az account show --query name -o tsv 2>/dev/null)

echo ""
echo -e "${CYAN}========================================${NC}"
echo -e "${CYAN} Knowz Self-Hosted Update${NC}"
echo -e "${CYAN}========================================${NC}"
echo "Subscription:   $CURRENT_SUB"
echo "Resource Group: $RESOURCE_GROUP"
echo "Target Version: $VERSION"
echo ""

# Step 1: Discover container apps in the resource group
echo -e "${YELLOW}[1/4] Discovering container apps...${NC}"

apps_json=$(az containerapp list --resource-group "$RESOURCE_GROUP" \
    --query "[].{name:name, image:properties.template.containers[0].image}" \
    -o json 2>/dev/null || echo "[]")

app_count=$(echo "$apps_json" | python3 -c "import sys,json; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")

if [[ "$app_count" -eq 0 ]]; then
    echo "Error: No container apps found in resource group '$RESOURCE_GROUP'"
    exit 1
fi

# Find apps by naming convention
api_app=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if 'api' in a['name'] or 'selfhosted-api' in (a.get('image') or ''):
        print(a['name']); break
" 2>/dev/null || true)

web_app=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if 'web' in a['name'] or 'selfhosted-web' in (a.get('image') or ''):
        print(a['name']); break
" 2>/dev/null || true)

mcp_app=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if 'mcp' in a['name'] or 'selfhosted-mcp' in (a.get('image') or ''):
        print(a['name']); break
" 2>/dev/null || true)

# Get current images
api_image=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if a['name'] == '$api_app':
        print(a.get('image') or 'unknown'); break
" 2>/dev/null || echo "unknown")

web_image=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if a['name'] == '$web_app':
        print(a.get('image') or 'unknown'); break
" 2>/dev/null || echo "unknown")

mcp_image=$(echo "$apps_json" | python3 -c "
import sys, json
apps = json.load(sys.stdin)
for a in apps:
    if a['name'] == '$mcp_app':
        print(a.get('image') or 'unknown'); break
" 2>/dev/null || echo "unknown")

[[ -z "$api_app" ]] && echo -e "  ${YELLOW}Warning: API container app not found${NC}"
[[ -z "$web_app" ]] && echo -e "  ${YELLOW}Warning: Web container app not found${NC}"
[[ -z "$mcp_app" ]] && echo -e "  ${YELLOW}Warning: MCP container app not found${NC}"

echo -e "  ${GREEN}Found:${NC}"
[[ -n "$api_app" ]] && echo "    API: $api_app (current: $api_image)"
[[ -n "$web_app" ]] && echo "    Web: $web_app (current: $web_image)"
[[ -n "$mcp_app" ]] && echo "    MCP: $mcp_app (current: $mcp_image)"
echo ""

# Step 2: Build target images
echo -e "${YELLOW}[2/4] Preparing update...${NC}"

TARGET_API="ghcr.io/knowz-io/knowz-selfhosted-api:$VERSION"
TARGET_WEB="ghcr.io/knowz-io/knowz-selfhosted-web:$VERSION"
TARGET_MCP="ghcr.io/knowz-io/knowz-selfhosted-mcp:$VERSION"

echo -e "  ${GRAY}Target images:${NC}"
echo -e "  ${GRAY}  api -> $TARGET_API${NC}"
echo -e "  ${GRAY}  web -> $TARGET_WEB${NC}"
echo -e "  ${GRAY}  mcp -> $TARGET_MCP${NC}"
echo ""

if [[ "$DRY_RUN" == true ]]; then
    echo -e "${YELLOW}DRY RUN -- no changes made.${NC}"
    echo -e "${YELLOW}Would update:${NC}"
    [[ -n "$api_app" ]] && echo "  $api_app -> $TARGET_API"
    [[ -n "$web_app" ]] && echo "  $web_app -> $TARGET_WEB"
    [[ -n "$mcp_app" ]] && echo "  $mcp_app -> $TARGET_MCP"
    exit 0
fi

# Step 3: Update container apps
echo -e "${YELLOW}[3/4] Updating container apps...${NC}"

update_count=0

if [[ -n "$api_app" ]]; then
    echo -e "  ${GRAY}Updating $api_app...${NC}"
    if az containerapp update --name "$api_app" --resource-group "$RESOURCE_GROUP" --image "$TARGET_API" --output none 2>/dev/null; then
        echo -e "    ${GREEN}$api_app updated.${NC}"
        update_count=$((update_count + 1))
    else
        echo -e "    ${RED}Failed to update $api_app${NC}"
    fi
fi

if [[ -n "$web_app" ]]; then
    echo -e "  ${GRAY}Updating $web_app...${NC}"
    if az containerapp update --name "$web_app" --resource-group "$RESOURCE_GROUP" --image "$TARGET_WEB" --output none 2>/dev/null; then
        echo -e "    ${GREEN}$web_app updated.${NC}"
        update_count=$((update_count + 1))
    else
        echo -e "    ${RED}Failed to update $web_app${NC}"
    fi
fi

if [[ -n "$mcp_app" ]]; then
    echo -e "  ${GRAY}Updating $mcp_app...${NC}"
    if az containerapp update --name "$mcp_app" --resource-group "$RESOURCE_GROUP" --image "$TARGET_MCP" --output none 2>/dev/null; then
        echo -e "    ${GREEN}$mcp_app updated.${NC}"
        update_count=$((update_count + 1))
    else
        echo -e "    ${RED}Failed to update $mcp_app${NC}"
    fi
fi

echo ""

# Step 4: Health check
if [[ "$SKIP_HEALTH_CHECK" == false ]] && [[ "$update_count" -gt 0 ]]; then
    echo -e "${YELLOW}[4/4] Running health checks...${NC}"
    sleep 10  # Wait for containers to restart

    if [[ -n "$api_app" ]]; then
        api_fqdn=$(az containerapp show --name "$api_app" --resource-group "$RESOURCE_GROUP" \
            --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || true)
        if [[ -n "$api_fqdn" ]]; then
            http_code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "https://$api_fqdn/api/v1/health" 2>/dev/null || echo "000")
            # 401 means auth is working (API is healthy, requires auth) — treat as healthy
            if [[ "$http_code" -ge 200 ]] && [[ "$http_code" -lt 400 ]]; then
                echo -e "  ${GREEN}API: $http_code${NC}"
            elif [[ "$http_code" == "401" ]]; then
                echo -e "  ${GREEN}API: $http_code (auth working)${NC}"
            else
                echo -e "  ${YELLOW}API: Unhealthy or starting up (HTTP $http_code)${NC}"
            fi
        fi
    fi

    if [[ -n "$web_app" ]]; then
        web_fqdn=$(az containerapp show --name "$web_app" --resource-group "$RESOURCE_GROUP" \
            --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || true)
        if [[ -n "$web_fqdn" ]]; then
            http_code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "https://$web_fqdn/" 2>/dev/null || echo "000")
            if [[ "$http_code" -ge 200 ]] && [[ "$http_code" -lt 400 ]]; then
                echo -e "  ${GREEN}Web: $http_code${NC}"
            else
                echo -e "  ${YELLOW}Web: Unhealthy or starting up (HTTP $http_code)${NC}"
            fi
        fi
    fi

    if [[ -n "$mcp_app" ]]; then
        mcp_fqdn=$(az containerapp show --name "$mcp_app" --resource-group "$RESOURCE_GROUP" \
            --query "properties.configuration.ingress.fqdn" -o tsv 2>/dev/null || true)
        if [[ -n "$mcp_fqdn" ]]; then
            http_code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "https://$mcp_fqdn/health" 2>/dev/null || echo "000")
            if [[ "$http_code" -ge 200 ]] && [[ "$http_code" -lt 400 ]]; then
                echo -e "  ${GREEN}MCP: $http_code${NC}"
            else
                echo -e "  ${YELLOW}MCP: Unhealthy or starting up (HTTP $http_code)${NC}"
            fi
        fi
    fi
else
    echo -e "${GRAY}[4/4] Health check skipped.${NC}"
fi

# Summary
echo ""
echo -e "${CYAN}========================================${NC}"
echo -e "${CYAN} Update Complete!${NC}"
echo -e "${CYAN}========================================${NC}"
echo "Updated $update_count container app(s) to version $VERSION"
echo ""
echo -e "${GRAY}Database migrations run automatically on API restart.${NC}"
echo -e "${GRAY}If you encounter issues, check logs:${NC}"
[[ -n "$api_app" ]] && echo -e "${GRAY}  az containerapp logs show --name $api_app --resource-group $RESOURCE_GROUP --tail 50${NC}"
echo ""
