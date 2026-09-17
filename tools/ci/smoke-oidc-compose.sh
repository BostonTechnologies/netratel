#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

mode="${NETRATEL_COMPOSE_SMOKE_MODE:-source}"
case "$mode" in
  source)
    compose_args=(-f compose.yaml -f tests/compose/oidc-smoke.compose.yaml)
    compose_up_args=(--build --detach)
    ;;
  release-images)
    compose_args=(-f release/compose.images.yaml -f tests/compose/oidc-smoke.compose.yaml)
    compose_up_args=(--detach)
    export NETRATEL_API_IMAGE="${NETRATEL_API_IMAGE:-netratel-api:release-smoke}"
    export NETRATEL_MIGRATIONS_IMAGE="${NETRATEL_MIGRATIONS_IMAGE:-netratel-migrations:release-smoke}"
    export NETRATEL_WEB_IMAGE="${NETRATEL_WEB_IMAGE:-netratel-web:release-smoke}"
    ;;
  *)
    echo "NETRATEL_COMPOSE_SMOKE_MODE must be source or release-images." >&2
    exit 2
    ;;
esac

project="netratel-oidc-smoke-${GITHUB_RUN_ID:-local}-${RANDOM}"
key_path="$(mktemp)"
cookie_jar="$(mktemp)"
tls_key_path="$(mktemp)"
tls_certificate_path="$(mktemp)"
tls_bundle_path="$(mktemp --suffix=.pfx)"
cli_extract_dir=""
mcp_stdio_extract_dir=""
mcp_stdio_config_path="$(mktemp --suffix=.json)"
mcp_stdio_error_path="$(mktemp)"
client_volume="${project}-client-state"
gateway_client="${project}-gateway-client"
stage="initializing Compose OIDC smoke"
wait_for_migrations() {
  local container_id state exit_code
  container_id="$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q migrations)"
  [[ -n "$container_id" ]] || { echo "Migration container was not created." >&2; return 1; }

  for _ in $(seq 1 60); do
    state="$(docker inspect --format '{{.State.Status}}' "$container_id")"
    if [[ "$state" == exited ]]; then
      exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container_id")"
      [[ "$exit_code" == 0 ]] || { echo "Migration container exited with ${exit_code}." >&2; return 1; }
      return 0
    fi
    sleep 1
  done

  echo "Migration container did not finish within 60 seconds." >&2
  return 1
}

wait_for_web() {
  local status
  for _ in $(seq 1 60); do
    status="$(curl --connect-timeout 2 --silent --output /dev/null --write-out '%{http_code}' "${web_url}/" || true)"
    if [[ "$status" == 200 || "$status" == 302 ]]; then
      return 0
    fi
    sleep 1
  done

  echo "The Web application did not become ready." >&2
  docker compose --project-name "$project" "${compose_args[@]}" \
    ps --all >&2 || true
  docker compose --project-name "$project" "${compose_args[@]}" \
    logs --no-color --tail 200 web api oidc >&2 || true
  return 1
}

wait_for_status() {
  local expected_status="$1" url="$2" status
  for _ in $(seq 1 60); do
    status="$(curl --connect-timeout 2 --silent --output /dev/null --write-out '%{http_code}' "$url" || true)"
    if [[ "$status" == "$expected_status" ]]; then
      return 0
    fi
    sleep 1
  done

  echo "Expected ${url} to return ${expected_status} after startup." >&2
  return 1
}

run_browser_oidc_smoke() {
  local playwright_script
  dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
  dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
  playwright_script="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1"
  [[ -f "$playwright_script" ]] || {
    echo "Playwright install script was not produced by the browser-smoke build." >&2
    return 1
  }
  pwsh "$playwright_script" install --with-deps chromium
  NETRATEL_BROWSER_SMOKE_WEB_URL="$web_url" \
    NETRATEL_BROWSER_SMOKE_USERNAME="netratel-test-operator" \
    dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build --filter 'FullyQualifiedName~OidcComposeBrowserSmokeTests'
}

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=${mode} Compose OIDC smoke failed::${stage}" >&2
  fi
  docker rm -f "$gateway_client" >/dev/null 2>&1 || true
  docker compose --project-name "$project" "${compose_args[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "$client_volume" >/dev/null 2>&1 || true
  unlink "$key_path" 2>/dev/null || true
  unlink "$cookie_jar" 2>/dev/null || true
  unlink "$tls_key_path" 2>/dev/null || true
  unlink "$tls_certificate_path" 2>/dev/null || true
  unlink "$tls_bundle_path" 2>/dev/null || true
  if [[ -n "$cli_extract_dir" ]]; then
    find "$cli_extract_dir" -depth -delete 2>/dev/null || true
  fi
  if [[ -n "$mcp_stdio_extract_dir" ]]; then
    find "$mcp_stdio_extract_dir" -depth -delete 2>/dev/null || true
  fi
  unlink "$mcp_stdio_config_path" 2>/dev/null || true
  unlink "$mcp_stdio_error_path" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

web_port="${NETRATEL_WEB_PORT:-8082}"
oidc_port="${NETRATEL_OIDC_TEST_PORT:-8080}"
export NETRATEL_WEB_PORT="$web_port"
export NETRATEL_OIDC_TEST_PORT="$oidc_port"
web_url="http://127.0.0.1:${web_port}"
oidc_resolve="host.docker.internal:${oidc_port}:127.0.0.1"

export POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-netratel-oidc-smoke-postgres}"
export OIDC_AUTHORITY="${OIDC_AUTHORITY:-https://unused.example.invalid}"
export OIDC_CLIENT_ID="${OIDC_CLIENT_ID:-unused-oidc-client}"
export OIDC_API_SCOPE="${OIDC_API_SCOPE:-unused-oidc-scope}"
export OIDC_API_AUDIENCE="${OIDC_API_AUDIENCE:-unused-oidc-audience}"
export OIDC_TOKEN_ENDPOINT="${OIDC_TOKEN_ENDPOINT:-https://unused.example.invalid/token}"
export OIDC_ADMIN_GROUP_ID="${OIDC_ADMIN_GROUP_ID:-unused-oidc-group}"
export OIDC_CLIENT_SECRET="${OIDC_CLIENT_SECRET:-unused-oidc-secret}"
client_image="${NETRATEL_CLIENT_SMOKE_IMAGE:-}"
[[ -n "$client_image" ]] || { echo "NETRATEL_CLIENT_SMOKE_IMAGE is required." >&2; exit 2; }

openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
# The API container runs as its own unprivileged UID, so this disposable key
# must be readable through the read-only bind mount. The key exists only for
# this isolated smoke run and is removed by cleanup.
chmod 644 "$key_path"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"
export NETRATEL_SMOKE_TLS_CERT_PASSWORD="netratel-compose-only-password"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=api' \
  -keyout "$tls_key_path" -out "$tls_certificate_path" >/dev/null 2>&1
openssl pkcs12 -export -out "$tls_bundle_path" -inkey "$tls_key_path" -in "$tls_certificate_path" \
  -passout "pass:${NETRATEL_SMOKE_TLS_CERT_PASSWORD}" >/dev/null 2>&1
chmod 644 "$tls_certificate_path" "$tls_bundle_path"
export NETRATEL_SMOKE_TLS_CERT_PATH="$tls_bundle_path"
api_port="${NETRATEL_API_TEST_PORT:-9222}"
api_url="http://127.0.0.1:${api_port}"

start_gateway_client() {
  docker rm -f "$gateway_client" >/dev/null 2>&1 || true
  docker run --detach --name "$gateway_client" --network "${project}_default" \
    --volume "${client_volume}:/var/lib/netratel" \
    --volume "${tls_certificate_path}:/run/netratel-smoke/tls.crt:ro" \
    --env SSL_CERT_FILE=/run/netratel-smoke/tls.crt \
    "$client_image" --api http://api:9222 --Gateway:Endpoint=https://api:9443 \
    --Gateway:TelemetryShadowEnabled=true --Gateway:TelemetryAuthorityEnabled=true \
    --Gateway:TelemetryFastIntervalSeconds=1 --Gateway:CommandAuthorityEnabled=true \
    --Gateway:JobAuthorityEnabled=false --Gateway:ControlGatewayEnabled=false \
    --Gateway:FileGatewayEnabled=false --Gateway:LogGatewayEnabled=false \
    --Gateway:RemoteSupportGatewayEnabled=false --Gateway:TerminalGatewayEnabled=false >/dev/null
}

wait_for_gateway_sessions() {
  local gateway_logs
  for _ in $(seq 1 30); do
    gateway_logs="$(docker logs "$gateway_client" 2>&1 || true)"
    if grep -Fq 'Presence admitted.' <<<"$gateway_logs" &&
      grep -Fq 'Command gateway admitted. authority=akka.' <<<"$gateway_logs"; then
      return 0
    fi
    sleep 1
  done

  echo "Disposable Client did not establish authenticated HTTPS presence and command-gateway sessions." >&2
  docker logs "$gateway_client" >&2 || true
  return 1
}

request_operator_access_token() {
  local redirect_uri authorization_url response callback_location callback_code token_response access_token
  redirect_uri="http://127.0.0.1:65535/netratel-smoke-callback"
  authorization_url="http://host.docker.internal:${oidc_port}/default/authorize?response_type=code&client_id=netratel-smoke-client&redirect_uri=http%3A%2F%2F127.0.0.1%3A65535%2Fnetratel-smoke-callback&scope=openid%20netratel.api&state=compose-smoke-api"
  response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" \
    --cookie "$cookie_jar" --cookie-jar "$cookie_jar" "$authorization_url")"
  callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"

  if [[ -z "$callback_location" ]]; then
    response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" \
      --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
      --data-urlencode 'username=netratel-test-operator' "$authorization_url")"
    callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"
  fi

  if [[ -n "$callback_location" ]]; then
    [[ "$callback_location" == "${redirect_uri}"* ]] || {
      echo "The test OIDC provider returned an unexpected direct-token callback." >&2
      return 1
    }
    callback_code="$(node -e 'const callback = new URL(process.argv[1]); process.stdout.write(callback.searchParams.get("code") ?? "")' "$callback_location")"
  else
    callback_code="$(sed -n 's/.*name="code" value="\([^"]*\)".*/\1/p' <<<"$response" | head -n 1)"
  fi

  [[ -n "$callback_code" ]] || {
    echo "The test OIDC provider did not return an authorization code for direct API verification." >&2
    return 1
  }
  token_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" \
    --data-urlencode 'grant_type=authorization_code' \
    --data-urlencode 'client_id=netratel-smoke-client' \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "redirect_uri=${redirect_uri}" \
    --data-urlencode "code=${callback_code}" \
    "http://host.docker.internal:${oidc_port}/default/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  [[ -n "$access_token" ]] || {
    echo "The test OIDC provider did not issue a direct API access token." >&2
    return 1
  }
  printf '%s' "$access_token"
}

wait_for_command_status() {
  local access_token="$1" command_id="$2" expected_status="$3" command_state
  for _ in $(seq 1 30); do
    command_state="$(curl --silent --show-error --fail \
      --header "Authorization: Bearer ${access_token}" \
      "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/commands/${command_id}")"
    if jq -e --argjson expected "$expected_status" \
      '(.currentStatus == $expected) or (.currentStatus == "Completed" and $expected == 4) or (.currentStatus == "Cancelled" and $expected == 6)' \
      >/dev/null <<<"$command_state"; then
      return 0
    fi
    sleep 1
  done

  echo "Gateway command ${command_id} did not reach expected lifecycle status ${expected_status}." >&2
  return 1
}

wait_for_telemetry() {
  local access_token="$1" telemetry
  for _ in $(seq 1 30); do
    telemetry="$(curl --silent --show-error --fail \
      --header "Authorization: Bearer ${access_token}" \
      "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/telemetry" || true)"
    if jq -e --argjson expected_tenant "$tenant_id" --arg expected_agent "$agent_id" \
      '(.tenantId == $expected_tenant) and (.agentId | ascii_downcase == ($expected_agent | ascii_downcase)) and (.source == "akka") and (.isAuthoritative == true)' \
      >/dev/null <<<"$telemetry"; then
      return 0
    fi
    sleep 1
  done

  echo "The enrolled Client did not publish an authoritative telemetry snapshot." >&2
  return 1
}

verify_cli_archive_scoped_read() {
  local cli_archive="$1" cli_executable cli_output cli_status
  [[ -s "$cli_archive" ]] || {
    echo "NETRATEL_CLI_SMOKE_ARCHIVE must name the packaged CLI archive to verify." >&2
    return 1
  }

  cli_extract_dir="$(mktemp -d)"
  tar -xzf "$cli_archive" -C "$cli_extract_dir"
  cli_executable="$cli_extract_dir/netratel-cli-linux-x64/netratel"
  [[ -x "$cli_executable" ]] || {
    echo "Packaged CLI archive did not contain an executable netratel command." >&2
    return 1
  }

  set +e
  cli_output="$("$cli_executable" \
    --api-base-url "$api_url" \
    --token-url "http://127.0.0.1:${oidc_port}/default/token" \
    --client-id netratel-cli-smoke-client \
    --username netratel-cli-smoke \
    --app-password synthetic-compose-only-password \
    --scope netratel.api \
    tenants list 2>&1)"
  cli_status=$?
  set -e
  if (( cli_status != 0 )); then
    stage="packaged CLI exited ${cli_status}: $(head -n 1 <<<"$cli_output")"
    return 1
  fi
  jq -e --argjson expected_tenant "$tenant_id" \
    '.. | objects | select(.tenantId? == $expected_tenant)' >/dev/null <<<"$cli_output" || {
      echo "The packaged CLI did not authenticate and read the disposable tenant." >&2
      return 1
    }
}

verify_mcp_stdio_archive_scoped_read() {
  local mcp_archive="$1" mcp_assembly mcp_response mcp_status mcp_error
  [[ -s "$mcp_archive" ]] || {
    echo "NETRATEL_MCP_STDIO_SMOKE_ARCHIVE must name the packaged stdio MCP archive to verify." >&2
    return 1
  }

  mcp_stdio_extract_dir="$(mktemp -d)"
  tar -xzf "$mcp_archive" -C "$mcp_stdio_extract_dir"
  mcp_assembly="$mcp_stdio_extract_dir/netratel-mcp-linux-x64/NetRatel.Mcp.dll"
  [[ -f "$mcp_assembly" ]] || {
    echo "Packaged stdio MCP archive did not contain NetRatel.Mcp.dll." >&2
    return 1
  }

  jq -n \
    --arg api_base_url "$api_url" \
    --arg token_url "http://127.0.0.1:${oidc_port}/default/token" \
    '{apiBaseUrl:$api_base_url,oidcTokenUrl:$token_url,oidcClientId:"netratel-cli-smoke-client",oidcUsername:"netratel-mcp-smoke",oidcAppPassword:"synthetic-compose-only-password",oidcScope:"netratel.api"}' \
    > "$mcp_stdio_config_path"
  chmod 600 "$mcp_stdio_config_path"

  set +e
  mcp_response="$({
    printf '%s\n' \
      '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"compose-archive-smoke","version":"1"}}}' \
      '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
      '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"netratel_tenants","arguments":{"operation":"list"}}}'
    sleep 1
  } | NETRATEL_MCP_CONFIG="$mcp_stdio_config_path" NETRATEL_MCP_INSTANCE=dev timeout 15s dotnet "$mcp_assembly" 2>"$mcp_stdio_error_path")"
  mcp_status=$?
  set -e
  if (( mcp_status != 0 )); then
    stage="packaged stdio MCP exited ${mcp_status}: $(head -n 1 "$mcp_stdio_error_path")"
    return 1
  fi
  jq -se --argjson expected_tenant "$tenant_id" '
    length == 2
    and (map(.id) | sort == [1, 2])
    and (map(select(.id == 1))[0].result.serverInfo.name == "NetRatel.Mcp")
    and (map(select(.id == 2))[0].result.structuredContent.success == true)
    and ([map(select(.id == 2))[0].result.structuredContent | .. | objects | select(.tenantId? == $expected_tenant)] | length > 0)
  ' <<<"$mcp_response" >/dev/null || {
    mcp_error="$(jq -r 'map(select(.id == 2))[0].result.structuredContent.error.code // map(select(.id == 2))[0].error.code // "unexpected_response"' <<<"$mcp_response" 2>/dev/null || true)"
    stage="packaged stdio MCP did not authenticate and read the disposable tenant (${mcp_error:-unexpected_response})"
    return 1
  }
}

stage="starting disposable Compose services"
docker compose --project-name "$project" "${compose_args[@]}" up "${compose_up_args[@]}"
stage="waiting for migrations"
wait_for_migrations

stage="waiting for disposable OIDC issuer"
curl --retry 20 --retry-connrefused --fail --silent --show-error \
  "http://127.0.0.1:${oidc_port}/isalive" >/dev/null

stage="waiting for the Web application"
wait_for_web
stage="checking anonymous API rejection"
wait_for_status 401 "${web_url}/api/v1/tenants"
stage="running browser OIDC rehearsal"
run_browser_oidc_smoke

unauthenticated_status="$(curl --silent --output /dev/null --write-out '%{http_code}' "${web_url}/api/v1/tenants")"
[[ "$unauthenticated_status" == 401 ]] || {
  echo "Expected the protected API route to reject an anonymous request, got ${unauthenticated_status}." >&2
  exit 1
}

stage="starting interactive OIDC challenge"
authorization_location="$(curl --silent --show-error --output /dev/null --dump-header - \
  --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  "${web_url}/auth/oidc?returnUrl=%2Ftenants" \
  | awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }')"
[[ "$authorization_location" == http://host.docker.internal:${oidc_port}/default/authorize* ]] || {
  echo "The Web application did not challenge the configured generic OIDC provider." >&2
  exit 1
}

stage="loading OIDC authorization page"
curl --silent --show-error --fail --resolve "$oidc_resolve" \
  --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  "$authorization_location" >/dev/null

stage="authenticating disposable OIDC operator"
callback_response="$(curl --silent --show-error --dump-header - \
  --resolve "$oidc_resolve" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  --data-urlencode 'username=netratel-test-operator' \
  "$authorization_location")"
callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$callback_response")"

if [[ -n "$callback_location" ]]; then
  [[ "$callback_location" == "${web_url}/signin-oidc"* ]] || {
    echo "The generic OIDC provider returned an unexpected authorization-code callback: ${callback_location}." >&2
    exit 1
  }

  authenticated_status="$(curl --silent --show-error \
    --resolve "$oidc_resolve" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    --output /dev/null --write-out '%{http_code}' "$callback_location")"
else
  callback_action="$(sed -n 's/.*<form action="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  callback_code="$(sed -n 's/.*name="code" value="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  callback_state="$(sed -n 's/.*name="state" value="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  [[ "$callback_action" == "${web_url}/signin-oidc" && -n "$callback_code" && -n "$callback_state" ]] || {
    echo "The generic OIDC provider did not return a usable authorization-code callback." >&2
    exit 1
  }

  authenticated_status="$(curl --silent --show-error \
    --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    --data-urlencode "code=$callback_code" --data-urlencode "state=$callback_state" \
    --output /dev/null --write-out '%{http_code}' "$callback_action")"
fi
[[ "$authenticated_status" == 302 ]] || {
  echo "OIDC callback did not establish a Web session, got ${authenticated_status}." >&2
  docker compose --project-name "$project" "${compose_args[@]}" \
    logs --no-color --tail 200 web >&2 || true
  exit 1
}

stage="checking authenticated API access"
protected_status="$(curl --silent --show-error --cookie "$cookie_jar" \
  --output /dev/null --write-out '%{http_code}' "${web_url}/api/v1/tenants")"
[[ "$protected_status" == 200 ]] || {
  echo "The authenticated OIDC session could not access the protected API route, got ${protected_status}." >&2
  docker compose --project-name "$project" "${compose_args[@]}" \
    logs --no-color --tail 200 web api >&2 || true
  exit 1
}

stage="creating disposable tenant"
tenant_response="$(curl --silent --show-error --fail --cookie "$cookie_jar" \
  --header 'Content-Type: application/json' \
  --data '{"name":"compose-smoke","description":"Disposable Compose smoke tenant","location":"test","domains":[],"autoUpdate":false}' \
  "${web_url}/api/v1/tenants/")"
tenant_id="$(jq -r '.tenantId // empty' <<<"$tenant_response")"
[[ "$tenant_id" =~ ^[1-9][0-9]*$ ]] || { echo "Compose smoke could not create a synthetic tenant." >&2; exit 1; }

stage="verifying packaged CLI read"
verify_cli_archive_scoped_read "${NETRATEL_CLI_SMOKE_ARCHIVE:-}"
stage="verifying packaged stdio MCP read"
verify_mcp_stdio_archive_scoped_read "${NETRATEL_MCP_STDIO_SMOKE_ARCHIVE:-}"

stage="issuing disposable Client enrollment"
enrollment_response="$(curl --silent --show-error --fail --cookie "$cookie_jar" \
  --header 'Content-Type: application/json' \
  --data '{"validForMinutes":5,"maxUses":1,"note":"Disposable Compose smoke enrollment"}' \
  "${web_url}/api/v1/tenants/${tenant_id}/enrollment-codes")"
enrollment_code="$(jq -r '.enrollmentCode // empty' <<<"$enrollment_response")"
[[ -n "$enrollment_code" ]] || { echo "Compose smoke could not issue a short-lived enrollment code." >&2; exit 1; }

docker volume create "$client_volume" >/dev/null
# Docker creates named volumes as root-owned.  Prepare the disposable state
# volume once so the published image still runs under its normal unprivileged
# `netratel` account for enrollment, token renewal, and gateway presence.
docker run --rm --user 0:0 --volume "${client_volume}:/var/lib/netratel" \
  --entrypoint /bin/sh "$client_image" -c 'chown -R netratel:netratel /var/lib/netratel'
stage="enrolling the disposable Client"
docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
  "$client_image" --api http://api:9222 --enroll "$enrollment_code"
stage="validating disposable Client authentication"
set +e
auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
  "$client_image" --api http://api:9222 --auth-check 2>&1)"
auth_check_status=$?
set -e
printf '%s\n' "$auth_check_output"
if (( auth_check_status != 0 )); then
  stage="Client auth check exited ${auth_check_status}: $(grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1)"
  return 1
fi
agent_id="$(sed -nE 's/.*[Aa]gent[Ii]d=([0-9A-Fa-f-]{36}).*/\1/ip' <<<"$auth_check_output" | head -n 1)"
[[ "$agent_id" =~ ^[0-9A-Fa-f-]{36}$ ]] || {
  echo "Disposable Client auth check did not report its enrolled agent ID." >&2
  exit 1
}
echo "Disposable Client authenticated as agent ${agent_id}."

stage="establishing Client gateway sessions"
start_gateway_client
wait_for_gateway_sessions

stage="requesting direct operator token"
operator_access_token="$(request_operator_access_token)"
stage="waiting for Client telemetry"
wait_for_telemetry "$operator_access_token"
command_payload='{"command":"printf netratel-compose-smoke","timeoutSeconds":10}'
command_response="$(curl --silent --show-error --fail \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' \
  --data "$(jq -nc --arg payload "$command_payload" '{taskType:"exec-shell-cmd", payloadJson:$payload, environment:0, correlationId:"compose-smoke-harmless"}')" \
  "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/commands")"
command_id="$(jq -r '.commandId // empty' <<<"$command_response")"
[[ "$command_id" =~ ^[0-9a-f]{32}$ ]] || {
  echo "The command authority did not return a valid disposable command ID." >&2
  exit 1
}
wait_for_command_status "$operator_access_token" "$command_id" 4

cancel_payload='{"command":"sleep 20","timeoutSeconds":30}'
cancel_response="$(curl --silent --show-error --fail \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' \
  --data "$(jq -nc --arg payload "$cancel_payload" '{taskType:"exec-shell-cmd", payloadJson:$payload, environment:0, correlationId:"compose-smoke-cancel"}')" \
  "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/commands")"
cancel_command_id="$(jq -r '.commandId // empty' <<<"$cancel_response")"
[[ "$cancel_command_id" =~ ^[0-9a-f]{32}$ ]] || {
  echo "The cancellation probe did not return a valid disposable command ID." >&2
  exit 1
}
curl --silent --show-error --fail --output /dev/null \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' --data '{"reason":"compose_smoke_cancel"}' \
  "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/commands/${cancel_command_id}/cancel"
wait_for_command_status "$operator_access_token" "$cancel_command_id" 6

docker rm -f "$gateway_client" >/dev/null
start_gateway_client
wait_for_gateway_sessions
docker rm -f "$gateway_client" >/dev/null

logout_headers="$(curl --silent --show-error --dump-header - --output /dev/null \
  --cookie "$cookie_jar" --cookie-jar "$cookie_jar" "${web_url}/auth/logout")"
grep -qi '^set-cookie:.*\.AspNetCore\.Cookies=;' <<<"$logout_headers" || {
  echo "Logout did not clear the Web authentication cookie." >&2
  exit 1
}

echo "${mode} Compose generic OIDC smoke test passed."
