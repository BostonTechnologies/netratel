#!/usr/bin/env bash
set -euo pipefail

image="${NETRATEL_MCP_HTTP_SMOKE_IMAGE:-}"
[[ -n "$image" ]] || { echo "NETRATEL_MCP_HTTP_SMOKE_IMAGE is required." >&2; exit 2; }
script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mcp_config="$script_root/tests/fixtures/mcp-http-smoke-config.json"
[[ -f "$mcp_config" ]] || { echo "HTTP MCP smoke configuration fixture is missing." >&2; exit 1; }

container="netratel-mcp-http-smoke-$$_${RANDOM}"
oidc_container="${container}-oidc"
cleanup() {
  local status=$?
  docker rm -f "$oidc_container" >/dev/null 2>&1 || true
  if docker container inspect "$container" >/dev/null 2>&1; then
    if (( status != 0 )); then
      docker logs "$container" >&2 || true
    fi
    docker rm -f "$container" >/dev/null || true
  fi
  exit "$status"
}
trap cleanup EXIT

oidc_config='{"interactiveLogin":true,"tokenCallbacks":[{"issuerId":"default","requestMappings":[{"requestParam":"client_id","match":"netratel-mcp-wrong-scope-client","claims":{"preferred_username":"netratel-mcp-wrong-scope@example.test","roles":["netratel-operators"],"scope":"netratel.mcp.observe","aud":["https://mcp.example.invalid/mcp"]}},{"requestParam":"code","match":"*","claims":{"preferred_username":"netratel-mcp-smoke@example.test","roles":["netratel-operators"],"scope":"netratel.mcp.read","aud":["https://mcp.example.invalid/mcp"]}}]}]}'
docker run --detach --name "$oidc_container" --publish 127.0.0.1::8080 \
  --env "JSON_CONFIG=${oidc_config}" \
  ghcr.io/navikt/mock-oauth2-server@sha256:ae36f65ca23e07e8786b288e53145e7ffb191c9dccfa9868ed433e7bbacad5db >/dev/null

oidc_port="$(docker port "$oidc_container" 8080/tcp | sed -n '1s/.*://p')"
[[ -n "$oidc_port" ]] || { echo "Disposable OIDC provider did not expose port 8080." >&2; exit 1; }
oidc_authority="http://host.docker.internal:${oidc_port}/default"
oidc_resolve="host.docker.internal:${oidc_port}:127.0.0.1"
for _ in $(seq 1 30); do
  if curl --resolve "$oidc_resolve" --fail --silent --show-error "${oidc_authority}/isalive" >/dev/null; then
    break
  fi
  sleep 1
done
curl --resolve "$oidc_resolve" --fail --silent --show-error "${oidc_authority}/isalive" >/dev/null

docker run --detach --name "$container" --publish 127.0.0.1::9224 \
  --add-host host.docker.internal:host-gateway \
  --mount "type=bind,source=$mcp_config,target=/run/netratel/mcp-config.json,readonly" \
  --env ASPNETCORE_ENVIRONMENT=Development \
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
base_url="http://127.0.0.1:${port}"

for _ in $(seq 1 30); do
  if curl --fail --silent --show-error "$base_url/health/live" >/dev/null; then
    break
  fi
  sleep 1
done
curl --fail --silent --show-error "$base_url/health/live" >/dev/null

resource_metadata="$(curl --fail --silent --show-error "$base_url/.well-known/oauth-protected-resource/mcp")"
jq -e '
  .resource == "https://mcp.example.invalid/mcp"
  and (.scopes_supported | index("netratel.mcp.read"))
' <<<"$resource_metadata" >/dev/null

unauthenticated_response="$(curl --silent --show-error --dump-header - --output /dev/null --write-out $'\n%{http_code}' \
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
  local client_id="$1" requested_scope="$2" redirect_uri authorization_url encoded_scope response callback_location callback_code token_response access_token
  redirect_uri="http://127.0.0.1:65535/netratel-mcp-smoke-callback"
  encoded_scope="$(node -e 'process.stdout.write(encodeURIComponent(process.argv[1]))' "openid profile ${requested_scope}")"
  authorization_url="${oidc_authority}/authorize?response_type=code&client_id=${client_id}&redirect_uri=http%3A%2F%2F127.0.0.1%3A65535%2Fnetratel-mcp-smoke-callback&scope=${encoded_scope}&state=mcp-http-smoke"
  response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" "$authorization_url")"
  callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"

  if [[ -z "$callback_location" ]]; then
    response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" \
      --data-urlencode 'username=netratel-mcp-smoke' "$authorization_url")"
    callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"
  fi

  [[ -n "$callback_location" ]] || {
    echo "Disposable OIDC provider did not return an authorization callback." >&2
    return 1
  }
  callback_code="$(node -e 'const callback = new URL(process.argv[1]); process.stdout.write(callback.searchParams.get("code") ?? "")' "$callback_location")"
  [[ -n "$callback_code" ]] || {
    echo "Disposable OIDC provider returned a callback without an authorization code." >&2
    return 1
  }

  token_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" \
    --data-urlencode 'grant_type=authorization_code' \
    --data-urlencode "client_id=${client_id}" \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "redirect_uri=${redirect_uri}" \
    --data-urlencode "code=${callback_code}" \
    "${oidc_authority}/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  [[ -n "$access_token" ]] || {
    echo "Disposable OIDC provider did not issue an access token." >&2
    return 1
  }
  printf '%s' "$access_token"
}

operator_access_token="$(request_operator_access_token netratel-mcp-smoke-client netratel.mcp.read)"
invalid_token="${operator_access_token%?}x"
[[ "$invalid_token" != "$operator_access_token" ]] || invalid_token="${operator_access_token%?}y"
invalid_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  --header "Authorization: Bearer ${invalid_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"
[[ "$invalid_status" == 401 ]] || { echo "Invalid HTTP MCP bearer token returned $invalid_status instead of 401." >&2; exit 1; }

wrong_scope_token="$(request_operator_access_token netratel-mcp-wrong-scope-client netratel.mcp.observe)"
wrong_scope_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  --header "Authorization: Bearer ${wrong_scope_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"
[[ "$wrong_scope_status" == 403 ]] || { echo "Wrong-scope HTTP MCP bearer token returned $wrong_scope_status instead of 403." >&2; exit 1; }

authorized_response="$(curl --fail --silent --show-error \
  --header "Authorization: Bearer ${operator_access_token}" \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"get"}}}' \
  "$base_url/mcp")"
jq -e '
  .jsonrpc == "2.0"
  and .id == 4
  and .result.structuredContent.success == true
' <<<"$authorized_response" >/dev/null || {
  echo "Authorized HTTP MCP capability read did not return a successful structured result." >&2
  exit 1
}

echo "HTTP MCP image smoke test passed."
