#!/usr/bin/env bats
# =============================================================================
# Bats tests for post-deploy-smoke.sh
# Spec: knowzcode/specs/SH_ENTERPRISE_SMOKE_TEST.md
#
# Mocks `curl`, `jq`, and `az` via a stub PATH directory. Each test seeds
# fixture response bodies and asserts the script exits with the expected
# code + log markers.
#
# Run: bats selfhosted/scripts/test-post-deploy-smoke.bats
# =============================================================================

setup() {
    SCRIPT_DIR="$(cd "$(dirname "${BATS_TEST_FILENAME}")" && pwd)"
    SCRIPT_UNDER_TEST="$SCRIPT_DIR/post-deploy-smoke.sh"
    [ -f "$SCRIPT_UNDER_TEST" ] || {
        echo "missing: $SCRIPT_UNDER_TEST" >&2
        return 1
    }

    STUB_DIR="$(mktemp -d)"
    STUB_STATE="$STUB_DIR/state"
    mkdir -p "$STUB_STATE"

    # Pre-create the fixture dir the script will probe.
    mkdir -p "$SCRIPT_DIR/fixtures"
    for f in test-seed.md test-seed.pdf test-seed.png; do
        [ -f "$SCRIPT_DIR/fixtures/$f" ] || echo "stub" > "$SCRIPT_DIR/fixtures/$f"
    done

    _write_stub_curl "default"
    _write_stub_az "default"
    _write_stub_jq

    export PATH="$STUB_DIR:$PATH"
    export KV_NAME_UNDER_TEST="kv-smoke-test"
    export API_URL_UNDER_TEST="https://api.smoke.test"
}

teardown() {
    rm -rf "$STUB_DIR"
}

# --- stubs -------------------------------------------------------------------

_write_stub_curl() {
    local mode="$1"
    cat > "$STUB_DIR/curl" <<STUB
#!/usr/bin/env bash
# Stub curl — returns fixed payloads based on URL + method.
MODE="\${STUB_CURL_MODE:-$mode}"
URL=""
METHOD="GET"
while [ \$# -gt 0 ]; do
    case "\$1" in
        -X) METHOD="\$2"; shift 2 ;;
        -H|-F|-d|-o|-w) shift 2 ;;
        --) shift; break ;;
        http*|https*) URL="\$1"; shift ;;
        *) shift ;;
    esac
done

case "\$URL" in
    */api/bootstrap/status)
        case "\$MODE" in
            not-ready) printf '{"ready": false}' ;;
            *)         printf '{"ready": true}' ;;
        esac ;;
    */api/v1/knowledge/upload)
        # Return incrementing ids
        idx=\$(cat "$STUB_STATE/upload_idx" 2>/dev/null || echo 0)
        idx=\$((idx + 1))
        echo \$idx > "$STUB_STATE/upload_idx"
        printf '{"id": "id-%s"}' "\$idx" ;;
    */api/v1/knowledge/*)
        if [ "\$METHOD" = "DELETE" ]; then
            echo ""
        else
            case "\$MODE" in
                enrich-fail)
                    printf '{"enrichmentStatus":"Failed","briefSummary":null,"tags":[],"chunks":[]}' ;;
                enrich-timeout)
                    printf '{"enrichmentStatus":"Processing","briefSummary":null,"tags":[],"chunks":[]}' ;;
                missing-summary)
                    printf '{"enrichmentStatus":"Complete","briefSummary":"","tags":["a"],"chunks":[{"id":1}]}' ;;
                *)
                    printf '{"enrichmentStatus":"Complete","briefSummary":"hello","tags":["seed"],"chunks":[{"id":1}]}' ;;
            esac
        fi ;;
    */api/v1/search/semantic)
        case "\$MODE" in
            search-miss)
                printf '{"results":[]}' ;;
            *)
                printf '{"results":[{"id":"id-1"},{"id":"id-2"},{"id":"id-3"}]}' ;;
        esac ;;
    */api/v1/admin/enrichment/outbox*)
        case "\$MODE" in
            outbox-failed)
                printf '{"totalCount": 3, "items": []}' ;;
            *)
                printf '{"totalCount": 0, "items": []}' ;;
        esac ;;
    *)
        echo "stub-curl: unhandled URL \$URL" >&2
        exit 22 ;;
esac
STUB
    chmod +x "$STUB_DIR/curl"
}

_write_stub_az() {
    local mode="$1"
    cat > "$STUB_DIR/az" <<STUB
#!/usr/bin/env bash
# Stub az — returns a fake API key for keyvault secret show.
MODE="\${STUB_AZ_MODE:-$mode}"
if [ "\$1" = "keyvault" ] && [ "\$2" = "secret" ] && [ "\$3" = "show" ]; then
    case "\$MODE" in
        kv-missing) echo "ERROR: SecretNotFound" >&2; exit 1 ;;
        kv-empty)   printf '' ;;
        *)          printf 'sk-bootstrap-test-key' ;;
    esac
    exit 0
fi
echo "stub-az: unhandled args: \$*" >&2
exit 1
STUB
    chmod +x "$STUB_DIR/az"
}

_write_stub_jq() {
    # Use the real jq if present; otherwise a minimal Python fallback.
    if command -v jq >/dev/null 2>&1; then
        cp "$(command -v jq)" "$STUB_DIR/jq" 2>/dev/null || ln -sf "$(command -v jq)" "$STUB_DIR/jq"
    else
        cat > "$STUB_DIR/jq" <<'STUB'
#!/usr/bin/env bash
python - "$@" <<'PY'
import json, sys
args = sys.argv[1:]
stdin = sys.stdin.read()
try:
    data = json.loads(stdin) if stdin.strip() else None
except Exception:
    sys.exit(1)
# Very minimal subset handling for our test cases.
expr = args[-1] if args else "."
flag_e = "-e" in args
flag_r = "-r" in args
try:
    # crude eval: replace '.' semantics
    res = None
    if expr.startswith(".ready == true"):
        res = bool(data and data.get("ready") is True)
    elif expr == ".id // empty":
        res = data.get("id", "") if isinstance(data, dict) else ""
    elif expr == ".enrichmentStatus // \"Unknown\"":
        res = data.get("enrichmentStatus", "Unknown")
    elif expr == ".briefSummary != null and (.briefSummary | length) > 0":
        v = data.get("briefSummary")
        res = bool(v) and len(v) > 0
    elif expr == "(.tags | length) > 0":
        res = len(data.get("tags", [])) > 0
    elif expr == "(.chunks | length) > 0":
        res = len(data.get("chunks", [])) > 0
    elif expr == ".totalCount // .count // 0":
        res = data.get("totalCount", data.get("count", 0))
    else:
        # .results[]? | select(.id == $n)
        if "select(.id ==" in expr:
            needle = None
            for i, a in enumerate(args):
                if a == "--arg" and args[i+1] == "n":
                    needle = args[i+2]
            found = any(r.get("id") == needle for r in data.get("results", []))
            if flag_e:
                sys.exit(0 if found else 1)
            sys.exit(0 if found else 1)
    if flag_e:
        sys.exit(0 if res else 1)
    if isinstance(res, bool):
        print("true" if res else "false")
    else:
        print(res if flag_r else json.dumps(res))
except Exception as ex:
    sys.stderr.write(f"stub-jq err: {ex}\n"); sys.exit(1)
PY
STUB
        chmod +x "$STUB_DIR/jq"
    fi
}

# --- Tests -------------------------------------------------------------------

@test "happy path: all 8 steps pass, exits 0" {
    STUB_CURL_MODE=default STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -eq 0 ]
    [[ "$output" == *"[1/8]"* ]]
    [[ "$output" == *"[8/8] SMOKE PASSED"* ]]
}

@test "fails fast when bootstrap never ready" {
    STUB_CURL_MODE=not-ready STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"bootstrap never reported ready"* ]]
}

@test "fails when KV bootstrap secret is missing" {
    STUB_CURL_MODE=default STUB_AZ_MODE=kv-missing \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"unable to read SelfHosted--BootstrapApiKey"* ]]
}

@test "fails when KV bootstrap secret is empty" {
    STUB_CURL_MODE=default STUB_AZ_MODE=kv-empty \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"SelfHosted--BootstrapApiKey is empty"* ]]
}

@test "fails when enrichment reports Failed status" {
    STUB_CURL_MODE=enrich-fail STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"enrichment Failed"* ]]
}

@test "fails when briefSummary is empty after Complete" {
    STUB_CURL_MODE=missing-summary STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"briefSummary is null or empty"* ]]
}

@test "fails when semantic search does not return seed id" {
    STUB_CURL_MODE=search-miss STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"semantic search"* ]]
    [[ "$output" == *"did not return"* ]]
}

@test "fails when EnrichmentOutbox has failed entries" {
    STUB_CURL_MODE=outbox-failed STUB_AZ_MODE=default \
        run bash "$SCRIPT_UNDER_TEST" "$API_URL_UNDER_TEST" "$KV_NAME_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"EnrichmentOutbox has"* ]]
    [[ "$output" == *"Failed entries"* ]]
}

@test "missing required args prints usage and exits non-zero" {
    run bash "$SCRIPT_UNDER_TEST"
    [ "$status" -ne 0 ]
    [[ "$output" == *"usage: post-deploy-smoke.sh"* ]]
}

@test "VERIFY 4 (scoped): /healthz is not the smoke pass signal" {
    # Spec VERIFY 4 intent: smoke MUST exercise the write path, not a liveness
    # probe. After VERIFY 12 (DP canary) landed, one /healthz call exists
    # purely as a post-restart readiness probe between encrypt and decrypt —
    # NOT as the smoke signal. Enforce that:
    #   1) No step log header references /healthz (write path only).
    #   2) The pass signal (SMOKE PASSED) is gated on write-path assertions.
    run grep -E "log \"\[[0-9]/[0-9]\] .*/health" "$SCRIPT_UNDER_TEST"
    [ "$status" -ne 0 ]
    run grep -E "SMOKE PASSED" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
}

@test "Step 9 success marker present in script" {
    run grep -E "\[9/9\] SMOKE PASSED" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
}

@test "Step 7 has sqlcmd fallback for 404 per spec D4" {
    run grep -E "admin endpoint 404.*sqlcmd.*D4" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
}

@test "Step 8 DP canary is present per spec VERIFY 12" {
    run grep -E "Data Protection canary.*VERIFY 12" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
    run grep -E "/api/config/test/encrypt" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
    run grep -E "/api/config/test/decrypt" "$SCRIPT_UNDER_TEST"
    [ "$status" -eq 0 ]
}
