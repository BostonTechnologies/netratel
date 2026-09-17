#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_ledger="$repo_root/docs/mcp-http/capability-parity.json"
projection="$repo_root/docs/mcp-http/operation-evidence-ledger.jsonl"
transform="$repo_root/scripts/mcp-parity-to-jsonl.jq"

jq -c -f "$transform" "$source_ledger" > "$projection"
