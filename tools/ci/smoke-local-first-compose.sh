#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-local-first-${GITHUB_RUN_ID:-local}-${RANDOM}"
web_port="${NETRATEL_LOCAL_FIRST_WEB_PORT:-18081}"
web_url="http://127.0.0.1:${web_port}"
key_path="$(mktemp)"
credential_path="$(mktemp)"
mcp_stdio_config_path="$(mktemp)"
mcp_stdio_error_path="$(mktemp)"
stage="initializing local-first Compose smoke"
bundle="${NETRATEL_LOCAL_FIRST_COMPOSE_BUNDLE:-}"
bundle_extract_dir=""
compose_file="compose.sqlite.yaml"
if [[ -n "$bundle" ]]; then
  [[ -s "$bundle" ]] || { echo "NETRATEL_LOCAL_FIRST_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
  bundle_extract_dir="$(mktemp -d)"
  tar -xzf "$bundle" -C "$bundle_extract_dir"
  compose_file="$bundle_extract_dir/compose.local-sqlite.yaml"
  [[ -f "$compose_file" ]] || { echo "Release bundle is missing compose.local-sqlite.yaml." >&2; exit 1; }
fi
compose=(docker compose --project-name "$project" -f "$compose_file")

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=Local-first Compose smoke failed::${stage}" >&2
    "${compose[@]}" ps --all >&2 || true
    "${compose[@]}" logs --no-color --tail 250 migrations api web >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  unlink "$key_path" 2>/dev/null || true
  unlink "$credential_path" 2>/dev/null || true
  unlink "$mcp_stdio_config_path" 2>/dev/null || true
  if [[ -n "$bundle_extract_dir" ]]; then
    find "$bundle_extract_dir" -depth -delete 2>/dev/null || true
  fi
  unlink "$mcp_stdio_error_path" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
# The disposable key is mounted read-only into an unprivileged container and is
# removed by the trap above. It is never emitted to logs or test output.
chmod 644 "$key_path"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"
export NETRATEL_WEB_PORT="$web_port"

stage="starting SQLite Compose profile"
if [[ -n "$bundle" ]]; then
  "${compose[@]}" up --detach
else
  "${compose[@]}" up --build --detach
fi

stage="waiting for migration runner"
migration_id="$("${compose[@]}" ps -a -q migrations)"
[[ -n "$migration_id" ]] || { echo "Migration container was not created." >&2; exit 1; }
migration_complete=false
for _ in $(seq 1 90); do
  status="$(docker inspect --format '{{.State.Status}}' "$migration_id")"
  if [[ "$status" == exited ]]; then
    [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]] || exit 1
    migration_complete=true
    break
  fi
  sleep 1
done
[[ "$migration_complete" == true ]] || { echo "Migration container did not complete within 90 seconds." >&2; exit 1; }

stage="waiting for restricted setup surface"
for _ in $(seq 1 90); do
  if curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null; then
    break
  fi
  sleep 1
done
curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null

stage="building and running Playwright local-first journey"
dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
playwright_script="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1"
[[ -f "$playwright_script" ]] || { echo "Playwright install script was not produced." >&2; exit 1; }
pwsh "$playwright_script" install --with-deps chromium
setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
NETRATEL_LOCAL_FIRST_WEB_URL="$web_url" \
NETRATEL_LOCAL_FIRST_SETUP_PROOF="$setup_proof" \
NETRATEL_LOCAL_FIRST_ADMIN_EMAIL="browser-admin@example.test" \
NETRATEL_LOCAL_FIRST_ADMIN_PASSWORD="browser smoke local passphrase" \
NETRATEL_LOCAL_FIRST_INTEGRATION_CREDENTIALS_FILE="$credential_path" \
  dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
    --configuration Release --no-build --filter 'FullyQualifiedName~LocalFirstComposeBrowserSmokeTests' \
    --results-directory TestResults/local-first -- --report-trx --report-trx-filename local-first-browser.trx
unset setup_proof

if [[ -n "${NETRATEL_CLI_SMOKE_ARCHIVE:-}" ]]; then
  stage="validating extracted CLI with local integration credentials"
  [[ -s "$NETRATEL_CLI_SMOKE_ARCHIVE" ]] || { echo "CLI smoke archive is missing." >&2; exit 1; }
  mapfile -t credentials < "$credential_path"
  [[ "${#credentials[@]}" == 2 ]] || { echo "Expected two one-time integration credentials." >&2; exit 1; }
  extract_dir="$(mktemp -d)"
  tar -xzf "$NETRATEL_CLI_SMOKE_ARCHIVE" -C "$extract_dir"
  cli_executable="$extract_dir/netratel-cli-linux-x64/netratel"
  [[ -x "$cli_executable" ]] || { echo "CLI archive executable is missing." >&2; exit 1; }
  permitted_log="$extract_dir/permitted.log"
  denied_log="$extract_dir/denied.log"
  set +e
  NETRATEL_CLI_API_BASE_URL="$web_url/api" NETRATEL_CLI_INTEGRATION_TOKEN="${credentials[0]}" \
    "$cli_executable" telemetry agent --tenant-id 1 --agent-id 00000000-0000-0000-0000-000000000001 >"$permitted_log" 2>&1
  permitted_status=$?
  NETRATEL_CLI_API_BASE_URL="$web_url/api" NETRATEL_CLI_INTEGRATION_TOKEN="${credentials[1]}" \
    "$cli_executable" telemetry agent --tenant-id 1 --agent-id 00000000-0000-0000-0000-000000000001 >"$denied_log" 2>&1
  denied_status=$?
  set -e
  [[ "$permitted_status" == 3 ]] || { echo "Permitted credential should reach the disposable missing target (404)." >&2; exit 1; }
  [[ "$denied_status" == 2 ]] || { echo "Out-of-scope credential should be denied (403)." >&2; exit 1; }
  ! grep -Fq "${credentials[0]}" "$permitted_log" "$denied_log"
  ! grep -Fq "${credentials[1]}" "$permitted_log" "$denied_log"
  rm -rf "$extract_dir"
fi

if [[ -n "${NETRATEL_MCP_STDIO_SMOKE_ARCHIVE:-}" ]]; then
  stage="validating extracted stdio MCP with isolated local integration configuration"
  [[ -s "$NETRATEL_MCP_STDIO_SMOKE_ARCHIVE" ]] || { echo "Stdio MCP smoke archive is missing." >&2; exit 1; }
  mapfile -t credentials < "$credential_path"
  [[ "${#credentials[@]}" == 2 ]] || { echo "Expected two one-time integration credentials." >&2; exit 1; }
  mcp_extract_dir="$(mktemp -d)"
  tar -xzf "$NETRATEL_MCP_STDIO_SMOKE_ARCHIVE" -C "$mcp_extract_dir"
  mcp_assembly="$mcp_extract_dir/netratel-mcp-linux-x64/NetRatel.Mcp.dll"
  [[ -f "$mcp_assembly" ]] || { echo "Stdio MCP archive assembly is missing." >&2; exit 1; }

  # The process receives only this absolute, owner-readable config. Poisoned
  # ambient OIDC values prove that a local integration credential never falls
  # back to another credential source when its own grant is insufficient.
  jq -n --arg api_base_url "$web_url" --arg credential "${credentials[0]}" \
    '{apiBaseUrl:$api_base_url,integrationCredential:$credential}' > "$mcp_stdio_config_path"
  chmod 600 "$mcp_stdio_config_path"
  set +e
  mcp_response="$({
    printf '%s\n' \
      '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"local-first-archive-smoke","version":"1"}}}' \
      '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
      '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"netratel_auth","arguments":{"operation":"status"}}}'
    sleep 1
  } | NETRATEL_MCP_CONFIG="$mcp_stdio_config_path" NETRATEL_MCP_INSTANCE=dev \
      NETRATEL_AGENT_CLIENT_OIDC_TOKEN_URL="http://ambient-credential.invalid/token" \
      NETRATEL_AGENT_CLIENT_OIDC_CLIENT_ID="ambient-credential" \
      NETRATEL_AGENT_CLIENT_OIDC_USERNAME="ambient-credential" \
      NETRATEL_AGENT_CLIENT_OIDC_APP_PASSWORD="ambient-credential" \
      timeout 15s dotnet "$mcp_assembly" 2>"$mcp_stdio_error_path")"
  mcp_status=$?
  set -e
  [[ "$mcp_status" == 0 ]] || { echo "Stdio MCP archive exited ${mcp_status}." >&2; exit 1; }
  jq -se '
    length == 2
    and (map(.id) | sort == [1, 2])
    and (map(select(.id == 1))[0].result.serverInfo.name == "NetRatel.Mcp")
    and (map(select(.id == 2))[0].result.structuredContent.success == false)
    and (map(select(.id == 2))[0].result.structuredContent.error.upstreamStatus == 404)
  ' <<<"$mcp_response" >/dev/null || {
    echo "Stdio MCP did not retain its configured integration-only authentication mode." >&2
    exit 1
  }
  ! grep -Fq "${credentials[0]}" <<<"$mcp_response"
  ! grep -Fq "${credentials[0]}" "$mcp_stdio_error_path"
  rm -rf "$mcp_extract_dir"
fi
