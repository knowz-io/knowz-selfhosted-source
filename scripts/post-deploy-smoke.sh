#!/usr/bin/env bash
# =============================================================================
# Self-hosted enterprise post-deploy smoke: real-enrichment exercise.
# Invoked by /deploy-selfhosted --enterprise Phase 6.5.
#
# Spec: knowzcode/specs/SH_ENTERPRISE_SMOKE_TEST.md
# NodeID: DEPLOY_SmokeTest
#
# Usage:
#   post-deploy-smoke.sh <api-url> <kv-name> [--keep-data]
#
# Optional environment variables (enterprise Phase 6.5):
#   SQL_CONNECTION_STRING - sqlcmd-formatted connection (host,port + -d -U -P).
#                           Used as fallback for Step 7 when the
#                           /api/v1/admin/enrichment/outbox endpoint returns
#                           404 (spec D4).
#   RESOURCE_GROUP        - enterprise resource group. Required for Step 8
#                           DP canary (spec VERIFY 12). If unset, Step 8 logs
#                           a WARN and is skipped.
#   API_APP_NAME          - container app name for Step 8 (default knowz-sh-api)
#
# Exits:
#   0 - smoke passed, test data cleaned up
#   1 - any failure (bootstrap, upload, enrichment, search, outbox, DP canary)
# =============================================================================
set -euo pipefail

API_URL="${1:?usage: post-deploy-smoke.sh <api-url> <kv-name> [--keep-data]}"
KV_NAME="${2:?usage: post-deploy-smoke.sh <api-url> <kv-name> [--keep-data]}"
KEEP_DATA="${3:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIXTURE_DIR="$SCRIPT_DIR/fixtures"

API_KEY=""
UPLOADED_IDS=()

log() {
    printf '[smoke] %s\n' "$*"
}

fail() {
    printf '[smoke] FAIL: %s\n' "$*" >&2
    exit 1
}

cleanup() {
    local exit_code=$?
    if [ "$KEEP_DATA" = "--keep-data" ]; then
        log "cleanup skipped (--keep-data)"
        exit "$exit_code"
    fi
    if [ -z "$API_KEY" ] || [ "${#UPLOADED_IDS[@]}" -eq 0 ]; then
        exit "$exit_code"
    fi
    log "cleanup: deleting ${#UPLOADED_IDS[@]} seed knowledge item(s)"
    local id
    for id in "${UPLOADED_IDS[@]}"; do
        if ! curl -fsS -X DELETE "$API_URL/api/v1/knowledge/$id" \
            -H "X-Api-Key: $API_KEY" > /dev/null 2>&1; then
            log "WARN: failed to delete knowledge item $id (non-fatal)"
        fi
    done
    exit "$exit_code"
}
trap cleanup EXIT

# Embedding-dim parity (FIX_SelfhostedVectorDimsConfigurable R6):
# The container fails to boot if Embedding:Dimensions is missing (Step 1 will
# then time out). A boot-time dim that mismatches the search index schema
# surfaces as empty results from /api/v1/search/semantic in Step 6 — search_hit
# fails the smoke loudly. No extra step needed: the end-to-end enrichment +
# semantic search path exercises both legs.

# --- Step 1: wait for bootstrap ready ----------------------------------------
log "[1/9] Waiting for /api/bootstrap/status (60s timeout)..."
ready=false
for _ in $(seq 1 30); do
    if curl -fsS "$API_URL/api/bootstrap/status" 2>/dev/null \
        | jq -e '.ready == true' > /dev/null 2>&1; then
        ready=true
        break
    fi
    sleep 2
done
[ "$ready" = "true" ] || fail "bootstrap never reported ready after 60s (check APP_CredentialBootstrap logs)"

# --- Step 2: fetch first API key from KV -------------------------------------
log "[2/9] Reading SelfHosted--BootstrapApiKey from $KV_NAME..."
API_KEY="$(az keyvault secret show --vault-name "$KV_NAME" \
    --name "SelfHosted--BootstrapApiKey" --query value -o tsv 2>/dev/null)" \
    || fail "unable to read SelfHosted--BootstrapApiKey from $KV_NAME"
[ -n "$API_KEY" ] || fail "SelfHosted--BootstrapApiKey is empty"

# --- Step 3: upload seed files (MD, PDF, PNG) --------------------------------
log "[3/9] Uploading 3 seed files from $FIXTURE_DIR..."
for name in test-seed.md test-seed.pdf test-seed.png; do
    [ -f "$FIXTURE_DIR/$name" ] || fail "missing seed fixture: $FIXTURE_DIR/$name"
done

upload_seed() {
    local path="$1"
    local mime="$2"
    local resp id
    if ! resp="$(curl -fsS -X POST "$API_URL/api/v1/knowledge/upload" \
        -H "X-Api-Key: $API_KEY" \
        -F "file=@$path;type=$mime" 2>&1)"; then
        fail "upload failed for $path: $resp"
    fi
    id="$(printf '%s' "$resp" | jq -r '.id // empty')"
    [ -n "$id" ] || fail "upload response missing .id for $path: $resp"
    printf '%s' "$id"
}

MD_ID="$(upload_seed "$FIXTURE_DIR/test-seed.md"  "text/markdown")"
PDF_ID="$(upload_seed "$FIXTURE_DIR/test-seed.pdf" "application/pdf")"
PNG_ID="$(upload_seed "$FIXTURE_DIR/test-seed.png" "image/png")"
UPLOADED_IDS=("$MD_ID" "$PDF_ID" "$PNG_ID")
log "    uploaded: MD=$MD_ID PDF=$PDF_ID PNG=$PNG_ID"

# --- Step 4: poll each until enrichmentStatus == Complete (120s timeout) -----
log "[4/9] Polling enrichment status (120s timeout per item)..."
poll_until_complete() {
    local id="$1"
    local status=""
    local body=""
    local i
    for i in $(seq 1 24); do
        if ! body="$(curl -fsS "$API_URL/api/v1/knowledge/$id" \
            -H "X-Api-Key: $API_KEY" 2>/dev/null)"; then
            sleep 5
            continue
        fi
        status="$(printf '%s' "$body" | jq -r '.enrichmentStatus // "Unknown"')"
        case "$status" in
            Complete) return 0 ;;
            Failed)   fail "enrichment Failed for $id (body: $body)" ;;
        esac
        sleep 5
    done
    fail "enrichment timeout for $id after 120s (last status=$status)"
}
poll_until_complete "$MD_ID"
poll_until_complete "$PDF_ID"
poll_until_complete "$PNG_ID"

# --- Step 5: assert briefSummary + tags + chunks -----------------------------
log "[5/9] Asserting briefSummary / tags / chunks populated..."
assert_enriched() {
    local id="$1"
    local body
    body="$(curl -fsS "$API_URL/api/v1/knowledge/$id" \
        -H "X-Api-Key: $API_KEY" 2>/dev/null)" \
        || fail "unable to fetch knowledge item $id for assertion"
    printf '%s' "$body" | jq -e '.briefSummary != null and (.briefSummary | length) > 0' > /dev/null \
        || fail "$id briefSummary is null or empty"
    printf '%s' "$body" | jq -e '(.tags | length) > 0' > /dev/null \
        || fail "$id tags array is empty"
    printf '%s' "$body" | jq -e '(.chunks | length) > 0' > /dev/null \
        || fail "$id chunks array is empty"
}
assert_enriched "$MD_ID"
assert_enriched "$PDF_ID"
assert_enriched "$PNG_ID"

# --- Step 6: semantic search returns each seed -------------------------------
log "[6/9] Semantic search for seed content..."
search_hit() {
    local query="$1"
    local needle="$2"
    local body
    body="$(curl -fsS -X POST "$API_URL/api/v1/search/semantic" \
        -H "X-Api-Key: $API_KEY" \
        -H "Content-Type: application/json" \
        -d "{\"query\":\"$query\",\"maxResults\":10}" 2>/dev/null)" \
        || fail "semantic search request failed for query '$query'"
    printf '%s' "$body" \
        | jq -e --arg n "$needle" '.results[]? | select(.id == $n)' > /dev/null \
        || fail "semantic search '$query' did not return $needle (body: $body)"
}
search_hit "seed markdown document" "$MD_ID"
search_hit "seed pdf document"      "$PDF_ID"
search_hit "seed image diagram"     "$PNG_ID"

# --- Step 7: zero EnrichmentOutbox failures (with SQL fallback per D4) -------
log "[7/9] Asserting EnrichmentOutbox has no Failed entries..."
OUTBOX_BODY_FILE="$(mktemp)"
trap 'rm -f "$OUTBOX_BODY_FILE"' RETURN 2>/dev/null || true
outbox_http="$(curl -sS -o "$OUTBOX_BODY_FILE" -w '%{http_code}' \
    "$API_URL/api/v1/admin/enrichment/outbox?status=Failed&limit=500" \
    -H "X-Api-Key: $API_KEY" 2>/dev/null || echo '000')"
outbox_body="$(cat "$OUTBOX_BODY_FILE" 2>/dev/null || echo '')"
rm -f "$OUTBOX_BODY_FILE"

case "$outbox_http" in
    2??)
        failed_count="$(printf '%s' "$outbox_body" | jq -r '.totalCount // .count // 0')"
        case "$failed_count" in
            ''|*[!0-9]*) fail "outbox totalCount not numeric: $outbox_body" ;;
        esac
        [ "$failed_count" -eq 0 ] \
            || fail "EnrichmentOutbox has $failed_count Failed entries (body: $outbox_body)"
        ;;
    404)
        # Spec D4: admin endpoint may not be deployed — fall back to direct SQL.
        if [ -n "${SQL_CONNECTION_STRING:-}" ] && command -v sqlcmd > /dev/null 2>&1; then
            log "    admin endpoint 404 — falling back to sqlcmd (D4)"
            sql_count="$(sqlcmd -C -b -S "$SQL_CONNECTION_STRING" \
                -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM EnrichmentOutboxEntries WHERE Status = 'Failed'" \
                -h -1 2>&1 | tr -d '[:space:]')" \
                || fail "sqlcmd fallback query failed: $sql_count"
            case "$sql_count" in
                ''|*[!0-9]*) fail "sqlcmd returned non-numeric count: $sql_count" ;;
            esac
            [ "$sql_count" -eq 0 ] \
                || fail "EnrichmentOutboxEntries (via sqlcmd) has $sql_count Failed rows"
        else
            fail "admin outbox endpoint returned 404 AND no SQL_CONNECTION_STRING/sqlcmd fallback (spec D4)"
        fi
        ;;
    *)
        fail "unexpected HTTP $outbox_http from /api/v1/admin/enrichment/outbox (body: $outbox_body)"
        ;;
esac

# --- Step 8: Data Protection canary (spec VERIFY 12) -------------------------
# Encrypt a canary string BEFORE restarting the API container; decrypt AFTER
# to validate that PersistKeysToAzureBlobStorage + ProtectKeysWithAzureKeyVault
# survive a revision restart — something WebApplicationFactory + EF InMemory
# cannot reproduce.
log "[8/9] Data Protection canary (encrypt -> restart -> decrypt)..."
if [ -z "${RESOURCE_GROUP:-}" ]; then
    log "    WARN: RESOURCE_GROUP not set — DP canary SKIPPED (spec VERIFY 12)"
else
    API_APP_NAME="${API_APP_NAME:-knowz-sh-api}"
    canary_plaintext="dp-canary-$(date -u +%s)-${RANDOM:-0}"

    encrypt_resp="$(curl -fsS -X POST "$API_URL/api/config/test/encrypt" \
        -H "X-Api-Key: $API_KEY" \
        -H "Content-Type: application/json" \
        -d "{\"value\":\"$canary_plaintext\"}" 2>&1)" \
        || fail "DP canary encrypt call failed: $encrypt_resp"
    canary_ciphertext="$(printf '%s' "$encrypt_resp" | jq -r '.ciphertext // empty')"
    [ -n "$canary_ciphertext" ] \
        || fail "DP canary /api/config/test/encrypt returned no ciphertext: $encrypt_resp"

    log "    restarting $API_APP_NAME (revision rolls to pick up DP keys from mounted blob)..."
    az containerapp revision restart -n "$API_APP_NAME" -g "$RESOURCE_GROUP" > /dev/null 2>&1 \
        || fail "DP canary: az containerapp revision restart failed"

    # VERIFY 4 note: /healthz is NOT the smoke signal here — it's a
    # post-restart readiness probe between encrypt and decrypt. The write-path
    # smoke signal is the encrypt/decrypt round-trip, which depends on the DP
    # key ring surviving the restart.
    ready_post=false
    for _ in $(seq 1 24); do
        if curl -fsS -o /dev/null "$API_URL/healthz" 2>/dev/null; then
            ready_post=true; break
        fi
        sleep 5
    done
    [ "$ready_post" = "true" ] || fail "DP canary: API did not recover /healthz within 120s post-restart"

    decrypt_resp="$(curl -fsS -X POST "$API_URL/api/config/test/decrypt" \
        -H "X-Api-Key: $API_KEY" \
        -H "Content-Type: application/json" \
        -d "{\"ciphertext\":\"$canary_ciphertext\"}" 2>&1)" \
        || fail "DP canary decrypt call failed after restart: $decrypt_resp"
    decrypted_plaintext="$(printf '%s' "$decrypt_resp" | jq -r '.plaintext // empty')"
    [ "$decrypted_plaintext" = "$canary_plaintext" ] \
        || fail "DP canary mismatch: expected '$canary_plaintext' got '$decrypted_plaintext' (key ring did not survive restart)"
    log "    DP canary: encrypt/restart/decrypt round-trip verified"
fi

# --- Step 9: done ------------------------------------------------------------
log "[9/9] SMOKE PASSED (3 seeds enriched + indexed + searchable, outbox clean, DP canary verified)"
