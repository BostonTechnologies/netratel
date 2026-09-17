#!/usr/bin/env bash
set -euo pipefail

image="${NETRATEL_MCP_HTTP_SMOKE_IMAGE:-}"
[[ -n "$image" ]] || { echo "NETRATEL_MCP_HTTP_SMOKE_IMAGE is required." >&2; exit 2; }
script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mcp_config="$script_root/tests/fixtures/mcp-http-smoke-config.json"
[[ -f "$mcp_config" ]] || { echo "HTTP MCP smoke configuration fixture is missing." >&2; exit 1; }
certificate_directory="$(mktemp -d)"
certificate_password="synthetic-mcp-http-smoke-certificate-password"
certificate_file="$certificate_directory/mcp-http-smoke.pfx"

openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout "$certificate_directory/mcp-http-smoke.key" \
  -out "$certificate_directory/mcp-http-smoke.crt" \
  -subj '/CN=mcp.example.invalid' \
  -days 1 >/dev/null 2>&1
openssl pkcs12 -export \
  -out "$certificate_file" \
  -inkey "$certificate_directory/mcp-http-smoke.key" \
  -in "$certificate_directory/mcp-http-smoke.crt" \
  -passout "pass:${certificate_password}" >/dev/null 2>&1
chmod 0644 "$certificate_file"

container="netratel-mcp-http-smoke-$$_${RANDOM}"
oidc_container="${container}-oidc"
network="${container}-network"
stage="initializing disposable HTTP MCP rehearsal"
cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=HTTP MCP image smoke test failed::${stage}" >&2
  fi
  docker rm -f "$oidc_container" >/dev/null 2>&1 || true
  if docker container inspect "$container" >/dev/null 2>&1; then
    if (( status != 0 )); then
      docker logs "$container" >&2 || true
    fi
    docker rm -f "$container" >/dev/null || true
  fi
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$certificate_directory"
  exit "$status"
}
trap cleanup EXIT

oidc_config='{"interactiveLogin":true,"tokenCallbacks":[{"issuerId":"default","requestMappings":[{"requestParam":"client_id","match":"netratel-mcp-smoke-client","claims":{"preferred_username":"netratel-mcp-smoke@example.test","roles":["netratel-operators"],"groups":["netratel-operators"],"scope":"netratel.mcp.read","aud":["https://mcp.example.invalid/mcp"]}},{"requestParam":"client_id","match":"netratel-mcp-wrong-scope-client","claims":{"preferred_username":"netratel-mcp-wrong-scope@example.test","roles":["netratel-operators"],"groups":["netratel-operators"],"scope":"netratel.mcp.observe","aud":["https://mcp.example.invalid/mcp"]}}]}]}'
stage="creating the disposable OIDC network"
docker network create "$network" >/dev/null
stage="starting the disposable OIDC issuer"
docker run --detach --name "$oidc_container" --network "$network" --network-alias oidc.smoke.invalid \
  --hostname oidc.smoke.invalid --publish 127.0.0.1:8080:8080 \
  --env "JSON_CONFIG=${oidc_config}" \
  ghcr.io/navikt/mock-oauth2-server@sha256:ae36f65ca23e07e8786b288e53145e7ffb191c9dccfa9868ed433e7bbacad5db >/dev/null

oidc_authority="http://oidc.smoke.invalid:8080/default"
oidc_resolve="oidc.smoke.invalid:8080:127.0.0.1"
stage="waiting for the disposable OIDC issuer"
for _ in $(seq 1 30); do
  if curl --resolve "$oidc_resolve" --fail --silent --show-error "${oidc_authority}/isalive" >/dev/null; then
    break
  fi
  sleep 1
done
curl --resolve "$oidc_resolve" --fail --silent --show-error "${oidc_authority}/isalive" >/dev/null

stage="starting the HTTPS HTTP MCP container"
docker run --detach --name "$container" --no-healthcheck --publish 127.0.0.1::9224 \
  --network "$network" \
  --mount "type=bind,source=$mcp_config,target=/run/netratel/mcp-config.json,readonly" \
  --mount "type=bind,source=$certificate_file,target=/run/netratel/mcp-http-smoke.pfx,readonly" \
  --env ASPNETCORE_ENVIRONMENT=Development \
  --env ASPNETCORE_URLS=https://+:9224 \
  --env ASPNETCORE_Kestrel__Certificates__Default__Path=/run/netratel/mcp-http-smoke.pfx \
  --env ASPNETCORE_Kestrel__Certificates__Default__Password="$certificate_password" \
  --env NETRATEL_MCP_CONFIG=/run/netratel/mcp-config.json \
  --env NETRATEL_MCP_INSTANCE=dev \
  --env NETRATEL_MCP_DEV_API_BASE_URL=https://api.example.invalid \
  --env NetRatel__Mcp__Http__PublicResourceUri=https://mcp.example.invalid/mcp \
  --env NetRatel__Mcp__Http__Authority="$oidc_authority" \
  --env NetRatel__Mcp__Http__RequireHttpsMetadata=false \
  --env NetRatel__Mcp__Http__Audience=https://mcp.example.invalid/mcp \
  --env NetRatel__Mcp__Http__RequiredGroups__0=netratel-operators \
  --env NetRatel__Mcp__Http__RequiredScopes__0=netratel.mcp.read \
  "$image" >/dev/null

port="$(docker port "$container" 9224/tcp | sed -n '1s/.*://p')"
[[ -n "$port" ]] || { echo "HTTP MCP smoke container did not expose port 9224." >&2; exit 1; }
base_url="https://127.0.0.1:${port}"
# The MCP handler binds protected-resource metadata to the public resource host.
# Preserve that host while routing the disposable container through loopback.
mcp_public_request_headers=(
  --insecure
  --header 'Host: mcp.example.invalid'
  --header 'Accept: application/json, text/event-stream'
)

stage="waiting for the HTTPS HTTP MCP health endpoint"
for _ in $(seq 1 30); do
  if curl --insecure --fail --silent --show-error "$base_url/health/live" >/dev/null; then
    break
  fi
  sleep 1
done
curl --insecure --fail --silent --show-error "$base_url/health/live" >/dev/null

stage="reading protected-resource metadata"
resource_metadata="$(curl --fail --silent --show-error "${mcp_public_request_headers[@]}" "$base_url/.well-known/oauth-protected-resource/mcp")"
jq -e '
  .resource == "https://mcp.example.invalid/mcp"
  and (.scopes_supported | index("netratel.mcp.read"))
' <<<"$resource_metadata" >/dev/null

stage="checking unauthenticated HTTP MCP rejection"
unauthenticated_response="$(curl --silent --show-error --dump-header - --output /dev/null --write-out $'\n%{http_code}' \
  "${mcp_public_request_headers[@]}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"
status_code="$(tail -n 1 <<<"$unauthenticated_response")"
headers="$(sed '$d' <<<"$unauthenticated_response")"
[[ "$status_code" == 401 ]] || { echo "Unauthenticated HTTP MCP request returned $status_code instead of 401." >&2; exit 1; }
grep -qi '^WWW-Authenticate:.*resource_metadata=' <<<"$headers" || {
  echo "Unauthenticated HTTP MCP response did not advertise resource metadata." >&2
  exit 1
}

request_operator_access_token() {
  local client_id="$1" requested_scope="$2" token_response access_token
  token_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" \
    --data-urlencode 'grant_type=client_credentials' \
    --data-urlencode "client_id=${client_id}" \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "scope=${requested_scope}" \
    "${oidc_authority}/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  [[ -n "$access_token" ]] || {
    echo "Disposable OIDC provider did not issue an access token." >&2
    return 1
  }
  printf '%s' "$access_token"
}

mcp_response_json() {
  local response="$1" sse_payload
  sse_payload="$(sed -n 's/^data: //p' <<<"$response")"
  printf '%s' "${sse_payload:-$response}"
}

stage="requesting the operator access token"
operator_access_token="$(request_operator_access_token netratel-mcp-smoke-client netratel.mcp.read)"
invalid_token="${operator_access_token%?}x"
[[ "$invalid_token" != "$operator_access_token" ]] || invalid_token="${operator_access_token%?}y"
stage="checking invalid bearer-token rejection"
invalid_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  "${mcp_public_request_headers[@]}" \
  --header "Authorization: Bearer ${invalid_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"
[[ "$invalid_status" == 401 ]] || { echo "Invalid HTTP MCP bearer token returned $invalid_status instead of 401." >&2; exit 1; }

stage="requesting the wrong-scope access token"
wrong_scope_token="$(request_operator_access_token netratel-mcp-wrong-scope-client netratel.mcp.observe)"
stage="checking wrong-scope HTTP MCP rejection"
wrong_scope_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  "${mcp_public_request_headers[@]}" \
  --header "Authorization: Bearer ${wrong_scope_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"
[[ "$wrong_scope_status" == 403 ]] || { echo "Wrong-scope HTTP MCP bearer token returned $wrong_scope_status instead of 403." >&2; exit 1; }

stage="initializing the authorized HTTP MCP session"
initialize_response="$(curl --silent --show-error --write-out $'\n%{http_code}' \
  "${mcp_public_request_headers[@]}" \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":4,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"netratel-http-smoke","version":"1"}}}' \
  "$base_url/mcp")"
initialize_status="$(tail -n 1 <<<"$initialize_response")"
initialize_response="$(sed '$d' <<<"$initialize_response")"
if [[ "$initialize_status" != 200 ]]; then
  stage="authorized HTTP MCP initialization returned ${initialize_status}"
  exit 1
fi
stage="validating the authorized HTTP MCP initialization response"
initialize_json="$(mcp_response_json "$initialize_response")"
if ! jq -e '
  .jsonrpc == "2.0"
  and .id == 4
  and .result.serverInfo.name == "NetRatel.Mcp.Http"
' <<<"$initialize_json" >/dev/null; then
  initialize_summary="$(jq -c '{jsonrpc, id, result}' <<<"$initialize_json" 2>/dev/null || printf 'non-JSON response')"
  stage="authorized HTTP MCP initialization response: ${initialize_summary}"
  echo "Authorized HTTP MCP initialization did not return the expected server information." >&2
  exit 1
fi

stage="checking authorized HTTP MCP capability read"
authorized_response="$(curl --fail --silent --show-error \
  "${mcp_public_request_headers[@]}" \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"get"}}}' \
  "$base_url/mcp")"
jq -e '
  .jsonrpc == "2.0"
  and .id == 5
  and .result.structuredContent.success == true
' <<<"$(mcp_response_json "$authorized_response")" >/dev/null || {
  echo "Authorized HTTP MCP capability read did not return a successful structured result." >&2
  exit 1
}

echo "HTTP MCP image smoke test passed."
