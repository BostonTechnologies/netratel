#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_ledger="$repo_root/docs/mcp-http/capability-parity.json"
projection="$repo_root/docs/mcp-http/operation-evidence-ledger.jsonl"
transform="$repo_root/scripts/mcp-parity-to-jsonl.jq"
projection_candidate="$(mktemp)"
trap 'rm -f "$projection_candidate"' EXIT

jq -e '
  .ledgerVersion == 5 and
  .catalogRevision == "2026-08-30.41" and
  (.operations | length) == .operationCount and
  ([.operations[] | .tool + "\u001f" + .operation + "\u001f" + .endpoint] | length) ==
    ([.operations[] | .tool + "\u001f" + .operation + "\u001f" + .endpoint] | unique | length)
' "$source_ledger" > /dev/null

jq -c -f "$transform" "$source_ledger" > "$projection_candidate"
cmp -s "$projection_candidate" "$projection"

jq -s -e '
  length == 193 and
  all(.[];
    .recordVersion == 5 and
    has("tool") and has("operation") and has("http") and has("stdio") and
    has("cli") and has("agentClient") and has("apiRoute") and
    has("requiredOAuthScopes") and has("policy") and has("confirmation") and
    has("idempotency") and has("availability") and has("schemas") and
    has("evidence") and has("documentation") and has("issue") and
    has("disposition") and has("sourceState") and has("sensitivity") and
    (.requiredOAuthScopes | type == "array") and
    (.requiredOAuthScopes | all(.[]; . == "netratel.mcp.read" or . == "netratel.mcp.observe" or . == "netratel.mcp.files" or . == "netratel.mcp.write" or . == "netratel.mcp.execute" or . == "netratel.mcp.onboarding" or . == "netratel.mcp.admin")) and
    (.availability | has("development") and has("production")) and
    (.evidence | has("deterministic") and has("live") and has("negative"))
  )
' "$projection" > /dev/null
