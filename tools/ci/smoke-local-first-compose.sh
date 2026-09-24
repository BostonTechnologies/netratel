#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-local-first-${GITHUB_RUN_ID:-local}-${RANDOM}"
web_port="${NETRATEL_LOCAL_FIRST_WEB_PORT:-18081}"
web_url="${NETRATEL_LOCAL_FIRST_WEB_URL:-http://127.0.0.1:${web_port}}"
curl_tls=()
if [[ "${NETRATEL_LOCAL_FIRST_IGNORE_HTTPS_ERRORS:-false}" == true ]]; then
  curl_tls=(--insecure)
fi
key_directory="$(mktemp -d)"
key_path="$key_directory/agent-auth-private.pem"
chmod 711 "$key_directory"
credential_path="$(mktemp)"
mcp_stdio_config_path="$(mktemp)"
mcp_stdio_error_path="$(mktemp)"
stage="initializing local-first Compose smoke"
bundle="${NETRATEL_LOCAL_FIRST_COMPOSE_BUNDLE:-}"
bundle_extract_dir=""
source_compose_file="${NETRATEL_LOCAL_FIRST_SOURCE_COMPOSE_FILE:-compose.yaml}"
bundle_compose_file="${NETRATEL_LOCAL_FIRST_BUNDLE_COMPOSE_FILE:-compose.images.yaml}"
compose_file="$source_compose_file"
mcp_http_image="${NETRATEL_LOCAL_HTTP_MCP_SMOKE_IMAGE:-}"
local_http_mcp_directory=""
if [[ -n "$bundle" ]]; then
  [[ -s "$bundle" ]] || { echo "NETRATEL_LOCAL_FIRST_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
  bundle_extract_dir="$(mktemp -d)"
  tar -xzf "$bundle" -C "$bundle_extract_dir"
  compose_file="$bundle_extract_dir/$bundle_compose_file"
  [[ -f "$compose_file" ]] || { echo "Release bundle is missing $bundle_compose_file." >&2; exit 1; }
fi

compose_files=(-f "$compose_file")
if [[ -n "${NETRATEL_LOCAL_FIRST_COMPOSE_OVERLAYS:-}" ]]; then
  IFS=':' read -r -a acceptance_overlays <<<"$NETRATEL_LOCAL_FIRST_COMPOSE_OVERLAYS"
  for acceptance_overlay in "${acceptance_overlays[@]}"; do
    [[ -f "$acceptance_overlay" ]] || { echo "Local-first acceptance overlay is missing: $acceptance_overlay" >&2; exit 1; }
    compose_files+=(-f "$acceptance_overlay")
  done
fi
if [[ -n "$mcp_http_image" ]]; then
  local_http_mcp_directory="$(mktemp -d)"
  local_http_mcp_overlay="$root/tests/compose/local-http-mcp.compose.yaml"
  mcp_overlay="$root/release/compose.mcp-http.yaml"
  if [[ -n "$bundle_extract_dir" ]]; then
    mcp_overlay="$bundle_extract_dir/compose.mcp-http.yaml"
  fi
  [[ -f "$local_http_mcp_overlay" && -f "$mcp_overlay" ]] || { echo "Local HTTP MCP acceptance overlays are missing." >&2; exit 1; }

  certificate_password="local-http-mcp-compose-only-password"
  api_key="$local_http_mcp_directory/api.key"
  api_certificate="$local_http_mcp_directory/api.crt"
  mcp_key="$local_http_mcp_directory/mcp.key"
  mcp_certificate="$local_http_mcp_directory/mcp.crt"
  mcp_pfx="$local_http_mcp_directory/mcp.pfx"
  mcp_config="$local_http_mcp_directory/mcp-config.json"
  m2m_secret="$(openssl rand -hex 32)"
  delegation_key="$(openssl rand -base64 48 | tr -d '\n')"

  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$api_key" -out "$api_certificate" \
    -subj '/CN=gateway' -addext 'subjectAltName=DNS:api,DNS:gateway' -addext 'basicConstraints=critical,CA:TRUE' -days 1 >/dev/null 2>&1
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$mcp_key" -out "$mcp_certificate" \
    -subj '/CN=mcp.local.test' -days 1 >/dev/null 2>&1
  openssl pkcs12 -export -out "$mcp_pfx" -inkey "$mcp_key" -in "$mcp_certificate" -passout "pass:${certificate_password}" >/dev/null 2>&1
  jq -n --arg api_base_url 'https://gateway:9443' --arg m2m_secret "$m2m_secret" \
    '{apiBaseUrl:$api_base_url,apiM2MTokenUrl:($api_base_url + "/connect/token"),apiM2MClientId:"netratel-mcp-http",apiM2MClientSecret:$m2m_secret,apiM2MScope:"netratel.api"}' > "$mcp_config"
  chmod 0644 "$api_certificate" "$api_key" "$mcp_pfx" "$mcp_config"

  export NETRATEL_MCP_HTTP_IMAGE="$mcp_http_image"
  export NETRATEL_MCP_INSTANCE=dev
  export NETRATEL_MCP_DEV_API_BASE_URL=https://gateway:9443
  export NETRATEL_MCP_PUBLIC_RESOURCE_URI=https://mcp.local.test/mcp
  export NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true
  export NETRATEL_MCP_DELEGATION_ENABLED=true
  export NETRATEL_MCP_DELEGATION_ISSUER=netratel-local-http-mcp-ci
  export NETRATEL_MCP_DELEGATION_AUDIENCE=netratel-local-http-mcp-ci
  export NETRATEL_MCP_DELEGATION_KEY_ID=netratel-local-http-mcp-ci
  export NETRATEL_MCP_DELEGATION_KEY_BASE64="$delegation_key"
  export NETRATEL_LOCAL_HTTP_MCP_CERT_PASSWORD="$certificate_password"
  export NETRATEL_LOCAL_HTTP_MCP_API_CA="$api_certificate"
  export NETRATEL_LOCAL_HTTP_MCP_API_KEY="$api_key"
  export NETRATEL_LOCAL_HTTP_MCP_GATEWAY_CONFIG="$root/tests/compose/local-http-mcp-api-proxy.nginx.conf"
  export NETRATEL_LOCAL_HTTP_MCP_MCP_PFX="$mcp_pfx"
  export NETRATEL_LOCAL_HTTP_MCP_CONFIG="$mcp_config"
  export NETRATEL_LOCAL_HTTP_MCP_M2M_SECRET="$m2m_secret"
  compose_files+=(-f "$mcp_overlay" -f "$local_http_mcp_overlay")
fi
compose=(docker compose --project-name "$project" "${compose_files[@]}")

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=Local-first Compose smoke failed::${stage}" >&2
    "${compose[@]}" ps --all >&2 || true
    local log_services=(migrations api web)
    if [[ -n "$mcp_http_image" ]]; then log_services+=(mcp-http); fi
    "${compose[@]}" logs --no-color --tail 250 "${log_services[@]}" >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "${project}_api-data" "${project}_web-keys" >/dev/null 2>&1 || true
  find "$key_directory" -depth -delete 2>/dev/null || true
  unlink "$credential_path" 2>/dev/null || true
  unlink "$mcp_stdio_config_path" 2>/dev/null || true
  if [[ -n "$bundle_extract_dir" ]]; then
    find "$bundle_extract_dir" -depth -delete 2>/dev/null || true
  fi
  if [[ -n "$local_http_mcp_directory" ]]; then
    find "$local_http_mcp_directory" -depth -delete 2>/dev/null || true
  fi
  unlink "$mcp_stdio_error_path" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
# The disposable key matches the documented non-root identity and private
# mode; its value is never emitted to logs or test output.
docker run --rm --volume "$key_directory:/keys" alpine:3.22 \
  sh -ceu 'chown 1654:1654 /keys/agent-auth-private.pem && chmod 600 /keys/agent-auth-private.pem'
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"
export NETRATEL_WEB_PORT="$web_port"

# Reproduce managed deployments that pre-create an empty root-owned named
# volume. The stock recipe must repair ownership before either non-root host
# writes its first Data Protection key; browser sign-in verifies the result.
stage="preparing root-owned fresh persistent volumes"
for volume in "${project}_api-data" "${project}_web-keys"; do
  docker volume create "$volume" >/dev/null
  docker run --rm --volume "$volume:/target" alpine:3.22 \
    sh -ceu 'chown 0:0 /target && chmod 700 /target'
done

stage="starting selected local-first Compose profile"
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
  if curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null; then
    break
  fi
  sleep 1
done
curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null

stage="building and running Playwright local-first journey"
dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
playwright_script="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1"
[[ -f "$playwright_script" ]] || { echo "Playwright install script was not produced." >&2; exit 1; }
pwsh "$playwright_script" install --with-deps chromium
stage="validating image operator commands and one-time proof rotation"
"${compose[@]}" exec -T api dotnet NetRatel.API.dll --help | grep -Fq -- '--show-setup-code'
initial_setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
[[ "$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --show-setup-code)" == "$initial_setup_proof" ]]
initial_status="$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --setup-status)"
grep -Fq 'Setup code: Available' <<<"$initial_status"
[[ "$initial_status" != *"$initial_setup_proof"* ]]
"${compose[@]}" exec -T api dotnet NetRatel.API.dll --rotate-setup-code >/dev/null
setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
[[ -n "$setup_proof" && "$setup_proof" != "$initial_setup_proof" ]]
[[ "$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --show-setup-code)" == "$setup_proof" ]]
forged_origin_status="$(curl "${curl_tls[@]}" --silent --output /dev/null --write-out '%{http_code}' \
  --header 'Origin: https://forged.invalid' --header 'Content-Type: application/json' \
  --data '{"proof":"invalid"}' "$web_url/api/v2/setup/claim")"
[[ "$forged_origin_status" == 403 ]] || { echo "A forged setup origin was not rejected." >&2; exit 1; }
if [[ "$web_url" == https://* ]]; then
  forged_host_status="$(curl "${curl_tls[@]}" --silent --output /dev/null --write-out '%{http_code}' \
    --header 'Host: forged.invalid' "$web_url/setup")"
  [[ "$forged_host_status" == 400 ]] || { echo "A forged public Host was not rejected." >&2; exit 1; }
fi
unset initial_setup_proof initial_status
stage="building and running Playwright local-first journey"
setup_proof_digest="$(printf '%s' "$setup_proof" | sha256sum | cut -d ' ' -f 1)"
NETRATEL_LOCAL_FIRST_WEB_URL="$web_url" \
NETRATEL_LOCAL_FIRST_SETUP_PROOF="$setup_proof" \
NETRATEL_LOCAL_FIRST_ADMIN_EMAIL="browser-admin@example.test" \
NETRATEL_LOCAL_FIRST_ADMIN_PASSWORD="browser smoke local passphrase" \
NETRATEL_LOCAL_FIRST_INTEGRATION_CREDENTIALS_FILE="$credential_path" \
  dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
    --configuration Release --no-build --filter 'FullyQualifiedName~LocalFirstComposeBrowserSmokeTests' \
    --results-directory TestResults/local-first -- --report-trx --report-trx-filename local-first-browser.trx
unset setup_proof

stage="verifying Ready operator status and ordinary restart"
ready_status="$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --setup-status)"
grep -Fq 'Installation: Ready' <<<"$ready_status"
grep -Fq 'Setup code: Completed' <<<"$ready_status"
if "${compose[@]}" exec -T api dotnet NetRatel.API.dll --show-setup-code >/dev/null 2>&1; then
  echo "A configured installation unexpectedly returned a setup code." >&2
  exit 1
fi
"${compose[@]}" restart api web >/dev/null
for _ in $(seq 1 90); do
  if curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e '.isReady == true' >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e '.isReady == true' >/dev/null

if [[ -n "$mcp_http_image" ]]; then
  stage="waiting for the paired local HTTP MCP gateway"
  mapfile -t credentials < "$credential_path"
  [[ "${#credentials[@]}" -ge 4 ]] || { echo "Expected HTTP MCP integration credentials from the browser journey." >&2; exit 1; }
  mcp_url="https://127.0.0.1:9224"
  mcp_headers=(--insecure --header 'Host: mcp.local.test' --header 'Accept: application/json, text/event-stream')
  for _ in $(seq 1 90); do
    if "${compose[@]}" exec -T mcp-http curl --connect-timeout 2 --fail --silent "${mcp_headers[@]}" "$mcp_url/health/live" >/dev/null; then
      break
    fi
    sleep 1
  done
  "${compose[@]}" exec -T mcp-http curl --connect-timeout 2 --fail --silent "${mcp_headers[@]}" "$mcp_url/health/live" >/dev/null

  stage="waiting for the paired local HTTP MCP API gateway after restart"
  for _ in $(seq 1 90); do
    if "${compose[@]}" exec -T mcp-http curl --cacert /run/netratel-smoke/api-ca.crt \
      --connect-timeout 2 --max-time 5 --fail --silent \
      https://gateway:9443/api/v2/setup/status | jq -e '.isReady == true' >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
  "${compose[@]}" exec -T mcp-http curl --cacert /run/netratel-smoke/api-ca.crt \
    --connect-timeout 2 --max-time 5 --fail --silent \
    https://gateway:9443/api/v2/setup/status | jq -e '.isReady == true' >/dev/null

  mcp_response_json() {
    local response="$1" sse_payload
    sse_payload="$(sed -n 's/^data: //p' <<<"$response")"
    printf '%s' "${sse_payload:-$response}"
  }
  mcp_call() {
    local credential="$1" request="$2"
    "${compose[@]}" exec -T mcp-http curl --fail --silent --show-error "${mcp_headers[@]}" \
      --header "Authorization: Bearer ${credential}" \
      --header 'Content-Type: application/json' \
      --data "$request" "$mcp_url/mcp"
  }

  stage="initializing the local HTTP MCP session without an identity provider"
  initialize_response="$(mcp_call "${credentials[2]}" '{"jsonrpc":"2.0","id":10,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"local-first-paired-smoke","version":"1"}}}')"
  jq -e '.id == 10 and .result.serverInfo.name == "NetRatel.Mcp.Http"' <<<"$(mcp_response_json "$initialize_response")" >/dev/null || {
    echo "Local HTTP MCP initialization did not return the expected host identity." >&2
    exit 1
  }

  stage="reading the real API system route through paired local HTTP MCP credentials"
  permitted_response="$(mcp_call "${credentials[2]}" '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"netratel_system","arguments":{"operation":"version"}}}')"
  jq -e '.id == 11 and .result.structuredContent.success == true' <<<"$(mcp_response_json "$permitted_response")" >/dev/null || {
    echo "Paired local HTTP MCP credential did not complete the API-backed system read." >&2
    exit 1
  }

  stage="rejecting an HTTP MCP credential without the instance discovery grant"
  denied_response="$(mcp_call "${credentials[3]}" '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"netratel_system","arguments":{"operation":"version"}}}')"
  jq -e '(.id == 12) and ([.. | strings?] | any(test("not authorized"; "i")))' <<<"$(mcp_response_json "$denied_response")" >/dev/null || {
    echo "Unscoped local HTTP MCP credential was not rejected with the safe authorization failure." >&2
    exit 1
  }
fi

if [[ -n "${NETRATEL_CLI_SMOKE_ARCHIVE:-}" ]]; then
  stage="validating extracted CLI with local integration credentials"
  [[ -s "$NETRATEL_CLI_SMOKE_ARCHIVE" ]] || { echo "CLI smoke archive is missing." >&2; exit 1; }
  mapfile -t credentials < "$credential_path"
  [[ "${#credentials[@]}" -ge 2 ]] || { echo "Expected API integration credentials." >&2; exit 1; }
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
  [[ "${#credentials[@]}" -ge 2 ]] || { echo "Expected API integration credentials." >&2; exit 1; }
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

if [[ "${NETRATEL_LOCAL_FIRST_STATE_RESET_ACCEPTANCE:-false}" == true ]]; then
  [[ -z "${NETRATEL_EXTERNAL_DATABASE_CONNECTION_STRING:-}" ]] || {
    echo "State-reset acceptance requires the disposable bundled PostgreSQL profile." >&2
    exit 1
  }

  stage="verifying partial database loss enters recovery"
  "${compose[@]}" down --remove-orphans >/dev/null
  docker volume rm "${project}_postgres-data" >/dev/null
  "${compose[@]}" up --detach >/dev/null
  recovered=false
  for _ in $(seq 1 90); do
    if curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" \
      | jq -e '.isRecoveryRequired == true and .isReady == false' >/dev/null 2>&1; then
      recovered=true
      break
    fi
    sleep 1
  done
  [[ "$recovered" == true ]] || { echo "A retained bootstrap descriptor accepted an empty replacement database." >&2; exit 1; }
  set +e
  recovery_status="$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --setup-status)"
  recovery_exit=$?
  set -e
  [[ "$recovery_exit" == 5 ]]
  grep -Fq 'Setup code: Recovery' <<<"$recovery_status"

  stage="verifying a complete disposable reset starts a new installation"
  "${compose[@]}" down --volumes --remove-orphans >/dev/null
  for volume in "${project}_postgres-data" "${project}_api-data" "${project}_web-keys"; do
    if docker volume inspect "$volume" >/dev/null 2>&1; then docker volume rm "$volume" >/dev/null; fi
  done
  docker run --rm --volume "$key_directory:/keys" alpine:3.22 \
    sh -ceu 'unlink /keys/agent-auth-private.pem'
  openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
  docker run --rm --volume "$key_directory:/keys" alpine:3.22 \
    sh -ceu 'chown 1654:1654 /keys/agent-auth-private.pem && chmod 600 /keys/agent-auth-private.pem'
  "${compose[@]}" up --detach >/dev/null
  fresh=false
  for _ in $(seq 1 90); do
    if curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" \
      | jq -e '.setupRequired == true and .isReady == false' >/dev/null 2>&1; then
      fresh=true
      break
    fi
    sleep 1
  done
  [[ "$fresh" == true ]] || { echo "A full disposable reset did not produce a fresh setup state." >&2; exit 1; }
  reset_setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
  [[ -n "$reset_setup_proof" && "$(printf '%s' "$reset_setup_proof" | sha256sum | cut -d ' ' -f 1)" != "$setup_proof_digest" ]]
  NETRATEL_LOCAL_FIRST_WEB_URL="$web_url" \
  NETRATEL_LOCAL_FIRST_SETUP_PROOF="$reset_setup_proof" \
  NETRATEL_LOCAL_FIRST_ADMIN_EMAIL="browser-admin@example.test" \
  NETRATEL_LOCAL_FIRST_ADMIN_PASSWORD="browser smoke local passphrase" \
  NETRATEL_LOCAL_FIRST_INTEGRATION_CREDENTIALS_FILE="$credential_path" \
    dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build --filter 'FullyQualifiedName~LocalFirstComposeBrowserSmokeTests' \
      --results-directory TestResults/local-first -- --report-trx --report-trx-filename local-first-reset-browser.trx
  unset reset_setup_proof
  curl "${curl_tls[@]}" --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e '.isReady == true' >/dev/null
fi
