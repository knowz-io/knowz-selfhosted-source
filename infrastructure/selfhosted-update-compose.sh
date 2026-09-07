#!/bin/bash
# infrastructure/selfhosted-update-compose.sh
# Updates Knowz Self-Hosted docker-compose deployment to a new version.
#
# Usage:
#   ./infrastructure/selfhosted-update-compose.sh
#   ./infrastructure/selfhosted-update-compose.sh --version 0.6.0
#   ./infrastructure/selfhosted-update-compose.sh --no-pull
#
# Prerequisites:
#   - Docker and Docker Compose installed
#   - Run from the selfhosted repository root, or let the script auto-detect

set -euo pipefail

# Defaults
VERSION="latest"
PULL_CODE=true

# Colors
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
GRAY='\033[0;90m'
NC='\033[0m'

usage() {
    echo "Usage: $0 [--version <tag>] [--no-pull]"
    echo ""
    echo "Options:"
    echo "  --version, -v   Image tag to use (default: latest)"
    echo "  --no-pull        Skip git pull (use local code as-is)"
    echo "  --help, -h       Show this help"
    exit 1
}

# Parse arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version|-v)
            VERSION="$2"
            shift 2
            ;;
        --no-pull)
            PULL_CODE=false
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

# Resolve repo directory (script is in infrastructure/, repo root is one level up)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"

echo ""
echo -e "${CYAN}=========================================${NC}"
echo -e "${CYAN} Knowz Self-Hosted Update (Docker Compose)${NC}"
echo -e "${CYAN}=========================================${NC}"
echo "Version: $VERSION"
echo "Directory: $REPO_DIR"
echo ""

# Step 1: Pull latest code (optional)
if [[ "$PULL_CODE" == true ]]; then
    echo -e "${YELLOW}[1/3] Pulling latest changes...${NC}"
    cd "$REPO_DIR"
    git pull origin main
    echo ""
else
    echo -e "${GRAY}[1/3] Skipping git pull (--no-pull)${NC}"
    echo ""
fi

# Step 2: Rebuild containers
echo -e "${YELLOW}[2/3] Rebuilding containers...${NC}"
cd "$REPO_DIR"
docker compose build
echo ""

# Step 3: Restart with new images
echo -e "${YELLOW}[3/3] Restarting services...${NC}"
docker compose up -d
echo ""

echo -e "${CYAN}=========================================${NC}"
echo -e "${CYAN} Update Complete!${NC}"
echo -e "${CYAN}=========================================${NC}"
echo ""
echo -e "${GRAY}Database migrations run automatically on startup.${NC}"
echo -e "${GRAY}View logs: docker compose logs -f api${NC}"
echo ""
