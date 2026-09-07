#!/usr/bin/env bash
# =============================================================================
# build-seeds.sh — thin wrapper around build-fixtures.py (spec §2.1 naming).
#
# Spec SH_ENTERPRISE_SMOKE_TEST §2.1 calls the generator "build-seeds.sh" and
# uses "seeds/" as the directory name; this repo ships the fixtures in
# selfhosted/scripts/fixtures/ with a Python generator (no pandoc /
# ImageMagick CI dependency per D1). This wrapper exists so operators running
# from the spec filename find a working entry point.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if ! command -v python > /dev/null 2>&1 && ! command -v python3 > /dev/null 2>&1; then
    echo "ERROR: python or python3 is required to (re)build seed fixtures" >&2
    exit 1
fi

PY="$(command -v python3 || command -v python)"
exec "$PY" "$SCRIPT_DIR/build-fixtures.py"
