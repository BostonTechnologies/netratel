#!/usr/bin/env bash
set -euo pipefail

# Optional hosted acceptance only. Reuse the source Compose, public HTTPS,
# unattended bootstrap and pairing fixtures; never publish these images.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
[[ "${GITHUB_ACTIONS:-false}" == true ]] || {
  echo "Run this disposable source-container smoke through Optional integration validation." >&2
  exit 2
}
image="${NETRATEL_HTTP_HOST_SETUP_SMOKE_IMAGE:?set the hosted source tooling image}"
temporary="$(mktemp -d)"
project="netratel-http-host-${GITHUB_RUN_ID}-${RANDOM}"
export COMPOSE_PROJECT_NAME="$project"
export NETRATEL_SETUP_VOLUME="${project}-setup"
export NETRATEL_CLIENT_VOLUME="${project}-clients"
export NETRATEL_MCP_HTTP_IMAGE="$image"
export NETRATEL_AUTHENTICATION_MODE=Local
export NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential
export NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true
export NETRATEL_ALLOW_INSECURE_LOCALHOST=false
export POSTGRES_PASSWORD=disposable-http-host-postgres-passphrase
export NETRATEL_PUBLIC_HOST=netratel.example.test
export NETRATEL_HTTPS_BIND_ADDRESS=0.0.0.0
export NETRATEL_HTTPS_PORT=443
export NETRATEL_WEB_PORT=19081
export NETRATEL_PUBLIC_ORIGIN="https://${NETRATEL_PUBLIC_HOST}"
export NETRATEL_PUBLIC_GATEWAY_ORIGIN="$NETRATEL_PUBLIC_ORIGIN"
export NETRATEL_MCP_PUBLIC_RESOURCE_URI="$NETRATEL_PUBLIC_ORIGIN/mcp"
export NETRATEL_HTTPS_CERTIFICATE="$temporary/tls.crt"
export NETRATEL_HTTPS_PRIVATE_KEY="$temporary/tls.key"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$temporary/agent.key"
export NETRATEL_SETUP_COMPOSE_FILES="compose.yaml,release/compose.public-https.yaml,release/compose.mcp-http.yaml,$temporary/fixture.yaml"
compose=(docker compose -f compose.yaml -f release/compose.public-https.yaml -f release/compose.mcp-http.yaml -f "$temporary/fixture.yaml")
setup=(docker compose -f compose.setup.yaml -f "$temporary/setup-network.yaml")
applied=("${compose[@]}" -f .netratel.http-host.yaml)
stage="preparing disposable HTTP host setup"
cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=HTTP host setup smoke failed::${stage}" >&2
    for receipt in TestResults/http-host-setup/personal-{saved,reloaded}.json; do
      if [[ -s "$receipt" ]]; then
        jq -r 'if type == "array" then . else .checks // [] end
          | .[] | "\(.name): \(.status): \(.detail // .message // "")"' "$receipt" >&2 || true
      fi
    done
    "${compose[@]}" ps --all >&2 || true
  fi
  "${applied[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "$NETRATEL_SETUP_VOLUME" "$NETRATEL_CLIENT_VOLUME" >/dev/null 2>&1 || true
  rm -f .netratel.http-host.yaml
  rm -rf "$temporary"
  return "$status"
}
[[ ! -e .netratel.http-host.yaml ]] || { echo "Refusing to replace an existing generated deployment overlay." >&2; exit 2; }
trap cleanup EXIT
chmod 755 "$temporary"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$NETRATEL_HTTPS_PRIVATE_KEY" \
  -out "$NETRATEL_HTTPS_CERTIFICATE" -subj /CN=netratel.example.test \
  -addext 'subjectAltName=DNS:netratel.example.test,IP:127.0.0.1' \
  -addext 'basicConstraints=critical,CA:TRUE' -days 1 >/dev/null 2>&1
chmod 644 "$NETRATEL_HTTPS_CERTIFICATE"
chmod 600 "$NETRATEL_HTTPS_PRIVATE_KEY"
cat /etc/ssl/certs/ca-certificates.crt "$NETRATEL_HTTPS_CERTIFICATE" > "$temporary/trust.pem"
openssl ecparam -name prime256v1 -genkey -noout -out "$NETRATEL_AGENT_AUTH_PRIVATE_KEY"
docker run --rm --volume "$temporary:/fixture" alpine:3.22 \
  sh -ceu 'chown 1654:1654 /fixture/agent.key; chmod 600 /fixture/agent.key'
cat > "$temporary/fixture.yaml" <<EOF
services:
  ingress:
    volumes:
      - "$root/release/nginx.public-https.conf:/etc/nginx/conf.d/default.conf:ro"
  mcp-http:
    extra_hosts:
      - "$NETRATEL_PUBLIC_HOST:host-gateway"
    volumes:
      - "$temporary/trust.pem:/etc/ssl/certs/ca-certificates.crt:ro"
EOF
cat > "$temporary/setup-network.yaml" <<EOF
services:
  mcp-http:
    extra_hosts:
      - "$NETRATEL_PUBLIC_HOST:host-gateway"
    volumes:
      - "$temporary/trust.pem:/etc/ssl/certs/ca-certificates.crt:ro"
EOF
curl_args=(--fail --silent --show-error --connect-timeout 2 --max-time 15 \
  --cacert "$NETRATEL_HTTPS_CERTIFICATE" --noproxy "$NETRATEL_PUBLIC_HOST" \
  --resolve "${NETRATEL_PUBLIC_HOST}:${NETRATEL_HTTPS_PORT}:127.0.0.1")
post() {
  local path="$1" body="$2"
  shift 2
  curl "${curl_args[@]}" "$@" --header 'Content-Type: application/json' \
    --data "$body" "$NETRATEL_PUBLIC_ORIGIN$path"
}
account_post() {
  post "$1" "$2" --cookie "$temporary/cookies" --header 'X-NetRatel-Account-Request: 1'
}
poll_protocol() {
  local path="$1" request="$2" expected="$3" interval="$4" response status
  local deadline=$((SECONDS + 45))
  while (( SECONDS < deadline )); do
    [[ "$interval" =~ ^[0-9]+$ ]] && (( interval >= 1 && interval <= 30 )) || {
      echo "The approval protocol returned an invalid polling interval." >&2; return 1;
    }
    (( SECONDS + interval < deadline )) || break
    sleep "$interval"
    response="$(post "$path" "$request")"
    status="$(jq -er '.status' <<<"$response")"
    if [[ "$status" == "$expected" ]]; then printf '%s' "$response"; return; fi
    case "$status" in
      pending|slow_down) interval="$(jq -er '.intervalSeconds' <<<"$response")" ;;
      *) echo "The approval protocol ended without the expected approved state ($status)." >&2; return 1 ;;
    esac
  done
  echo "The approval protocol did not complete within 45 seconds." >&2
  return 1
}
wait_state() {
  local predicate="$1" deadline=$((SECONDS + 90))
  while (( SECONDS < deadline )); do
    if curl "${curl_args[@]}" "$NETRATEL_PUBLIC_ORIGIN/api/v2/setup/status" 2>/dev/null \
      | jq -e "$predicate" >/dev/null; then return; fi
    sleep 1
  done
  echo "The disposable API did not reach the expected bootstrap state within 90 seconds." >&2
  return 1
}
wait_ready() { wait_state '.isReady == true'; }
wait_mcp() {
  local deadline=$((SECONDS + 60))
  while (( SECONDS < deadline )); do
    if "${applied[@]}" exec -T mcp-http curl --fail --silent --max-time 2 \
      http://127.0.0.1:9224/health/live >/dev/null 2>&1; then return; fi
    sleep 1
  done
  echo "The configured disposable MCP host did not become ready within 60 seconds." >&2
  return 1
}

stage="starting the source Web/API HTTPS fixture"
"${compose[@]}" up --build --detach postgres volume-init migrations api web ingress
[[ -n "$("${compose[@]}" ps -q api)" ]]
wait_state '.setupRequired == true and .isReady == false'
printf '%s\n' 'disposable HTTP host administrator passphrase' | "${compose[@]}" exec -T api \
  sh -ceu 'umask 077; cat > /var/netratel/bootstrap/http-host-password'
"${compose[@]}" exec -T \
  -e Bootstrap__Unattended__PasswordFile=/var/netratel/bootstrap/http-host-password \
  -e Bootstrap__Unattended__DisplayName='HTTP host smoke administrator' \
  -e Bootstrap__Unattended__Email=http-host-smoke@example.test \
  -e Bootstrap__Unattended__TenantName='HTTP host smoke tenant' \
  api dotnet NetRatel.API.dll --initialize-unattended >/dev/null
"${compose[@]}" restart api web >/dev/null
wait_ready
post /api/v2/local-auth/login \
  '{"email":"http-host-smoke@example.test","password":"disposable HTTP host administrator passphrase","rememberMe":false}' \
  --cookie-jar "$temporary/cookies" >/dev/null
stage="generating into the supplied persistent setup volume"
mkdir -p TestResults/http-host-setup
"${setup[@]}" run --rm --no-deps -T mcp-http setup --mode host --non-interactive \
  --instance "$NETRATEL_PUBLIC_ORIGIN" > TestResults/http-host-setup/generated.json
jq -e '.state == "configuration-written-apply-and-live-verification-pending"' TestResults/http-host-setup/generated.json >/dev/null
fingerprint="$(jq -er '.registrationFingerprint' TestResults/http-host-setup/generated.json)"
[[ "$fingerprint" =~ ^[[:xdigit:]]{64}$ ]]

stage="approving the origin-bound host generation with the disposable administrator"
capabilities="$(curl "${curl_args[@]}" "$NETRATEL_PUBLIC_ORIGIN/api/http-host-setup/v1/capabilities")"
jq -e --arg origin "$NETRATEL_PUBLIC_ORIGIN" '.protocolVersion == 1 and .publicOrigin == $origin and .localCredentialMode == true' <<<"$capabilities" >/dev/null
host_request="$(post /api/http-host-setup/v1/start "$(jq -n --arg origin "$NETRATEL_PUBLIC_ORIGIN" --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" --arg fingerprint "$fingerprint" '{publicOrigin:$origin,resource:$resource,registrationFingerprint:$fingerprint}')")"
code_request="$(jq '{userCode}' <<<"$host_request")"
account_post /api/v2/account/integration-credentials/http-host-setup/lookup "$code_request" \
  | jq -e --arg fingerprint "$fingerprint" '.registrationFingerprint == $fingerprint' >/dev/null
account_post /api/v2/account/integration-credentials/http-host-setup/approve "$code_request" >/dev/null
poll_protocol /api/http-host-setup/v1/poll "$(jq '{deviceCapability}' <<<"$host_request")" approved "$(jq -er '.intervalSeconds' <<<"$host_request")" \
  | jq -e --arg fingerprint "$fingerprint" '.status == "approved" and .registrationFingerprint == $fingerprint' >/dev/null

stage="applying the generated action without secret or YAML repair"
apply_command="$(jq -er '.result.instructions[] | select(startswith("On the deployment host, review and run: ")) | ltrimstr("On the deployment host, review and run: ")' TestResults/http-host-setup/generated.json)"
bash -euo pipefail -c "$apply_command"
wait_ready
wait_mcp
"${applied[@]}" exec -T --user 1654 api sh -ceu 'test -r /run/netratel-http-host/runtime/delegation.key; test -r /run/netratel-http-host/runtime/outbound-m2m.secret; test ! -r /run/netratel-http-host/secrets/delegation.key'
"${applied[@]}" exec -T --user 10001 mcp-http sh -ceu 'test -r /run/netratel-http-host/runtime/delegation.key; test -r /run/netratel-http-host/runtime/outbound-m2m.secret; test "$(id -u)" = 10001'

stage="obtaining a separate temporary personal MCP verification credential"
pairing="$(post /api/integration-pairing/v1/start "$(jq -n --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" '{name:"HTTP host disposable verification",purpose:1,resource:$resource}')")"
expiry="$(date -u -d '+1 hour' +'%Y-%m-%dT%H:%M:%SZ')"
account_post /api/v2/account/integration-credentials/pairing/approve \
  "$(jq -n --arg code "$(jq -r '.userCode' <<<"$pairing")" --arg expiry "$expiry" '{userCode:$code,expiresAtUtc:$expiry,grants:[{tenantId:1,permission:"telemetry.read"}],instancePermissions:["mcp.discovery.read"]}')" >/dev/null
delivery="$(poll_protocol /api/integration-pairing/v1/poll "$(jq '{deviceCapability}' <<<"$pairing")" issued "$(jq -er '.intervalSeconds' <<<"$pairing")")"
credential="$(jq -er '.delivery.secret' <<<"$delivery")"
[[ "$credential" == nrt_ic_* ]]

mcp_session=""
verification_phase=initial
redact_mcp_response() {
  # Never retain response headers, bearer credentials, or signed assertions.
  # The disposable identity facts and tool schemas are useful failure evidence.
  python3 - "$temporary/mcp-response" "$1" "$temporary/mcp-credential" <<'PY'
import json, pathlib, re, sys
credential = pathlib.Path(sys.argv[3]).read_text().strip()
value = pathlib.Path(sys.argv[1]).read_text()
if credential:
    value = value.replace(credential, "[REDACTED]")
value = re.sub(r"nrt_ic_[A-Za-z0-9_.-]+", "[REDACTED]", value)
value = re.sub(r"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[REDACTED]", value)
try:
    document = json.loads(value)
except json.JSONDecodeError:
    document = {"nonJsonResponse": value}
def redact(item):
    if isinstance(item, dict):
        return {key: "[REDACTED]" if re.search(r"^(authorization|access_?token|refresh_?token|assertion|secret|deviceCapability)$", key, re.I)
                else redact(child) for key, child in item.items()}
    if isinstance(item, list):
        return [redact(child) for child in item]
    return item
pathlib.Path(sys.argv[2]).write_text(json.dumps(redact(document), indent=2) + "\n")
PY
}
umask 077
printf '%s' "$credential" > "$temporary/mcp-credential"
mcp_call() {
  local response payload status=0 headers=()
  if [[ -n "$mcp_session" ]]; then headers=(--header "Mcp-Session-Id: $mcp_session"); fi
  response="$(post /mcp "$1" --no-fail --fail-with-body --header 'Accept: application/json, text/event-stream' \
    --header 'MCP-Protocol-Version: 2025-03-26' --header "Authorization: Bearer $credential" \
    --dump-header "$temporary/mcp-headers" "${headers[@]}")" || status=$?
  payload="$(sed -n 's/^data: *//p' <<<"$response")"
  printf '%s' "${payload:-$response}" > "$temporary/mcp-response"
  redact_mcp_response "$2"
  (( status == 0 )) || return "$status"
  printf '%s' "${payload:-$response}"
}
verify_mcp() {
  mcp_session=""
  stage="verifying ${verification_phase} authenticated MCP initialize"
  mcp_call '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"http-host-setup-smoke","version":"1"}}}' \
    "TestResults/http-host-setup/${verification_phase}-initialize.json" \
    | jq -e '.id == 1 and .result.serverInfo.name == "NetRatel.Mcp.Http"' >/dev/null
  mcp_session="$(awk 'tolower($1) == "mcp-session-id:" {gsub(/\r/, "", $2); print $2}' "$temporary/mcp-headers")"
  stage="verifying ${verification_phase} MCP initialized notification"
  mcp_call '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    "TestResults/http-host-setup/${verification_phase}-initialized.json" >/dev/null
  stage="verifying ${verification_phase} authenticated MCP tool catalog"
  mcp_call '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
    "TestResults/http-host-setup/${verification_phase}-catalog.json" \
    | jq -e '.id == 2 and any(.result.tools[]; .name == "netratel_auth")' >/dev/null
  stage="verifying ${verification_phase} netratel_auth/status through signed API delegation"
  mcp_call '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"netratel_auth","arguments":{"operation":"status"}}}' \
    "TestResults/http-host-setup/${verification_phase}-auth-status.json" \
    | jq -e '.id == 3 and (.result.isError // false) == false and .result.structuredContent.success == true
      and .result.structuredContent.data.authenticated == true
      and .result.structuredContent.data.authMode == "mcp_operator_delegation"
      and (.result.structuredContent.data.groups | type == "array" and all(.[]; type == "string" and length > 0))
      and (.result.structuredContent.data.roles | type == "array" and all(.[]; type == "string" and length > 0))' >/dev/null
}
stage="verifying authenticated catalog and a real API-backed read"
verify_mcp

stage="saving a personal client with bare HTTP setup inside the serving container"
printf '%s\n' "$credential" | "${applied[@]}" exec -T \
  -e NETRATEL_HTTP_HOST_SMOKE_ORIGIN="$NETRATEL_PUBLIC_ORIGIN" mcp-http sh -ceu '
    umask 077
    credential_file="$(mktemp)"
    result_file="$(mktemp)"
    trap '\''rm -f "$credential_file" "$result_file"'\'' EXIT
    cat > "$credential_file"
    setup_status=0
    dotnet /app/NetRatel.Mcp.Http.dll setup --non-interactive \
      --instance "$NETRATEL_HTTP_HOST_SMOKE_ORIGIN" --credential-file "$credential_file" > "$result_file" || setup_status=$?
    if grep -F -q -f "$credential_file" "$result_file"; then
      echo "Personal setup emitted credential material." >&2; exit 1
    else
      test "$?" = 1
    fi
    if [ "$setup_status" -ne 0 ]; then cat "$result_file"; exit "$setup_status"; fi
    profile="$NETRATEL_CLIENT_DIRECTORY/profiles/httpmcpclient/default.json"
    test -f "$profile"
    test "$(stat -c %a "$profile")" = 600
    for secret in "$profile".*.secret; do
      test -f "$secret"
      test "$(stat -c %a "$secret")" = 600
    done
    cat "$result_file"
  ' > TestResults/http-host-setup/personal-saved.json
jq -e '.saved == true and .transport == "HttpMcpClient"
  and .path == "/var/lib/netratel/clients/profiles/httpmcpclient/default.json"
  and (.checks | length >= 5 and all(.[]; .status == "completed"))
  and (.launch | fromjson | .credentialFile | startswith("/var/lib/netratel/clients/profiles/httpmcpclient/"))' \
  TestResults/http-host-setup/personal-saved.json >/dev/null

stage="resuming from a recreated tooling container with the equivalent /mcp input"
"${setup[@]}" run --rm --no-deps -T mcp-http setup --mode host --non-interactive \
  --instance "$NETRATEL_PUBLIC_ORIGIN/mcp/" > TestResults/http-host-setup/resumed.json
jq -e --arg fingerprint "$fingerprint" '.registrationFingerprint == $fingerprint' TestResults/http-host-setup/resumed.json >/dev/null
"${applied[@]}" up --detach --force-recreate --no-deps api web mcp-http
wait_ready
wait_mcp
verification_phase=recreated
verify_mcp
stage="reloading the personal default profile after serving-container recreation"
"${applied[@]}" exec -T mcp-http dotnet /app/NetRatel.Mcp.Http.dll doctor \
  --transport http --profile default > TestResults/http-host-setup/personal-reloaded.json
jq -e 'type == "array" and length >= 5 and all(.[]; .status == "completed")
  and (map(.name) | index("permitted read-only tool") != null)' \
  TestResults/http-host-setup/personal-reloaded.json >/dev/null
post /api/integration-pairing/v1/cancel "$(jq '{deviceCapability}' <<<"$pairing")" >/dev/null
unset credential delivery pairing host_request
jq -n --arg fingerprint "$fingerprint" --arg source "$(git rev-parse HEAD)" \
  '{sourceSha:$source,registrationFingerprint:$fingerprint,headlessGeneration:true,administratorProtocolApproval:true,generatedApply:true,authenticatedCatalogAndApiRead:true,recreatedAndResumed:true,personalProfileSavedAndReloaded:true,interactiveTerminalAcceptance:"covered separately by focused terminal fixtures",ownerDeploymentAcceptance:"pending"}' \
  > TestResults/http-host-setup/result.json
echo 'Disposable HTTP host setup, administrator approval, generated apply and resumed API-backed verification passed.'
