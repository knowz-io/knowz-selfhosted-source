#!/usr/bin/env bash
# Assert that README.md's MCP tool table advertises exactly the tools this edition runs.
#
# Contract (DOC_SelfHostedLimitedEditionContract VERIFY-D3/D4/D6):
#   advertised = every [McpServerTool] name in src/Knowz.MCP/Tools/KnowzProxyTools.cs
#                minus the frozen self-hosted hidden set below.
# The prose count in the README must equal the number of advertised rows.
# Exit 0 on exact match; non-zero with the symmetric difference printed otherwise.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS_SRC="$ROOT/src/Knowz.MCP/Tools/KnowzProxyTools.cs"
README="$ROOT/README.md"
EXPECTED_DECLARED_COUNT=36

# Frozen hidden set — tools the self-hosted API cannot serve (graph + todos + document
# windowing + async amend). Kept in lockstep with MCP_SelfHostedToolContract's hidden-set
# constant; if the two ever disagree, the runtime coverage test wins and the doc is wrong.
HIDDEN=(
  graph_query
  list_todos
  get_todo_summary
  create_todo
  update_todo_status
  inspect_document_map
  get_document_window
  search_document_text
  amend_knowledge_async
  get_amend_request_status
)

fail=0
err() { echo "FAIL: $*" >&2; fail=1; }

[ -f "$TOOLS_SRC" ] || { echo "FAIL: missing $TOOLS_SRC" >&2; exit 2; }
[ -f "$README" ]    || { echo "FAIL: missing $README" >&2; exit 2; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# 1. Derive the declared tool set from source.
grep -oE '\[McpServerTool[^]]*Name[[:space:]]*=[[:space:]]*"[a-z0-9_]+"' "$TOOLS_SRC" \
  | grep -oE '"[a-z0-9_]+"' | tr -d '"' | sort -u > "$tmp/declared"
declared_count=$(wc -l < "$tmp/declared" | tr -d ' ')

if [ "$declared_count" -ne "$EXPECTED_DECLARED_COUNT" ]; then
  err "KnowzProxyTools.cs declares $declared_count [McpServerTool] methods, expected $EXPECTED_DECLARED_COUNT."
  echo "      A tool was added or removed upstream. Re-derive the advertised list, update the" >&2
  echo "      hidden set if the new tool cannot run self-hosted, then bump EXPECTED_DECLARED_COUNT." >&2
fi

# 2. Hidden set must be exactly the 10 frozen names and every one must exist in source.
if [ "${#HIDDEN[@]}" -ne 10 ]; then
  err "hidden set has ${#HIDDEN[@]} entries, expected the 10 frozen names."
fi
printf '%s\n' "${HIDDEN[@]}" | sort -u > "$tmp/hidden"
while read -r h; do
  grep -qx "$h" "$tmp/declared" || err "hidden tool '$h' is not declared in KnowzProxyTools.cs."
done < "$tmp/hidden"

# 3. Expected advertised set = declared - hidden.
comm -23 "$tmp/declared" "$tmp/hidden" > "$tmp/expected"
expected_count=$(wc -l < "$tmp/expected" | tr -d ' ')

# 4. Actual advertised set = backticked tool names inside the README MCP tool tables.
awk '/^### Available Tools/{on=1} on&&/^## /{on=0} on' "$README" \
  | grep -oE '`[a-z0-9_]+`' | tr -d '`' | sort -u > "$tmp/readme_all"
# Only names that are real tools (the section also backticks non-tool words).
comm -12 "$tmp/readme_all" "$tmp/declared" > "$tmp/actual"
actual_count=$(wc -l < "$tmp/actual" | tr -d ' ')

missing="$(comm -23 "$tmp/expected" "$tmp/actual")"
extra="$(comm -13 "$tmp/expected" "$tmp/actual")"
if [ -n "$missing" ]; then
  err "README is missing advertised tools:"; echo "$missing" | sed 's/^/       - /' >&2
fi
if [ -n "$extra" ]; then
  err "README advertises tools that are hidden on this edition:"; echo "$extra" | sed 's/^/       + /' >&2
fi

# 5. Prose count must be derived from the list, stated once.
prose_counts="$(grep -oE 'exposes [0-9]+ tools' "$README" | grep -oE '[0-9]+')"
prose_n=$(printf '%s\n' "$prose_counts" | grep -c '[0-9]' || true)
if [ "$prose_n" -ne 1 ]; then
  err "expected exactly one 'exposes N tools' statement in README, found $prose_n."
elif [ "$prose_counts" -ne "$expected_count" ]; then
  err "README prose says $prose_counts tools but the advertised list has $expected_count rows."
fi

# 5b. EVERY numeric tool claim anywhere in the README must equal the advertised count --
# the ASCII architecture diagram and the project-structure tree both used to carry a
# hand-maintained number that nothing checked.
grep -noE '[0-9]+ tools?\b' "$README" | while IFS=: read -r ln hit; do
  n="${hit%% *}"
  if [ "$n" -ne "$expected_count" ]; then
    echo "FAIL: README:$ln claims '$hit' but the advertised list has $expected_count rows." >&2
    echo "$ln" >> "$tmp/countfail"
  fi
done
[ -s "$tmp/countfail" ] && fail=1

# 6. No hidden tool may appear anywhere in the README.
while read -r h; do
  if grep -qE "\`$h\`" "$README"; then
    err "hidden tool '$h' is mentioned in README."
  fi
done < "$tmp/hidden"

# 7. The retired 'Collaboration & tasks' heading must be gone.
if grep -q 'Collaboration & tasks' "$README"; then
  err "README still uses the 'Collaboration & tasks' heading (tasks are not in this edition)."
fi

if [ "$fail" -eq 0 ]; then
  echo "OK: README advertises exactly $actual_count of $declared_count MCP tools (${#HIDDEN[@]} hidden)."
fi
exit "$fail"
