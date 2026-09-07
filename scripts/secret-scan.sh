#!/usr/bin/env bash
# Fail the selfhosted tree if forbidden secrets/defaults are present (VERIFY-S1/S2/S3).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
fail=0
check() {
  local pattern="$1"
  local label="$2"
  if grep -R --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules --exclude-dir=dist \
      -n -E "$pattern" "$ROOT" | grep -v "scripts/secret-scan.sh" | grep -v "secret-scan.test" >/dev/null; then
    echo "FORBIDDEN: $label"
    grep -R --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules --exclude-dir=dist \
      -n -E "$pattern" "$ROOT" | grep -v "scripts/secret-scan.sh" || true
    fail=1
  fi
}

check 'Knowz_Dev_P@ssw0rd!' 'baked SQL SA password'
check 'knowz-mcp-dev-service-key' 'baked MCP service key'
check 'sh-e1cc594258136f1aea090931' 'hardcoded Playwright API key'
check 'default[[:space:]]*=[[:space:]]*"changeme"' 'terraform admin_password default'

# Credential-shaped defaults in *.tfvars.example. Deliberately path-scoped: an
# unscoped 'ChangeMe' pattern would fire on the weak-password denylist tests
# (ConfigValidatorTests.cs / AuthServiceTests.cs), which must keep their literals.
check_tfvars_example() {
  local pattern="$1"
  local label="$2"
  local hits
  hits="$(grep -R --include='*.tfvars.example' --exclude-dir=bin --exclude-dir=obj \
      --exclude-dir=node_modules --exclude-dir=dist -n -E "$pattern" "$ROOT" || true)"
  if [[ -n "$hits" ]]; then
    echo "FORBIDDEN: $label"
    echo "$hits"
    fail=1
  fi
}

check_tfvars_example 'ChangeMe' 'credential-shaped default in a *.tfvars.example (use <set-a-strong-password>)'
check_tfvars_example '(admin_password|sql_admin_password|registry_password)[[:space:]]*=[[:space:]]*"changeme"' 'weak password default in a *.tfvars.example'

if ! grep -q '^\.env$' "$ROOT/.dockerignore"; then
  echo "FORBIDDEN: .dockerignore missing .env"
  fail=1
fi
if ! grep -q 'appsettings.Local.json' "$ROOT/.dockerignore"; then
  echo "FORBIDDEN: .dockerignore missing appsettings.Local.json"
  fail=1
fi
if ! grep -q 'tfvars' "$ROOT/.dockerignore"; then
  echo "FORBIDDEN: .dockerignore missing tfvars"
  fail=1
fi

if [[ "$fail" -ne 0 ]]; then
  echo "selfhosted secret-scan FAILED"
  exit 1
fi
echo "selfhosted secret-scan OK"
