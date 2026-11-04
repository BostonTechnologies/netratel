#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
source "$root/tools/ci/chromium-nss-trust.sh"
source "$root/tools/ci/oidc-smoke-claims.sh"

mode="${NETRATEL_COMPOSE_SMOKE_MODE:-source}"
auth_environment="${NETRATEL_SMOKE_AUTH_ENVIRONMENT:-Production}"
case "$auth_environment" in
  Development|Production) ;;
  *)
    echo "NETRATEL_SMOKE_AUTH_ENVIRONMENT must be Development or Production." >&2
    exit 2
    ;;
esac
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
smoke_ca_key_path="$(mktemp)"
smoke_ca_certificate_path="$(mktemp --suffix=.crt)"
tls_request_path="$(mktemp --suffix=.csr)"
tls_extensions_path="$(mktemp)"
oidc_tls_key_path="$(mktemp)"
oidc_tls_request_path="$(mktemp --suffix=.csr)"
oidc_tls_certificate_path="$(mktemp --suffix=.crt)"
oidc_tls_extensions_path="$(mktemp)"
oidc_tls_keystore_path="$(mktemp --suffix=.p12)"
trust_bundle_path="$(mktemp --suffix=.pem)"
smoke_ca_trust_path=""
smoke_ca_trust_installed=0
cli_extract_dir=""
mcp_stdio_extract_dir=""
mcp_stdio_config_path="$(mktemp --suffix=.json)"
mcp_stdio_error_path="$(mktemp)"
bundle_extract_dir=""
gateway_presence_response_path="$(mktemp)"
command_response_path="$(mktemp)"
source_images_override_path=""
client_volume="${project}-client-state"
gateway_client="${project}-gateway-client"
inventory_state_directory=/var/lib/netratel/state
software_inventory_collection_id=
inventory_evidence_directory="${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}"
log_evidence_directory="${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}"
inventory_state_boundary="documented fixture override in existing persisted volume"
if [[ -z "$inventory_evidence_directory" && "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  inventory_evidence_directory="$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")/software-inventory"
  log_evidence_directory="$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")/client-logs"
  inventory_state_boundary=image-packaged-default
fi
stage="initializing Compose OIDC smoke"
record_client_acceptance() {
  if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
    jq -nc --arg stage "$1" '{stage:$stage,passed:true}' >> "$NETRATEL_CLIENT_ARCHIVE_EVIDENCE/deployment-stages.jsonl"
  fi
}

retain_gateway_summary() {
  if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
    docker logs "$gateway_client" 2>&1 |
      python3 "$root/tools/ci/summarize-gateway-log.py" --metrics > "$NETRATEL_CLIENT_ARCHIVE_EVIDENCE/gateway-$1.json" || true
  fi
}

wait_for_migrations() {
  local container_id state exit_code
  container_id="$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q migrations)"
  [[ -n "$container_id" ]] || { echo "Migration container was not created." >&2; return 1; }

  for _ in $(seq 1 60); do
    state="$(docker inspect --format '{{.State.Status}}' "$container_id")"
    if [[ "$state" == exited ]]; then
      exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container_id")"
      if [[ "$exit_code" != 0 ]]; then
        echo "Migration container exited with ${exit_code}." >&2
        docker logs "$container_id" >&2 || true
        return 1
      fi
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
    status="$(curl --connect-timeout 2 --silent "${web_curl_args[@]}" --output /dev/null --write-out '%{http_code}' "$url" || true)"
    if [[ "$status" == "$expected_status" ]]; then
      return 0
    fi
    sleep 1
  done

  echo "Expected ${url} to return ${expected_status} after startup." >&2
  return 1
}

run_browser_oidc_smoke() {
  local playwright_script proxy_address authorization_boundaries
  proxy_address="$(docker compose --project-name "$project" "${compose_args[@]}" port web-proxy 9444)"
  authorization_boundaries=false
  if [[ "$auth_environment" == Production ]]; then
    authorization_boundaries=true
  fi
  dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
  dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
  playwright_script="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1"
  [[ -f "$playwright_script" ]] || {
    echo "Playwright install script was not produced by the browser-smoke build." >&2
    return 1
  }
  pwsh "$playwright_script" install --with-deps chromium
  prepare_chromium_nss_trust "$smoke_ca_certificate_path"
  rm -f TestResults/oidc-compose/oidc-browser.trx
  NETRATEL_BROWSER_SMOKE_WEB_URL="$web_public_url" \
    NETRATEL_BROWSER_SMOKE_PROXY_URL="https://$proxy_address" \
    NETRATEL_BROWSER_SMOKE_USERNAME="netratel-test-operator" \
    NETRATEL_BROWSER_SMOKE_DIRECTORY_AUTHZ="$authorization_boundaries" \
    NETRATEL_BROWSER_SMOKE_TENANT_ADMIN_USERNAME="netratel-test-tenant-admin" \
    NETRATEL_BROWSER_SMOKE_UNPRIVILEGED_USERNAME="netratel-test-unprivileged" \
    NETRATEL_BROWSER_SMOKE_NSS_DATA_HOME="$chromium_nss_xdg_data_home" \
    XDG_DATA_HOME="$chromium_nss_xdg_data_home" \
    SSL_CERT_FILE="$trust_bundle_path" \
    NODE_EXTRA_CA_CERTS="$smoke_ca_certificate_path" \
    dotnet test --project src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build \
      --filter-class NetRatel.Web.PlaywrightTests.OidcComposeBrowserSmokeTests \
      --results-directory TestResults/oidc-compose --report-trx --report-trx-filename oidc-browser.trx
  python3 tools/ci/verify-mtp-trx.py TestResults/oidc-compose/oidc-browser.trx --expected-executed 2
}

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=${mode} Compose OIDC smoke failed::${stage}" >&2
  fi
  if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
    retain_gateway_summary final
    jq -n --arg stage "$stage" --argjson exitCode "$status" \
      '{exitCode:$exitCode,lastStage:$stage,passed:($exitCode == 0),scope:"Source OIDC deployment with exact archived client; broader feature/recovery acceptance remains pending"}' \
      > "$NETRATEL_CLIENT_ARCHIVE_EVIDENCE/deployment.json"
  fi
  docker rm -f "$gateway_client" >/dev/null 2>&1 || true
  docker compose --project-name "$project" "${compose_args[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "$client_volume" >/dev/null 2>&1 || true
  cleanup_chromium_nss_trust || true
  unlink "$key_path" 2>/dev/null || true
  unlink "$cookie_jar" 2>/dev/null || true
  unlink "$tls_key_path" 2>/dev/null || true
  unlink "$tls_certificate_path" 2>/dev/null || true
  unlink "$tls_bundle_path" 2>/dev/null || true
  unlink "$smoke_ca_key_path" 2>/dev/null || true
  unlink "$smoke_ca_certificate_path" 2>/dev/null || true
  unlink "$tls_request_path" 2>/dev/null || true
  unlink "$tls_extensions_path" 2>/dev/null || true
  unlink "$oidc_tls_key_path" 2>/dev/null || true
  unlink "$oidc_tls_request_path" 2>/dev/null || true
  unlink "$oidc_tls_certificate_path" 2>/dev/null || true
  unlink "$oidc_tls_extensions_path" 2>/dev/null || true
  unlink "$oidc_tls_keystore_path" 2>/dev/null || true
  unlink "${smoke_ca_certificate_path}.srl" 2>/dev/null || true
  unlink "$trust_bundle_path" 2>/dev/null || true
  if (( smoke_ca_trust_installed )); then
    if [[ "$EUID" -eq 0 ]]; then
      rm -f "$smoke_ca_trust_path"
      update-ca-certificates >/dev/null 2>&1 || true
    else
      sudo rm -f "$smoke_ca_trust_path"
      sudo update-ca-certificates >/dev/null 2>&1 || true
    fi
  fi
  if [[ -n "$cli_extract_dir" ]]; then
    find "$cli_extract_dir" -depth -delete 2>/dev/null || true
  fi
  if [[ -n "$mcp_stdio_extract_dir" ]]; then
    find "$mcp_stdio_extract_dir" -depth -delete 2>/dev/null || true
  fi
  unlink "$mcp_stdio_config_path" 2>/dev/null || true
  unlink "$mcp_stdio_error_path" 2>/dev/null || true
  unlink "$gateway_presence_response_path" 2>/dev/null || true
  unlink "$command_response_path" 2>/dev/null || true
  if [[ -n "$source_images_override_path" ]]; then unlink "$source_images_override_path"; fi
  if [[ -n "$bundle_extract_dir" ]]; then
    find "$bundle_extract_dir" -depth -delete 2>/dev/null || true
  fi
  return "$status"
}
trap cleanup EXIT

if [[ "$mode" == release-images && -n "${NETRATEL_COMPOSE_SMOKE_BUNDLE:-}" ]]; then
  bundle_extract_dir="$(mktemp -d)"
  tar -xzf "$NETRATEL_COMPOSE_SMOKE_BUNDLE" -C "$bundle_extract_dir"
  [[ -s "$bundle_extract_dir/INSTALL.md" ]] || { echo "Extracted bundle lacks installation instructions." >&2; exit 1; }
  compose_args=(--env-file "$bundle_extract_dir/.env.images.example"
    -f "$bundle_extract_dir/compose.images.yaml" -f "$root/tests/compose/oidc-smoke.compose.yaml")
fi

web_port="${NETRATEL_WEB_PORT:-8082}"
if [[ "$auth_environment" == Production ]]; then
  oidc_port="${NETRATEL_OIDC_TEST_PORT:-18444}"
  compose_args+=(-f tests/compose/oidc-smoke.production.compose.yaml)
  export NETRATEL_SMOKE_OIDC_TLS_PASSWORD="${NETRATEL_SMOKE_OIDC_TLS_PASSWORD:-netratel-compose-oidc-password}"
else
  oidc_port="${NETRATEL_OIDC_TEST_PORT:-8080}"
fi
export NETRATEL_WEB_PORT="$web_port"
export NETRATEL_OIDC_TEST_PORT="$oidc_port"
export NETRATEL_SMOKE_AUTH_ENVIRONMENT="$auth_environment"
web_url="http://127.0.0.1:${web_port}"
web_public_url="$web_url"
oidc_resolve="host.docker.internal:${oidc_port}:127.0.0.1"
oidc_curl_args=()
web_curl_args=()
oidc_scheme="http"
if [[ "$auth_environment" == Production ]]; then
  oidc_scheme="https"
fi
oidc_authority="${oidc_scheme}://host.docker.internal:${oidc_port}/default"
oidc_loopback_token_url="${oidc_scheme}://127.0.0.1:${oidc_port}/default/token"

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
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$smoke_ca_key_path" >/dev/null 2>&1
openssl req -x509 -new -key "$smoke_ca_key_path" -sha256 -days 2 \
  -subj '/CN=NetRatel disposable smoke CA' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -out "$smoke_ca_certificate_path" >/dev/null 2>&1
openssl req -new -newkey rsa:2048 -nodes -keyout "$tls_key_path" \
  -out "$tls_request_path" -subj '/CN=gateway' >/dev/null 2>&1
cat > "$tls_extensions_path" <<'EOF'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:gateway,DNS:localhost,IP:127.0.0.1
EOF
openssl x509 -req -in "$tls_request_path" -CA "$smoke_ca_certificate_path" \
  -CAkey "$smoke_ca_key_path" -CAcreateserial -days 2 -sha256 \
  -extfile "$tls_extensions_path" -out "$tls_certificate_path" >/dev/null 2>&1
openssl pkcs12 -export -out "$tls_bundle_path" -inkey "$tls_key_path" -in "$tls_certificate_path" \
  -certfile "$smoke_ca_certificate_path" \
  -passout "pass:${NETRATEL_SMOKE_TLS_CERT_PASSWORD}" >/dev/null 2>&1
chmod 644 "$tls_certificate_path" "$tls_bundle_path" "$smoke_ca_certificate_path" "$trust_bundle_path"
cp "$smoke_ca_certificate_path" "$trust_bundle_path"
export NETRATEL_SMOKE_TLS_CERT_PATH="$tls_bundle_path"
export NETRATEL_SMOKE_TLS_CERTIFICATE_PATH="$tls_certificate_path"
export NETRATEL_SMOKE_TLS_KEY_PATH="$tls_key_path"
export NETRATEL_SMOKE_CA_BUNDLE_PATH="$trust_bundle_path"

if [[ "$auth_environment" == Production ]]; then
  openssl req -new -newkey rsa:2048 -nodes -keyout "$oidc_tls_key_path" \
    -out "$oidc_tls_request_path" -subj '/CN=host.docker.internal' >/dev/null 2>&1
  cat > "$oidc_tls_extensions_path" <<'EOF'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:host.docker.internal,IP:127.0.0.1
EOF
  openssl x509 -req -in "$oidc_tls_request_path" -CA "$smoke_ca_certificate_path" \
    -CAkey "$smoke_ca_key_path" -CAcreateserial -days 2 -sha256 \
    -extfile "$oidc_tls_extensions_path" -out "$oidc_tls_certificate_path" >/dev/null 2>&1
  openssl pkcs12 -export -out "$oidc_tls_keystore_path" -inkey "$oidc_tls_key_path" \
    -in "$oidc_tls_certificate_path" -certfile "$smoke_ca_certificate_path" \
    -name netratel-oidc-smoke \
    -passout "pass:${NETRATEL_SMOKE_OIDC_TLS_PASSWORD}" >/dev/null 2>&1
  chmod 644 "$oidc_tls_keystore_path"
  export NETRATEL_SMOKE_OIDC_TLS_KEYSTORE_PATH="$oidc_tls_keystore_path"
  export SSL_CERT_FILE="$trust_bundle_path"
  oidc_curl_args=(--cacert "$smoke_ca_certificate_path")

  if [[ "${GITHUB_ACTIONS:-false}" == true ]]; then
    command -v update-ca-certificates >/dev/null 2>&1 || {
      echo 'The hosted Production OIDC smoke requires update-ca-certificates to trust its disposable CA.' >&2
      exit 1
    }
    smoke_ca_trust_path="/usr/local/share/ca-certificates/netratel-smoke-${project}.crt"
    [[ ! -e "$smoke_ca_trust_path" && ! -L "$smoke_ca_trust_path" ]] || {
      echo 'Refusing to replace a pre-existing system CA file at the smoke-owned path.' >&2
      exit 1
    }
    smoke_ca_trust_installed=1
    if [[ "$EUID" -eq 0 ]]; then
      install -m 0644 "$smoke_ca_certificate_path" "$smoke_ca_trust_path"
      update-ca-certificates >/dev/null
    else
      command -v sudo >/dev/null 2>&1 || {
        echo 'The hosted Production OIDC smoke needs root or sudo to trust its disposable CA.' >&2
        exit 1
      }
      sudo install -m 0644 "$smoke_ca_certificate_path" "$smoke_ca_trust_path"
      sudo update-ca-certificates >/dev/null
    fi
  fi
fi

export NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"
if [[ -n "$bundle_extract_dir" ]]; then
  export NETRATEL_PUBLIC_NGINX_CONFIG_PATH="$bundle_extract_dir/nginx.public-https.conf"
else
  export NETRATEL_PUBLIC_NGINX_CONFIG_PATH="$root/release/nginx.public-https.conf"
fi
export NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
api_port="${NETRATEL_API_TEST_PORT:-9222}"
api_url="http://127.0.0.1:${api_port}"

start_gateway_client() {
  local inventory_configuration=()
  if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
    inventory_configuration=(--env "NetRatelCLIENT__Client__AutoUpdate__StateDirectory=$inventory_state_directory")
  fi
  docker rm -f "$gateway_client" >/dev/null 2>&1 || true
  docker run --detach --name "$gateway_client" --network "${project}_default" \
    --volume "${client_volume}:/var/lib/netratel" \
    --volume "${trust_bundle_path}:/run/netratel-smoke/ca-bundle.pem:ro" \
    --env SSL_CERT_FILE=/run/netratel-smoke/ca-bundle.pem \
    --env NetRatel_CLIENT_LOG_DIR=/var/lib/netratel/logs \
    --env NetRatelCLIENT__Client__ApiBaseUrl=http://api:9222 \
    --env NetRatelCLIENT__Gateway__Endpoint=https://gateway:443 \
    "${inventory_configuration[@]}" \
    "$client_image" \
    --Gateway:TelemetryFastIntervalSeconds=1 >/dev/null
}

wait_for_gateway_sessions() {
  local gateway_logs
  for _ in $(seq 1 30); do
    gateway_logs="$(docker logs "$gateway_client" 2>&1 || true)"
    if grep -Fq 'Presence admitted.' <<<"$gateway_logs" &&
      grep -Fq 'Command gateway admitted. authority=akka.' <<<"$gateway_logs"; then
      echo "Passed authenticated gRPC transport through the public HTTPS ingress (gateway:443): presence and command sessions admitted." >&2
      return 0
    fi
    sleep 1
  done

  echo "Disposable Client did not establish authenticated HTTPS presence and command-gateway sessions." >&2
  docker logs "$gateway_client" >&2 || true
  return 1
}

read_current_gateway_presence() {
  local access_token="$1" http_status state
  http_status="$(curl --connect-timeout 2 --max-time 5 --silent --output "$gateway_presence_response_path" \
    --write-out '%{http_code}' --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v2/client-presence/?tenantId=${tenant_id}&online=true&search=${agent_id}&limit=100" 2>/dev/null || true)"

  if [[ "$http_status" == 200 ]] && jq -e \
    --argjson expected_tenant "$tenant_id" --arg expected_agent "$agent_id" \
    'any(.items[]?; .tenantId == $expected_tenant and ((.agentId | ascii_downcase) == ($expected_agent | ascii_downcase)) and .online == true and .source == "gateway" and .authority == "akka" and .isAuthoritative == true)' \
    "$gateway_presence_response_path" >/dev/null 2>&1; then
    state="online"
  elif [[ "$http_status" == 200 ]]; then
    state="offline"
  else
    state="unavailable"
  fi

  printf '%s %s\n' "${http_status:-000}" "$state"
}

gateway_client_container_state() {
  docker inspect --format '{{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} restarting={{.State.Restarting}}' \
    "$gateway_client" 2>/dev/null || printf 'unavailable'
}

wait_for_current_gateway_presence() {
  local access_token="$1" http_status state
  for _ in $(seq 1 30); do
    read -r http_status state < <(read_current_gateway_presence "$access_token")
    if [[ "$http_status" == 200 && "$state" == online ]]; then
      echo "Passed current API presence check for the disposable Client." >&2
      return 0
    fi
    sleep 1
  done

  echo "Disposable Client did not have current API gateway presence (HTTP ${http_status:-unavailable}, state=${state:-unavailable})." >&2
  printf 'Disposable Client container state: %s\n' "$(gateway_client_container_state)" >&2
  return 1
}

runtime_log_source_available() {
  jq -e '
    if type != "array" then false else
      [.[] | select(type == "object" and .sourceId == "netratel-runtime")] as $runtime |
      ($runtime | length) == 1 and ($runtime[0] |
        .kind == "runtime" and .platform == "linux" and .available == true and .unavailableReason == null and
        .supportsHistory == true and .supportsPaging == true and .supportsLive == true)
    end' "$1" >/dev/null
}

runtime_log_history_valid() {
  jq -e --argjson minimum "$2" '
    def nonempty_string: if type == "string" then length > 0 else false end;
    def integer: if type == "number" then floor == . else false end;
    if type != "object" or (.records | type) != "array" then false else
      .errorCode == null and .resyncRequired == false and (.hasMore | type) == "boolean" and
      (.droppedRecordCount | integer and . >= 0) and (.records | length) >= $minimum and (.records | length) <= 100 and
      all(.records[]; .sourceId == "netratel-runtime" and .category == "Gateway" and .prefix == "Gateway" and
        (.sequence | integer and . > 0) and (.cursor | nonempty_string) and (.timestampUtc | nonempty_string) and
        (.message | type) == "string" and (.message | contains("Presence admitted.")) and .truncated == false)
    end' "$1" >/dev/null
}

verify_runtime_log_acceptance() {
  local token="$1" evidence="$2" phase="$3" route sources history http_status ready=false
  mkdir -p "$evidence"
  route="$api_url/api/v2/agents/$tenant_id/$agent_id/logs"
  sources="$evidence/log-sources-$phase.json"
  history="$evidence/log-history-$phase.json"
  stage="checking authorized runtime log source and history ($phase)"
  for _ in $(seq 1 30); do
    http_status="$(curl --silent --show-error --connect-timeout 2 --max-time 5 --output "$sources" --write-out '%{http_code}' \
      --header "Authorization: Bearer $token" "$route/sources")" || return $?
    case "$http_status" in
      200)
        runtime_log_source_available "$sources" || return 1
        curl --silent --show-error --fail --connect-timeout 2 --max-time 5 --get --header "Authorization: Bearer $token" \
          --data-urlencode sourceId=netratel-runtime --data-urlencode pageSize=100 \
          --data-urlencode prefix=Gateway --data-urlencode 'text=Presence admitted.' "$route/history" > "$history" || return $?
        # Admission can precede the initial runtime snapshot; only a valid empty page may wait.
        runtime_log_history_valid "$history" 0 || return 1
        if runtime_log_history_valid "$history" 1; then ready=true; break; fi
        ;;
      404) ;; # The admitted log stream has not registered yet.
      *) echo "Runtime log sources failed (HTTP $http_status)." >&2; return 1 ;;
    esac
    sleep 1
  done
  [[ "$ready" == true ]] || { echo 'Runtime log source/history did not become available.' >&2; return 1; }
  runtime_log_source_available "$sources"
  runtime_log_history_valid "$history" 1
  jq -n --argjson tenant "$tenant_id" --arg agent "$agent_id" --arg phase "$phase" \
    --slurpfile sources "$sources" --slurpfile history "$history" '
    {passed:true,tenantId:$tenant,agentId:$agent,phase:$phase,sourceId:"netratel-runtime",
      historyRecordCount:($history[0].records | length),droppedRecordCount:$history[0].droppedRecordCount,
      liveAdvertised:([$sources[0][] | select(.sourceId == "netratel-runtime")][0].supportsLive),liveDeliveryExecuted:false,
      historyFilter:{prefix:"Gateway",text:"Presence admitted.",pageSize:100}}' > "$evidence/runtime-logs-$phase.json"
  record_client_acceptance "$phase-runtime-log-source-and-history"
}

inventory_status_complete() {
  jq -e --argjson tenant "$2" --arg agent "$3" --arg collection "$4" '
    def positive_integer: if type == "number" then . > 0 and floor == . else false end;
    .tenantId == $tenant and (.agentId | ascii_downcase) == ($agent | ascii_downcase) and
    .latestScan.collectionId == $collection and .latestScan.status == "Complete" and
    .latestScan.errors == [] and (.latestScan.receivedChunks | positive_integer) and (.latestScan.totalChunks | positive_integer) and
    .latestScan.receivedChunks == .latestScan.totalChunks and
    (.lastSuccessfulScanUtc | strings | length) > 0 and (.latestScan.completedAtUtc | strings | length) > 0' "$1" >/dev/null
}

inventory_scan_complete() {
  jq -e --arg collection "$2" '
    def positive_integer: if type == "number" then . > 0 and floor == . else false end;
    .collectionId == $collection and .status == "Complete" and .errors == [] and
    (.receivedChunks | positive_integer) and (.totalChunks | positive_integer) and .receivedChunks == .totalChunks and
    (.completedAtUtc | strings | length) > 0' "$1" >/dev/null
}

inventory_snapshot_complete() {
  jq -e --arg agent "$2" '
    def positive_integer: if type == "number" then . > 0 and floor == . else false end;
    (.total | positive_integer) and (.items | type) == "array" and (.items | length) > 0 and
    all(.items[]; (.agentId | ascii_downcase) == ($agent | ascii_downcase) and .os == "linux" and .package.source == "dpkg") and
    any(.items[]; .package.name == "bash" and (.package.version | type) == "string" and (.package.version | length) > 0)' "$1" >/dev/null
}

prepare_software_inventory_acceptance() {
  local token="$1" evidence="$2" boundary="$3" policy
  mkdir -p "$evidence"
  stage="configuring one disposable software inventory collection"
  policy="$api_url/api/v2/tenants/$tenant_id/software-inventory/policy"
  curl --silent --show-error --fail --header "Authorization: Bearer $token" "$policy" > "$evidence/software-original-policy.json"
  jq '.enabled = false' "$evidence/software-original-policy.json" > "$evidence/software-policy-request.json"
  curl --silent --show-error --fail --request PUT --header "Authorization: Bearer $token" --header 'Content-Type: application/json' \
    --data-binary "@$evidence/software-policy-request.json" "$policy" > "$evidence/software-configured-policy.json"
  jq -e '.enabled == false' "$evidence/software-configured-policy.json" >/dev/null
  jq -n --arg directory "$inventory_state_directory" --arg boundary "$boundary" '{stateDirectory:$directory,stateDirectoryBoundary:$boundary,automaticCadenceEnabled:false}' \
    > "$evidence/software-configuration.json"
}

verify_software_inventory_acceptance() {
  local token="$1" evidence="$2" boundary="$3" route status directory deadline
  if [[ "$boundary" == image-packaged-default ]]; then
    stage="validating the normal Client image packaged state default"
    docker exec "$gateway_client" python3 -c 'import json,os; print(json.dumps({"uid":os.getuid(),"packagedStateDirectory":json.load(open("/app/appsettings.json"))["Client"]["AutoUpdate"]["StateDirectory"],"stateEnvironmentOverridePresent":"NetRatelCLIENT__Client__AutoUpdate__StateDirectory" in os.environ}))' > "$evidence/software-image-default.json"
    jq -e --arg directory "$inventory_state_directory" \
      '.uid == 10001 and .packagedStateDirectory == $directory and .stateEnvironmentOverridePresent == false' "$evidence/software-image-default.json" >/dev/null
  fi
  route="$api_url/api/v2/agents/$tenant_id/$agent_id/software-inventory"
  status="$evidence/software-status.json"
  stage="requesting one authorized software inventory collection"
  curl --silent --show-error --fail --header "Authorization: Bearer $token" "$route" > "$evidence/software-before-refresh.json"
  jq -e '.latestScan == null and .canRefresh == true and .policy.enabled == false' "$evidence/software-before-refresh.json" >/dev/null
  # This is the sole refresh request. Subsequent polls and restart checks are read-only.
  curl --silent --show-error --fail --request POST --header "Authorization: Bearer $token" --header 'Content-Type: application/json' \
    --data '{"force":true}' "$route/refresh" > "$evidence/software-refresh.json"
  software_inventory_collection_id="$(jq -er '.latestScan.collectionId' "$evidence/software-refresh.json")"
  [[ "$software_inventory_collection_id" =~ ^[0-9a-fA-F-]{36}$ ]]
  jq -e --argjson tenant "$tenant_id" --arg agent "$agent_id" \
    '.tenantId == $tenant and (.agentId | ascii_downcase) == ($agent | ascii_downcase) and .canRefresh == true' "$evidence/software-refresh.json" >/dev/null
  directory="$inventory_state_directory/software-inventory/$tenant_id-${agent_id//-/}"
  directory="${directory,,}"
  stage="waiting for real software inventory collection and matching acknowledgement"
  deadline=$((SECONDS + 180))
  while (( SECONDS < deadline )); do
    curl --silent --show-error --fail --max-time 5 --header "Authorization: Bearer $token" "$route" > "$status"
    if jq -e '.latestScan.status == "Error" or .latestScan.status == "Partial"' "$status" >/dev/null; then
      echo 'The software inventory collection reported an incomplete or failed result.' >&2
      return 1
    fi
    if inventory_status_complete "$status" "$tenant_id" "$agent_id" "$software_inventory_collection_id" &&
      docker exec "$gateway_client" test -d "$directory" && docker exec "$gateway_client" test ! -e "$directory/pending.json"; then
      break
    fi
    sleep 1
  done
  inventory_status_complete "$status" "$tenant_id" "$agent_id" "$software_inventory_collection_id"
  [[ "$(docker exec "$gateway_client" id -u)" == 10001 ]]
  docker exec "$gateway_client" test -d "$directory"
  docker exec "$gateway_client" test ! -e "$directory/pending.json"
  curl --silent --show-error --fail --header "Authorization: Bearer $token" "$route/scans/$software_inventory_collection_id" > "$evidence/software-scan.json"
  inventory_scan_complete "$evidence/software-scan.json" "$software_inventory_collection_id"
  curl --silent --show-error --fail --header "Authorization: Bearer $token" "$route/snapshot?pageSize=100&source=dpkg&os=linux&search=bash" > "$evidence/software-snapshot.json"
  inventory_snapshot_complete "$evidence/software-snapshot.json" "$agent_id"
  jq -n --arg collection "$software_inventory_collection_id" --arg directory "$directory" \
    --slurpfile status "$status" --slurpfile snapshot "$evidence/software-snapshot.json" \
    '{passed:true,collectionId:$collection,refreshRequests:1,normalUid:10001,matchingAcknowledgementConsumed:true,outboxDirectory:$directory,status:$status[0],snapshot:$snapshot[0]}' > "$evidence/software-inventory.json"
  record_client_acceptance software-inventory-complete-and-acknowledged
}

verify_software_inventory_after_restart() {
  local token="$1" evidence="$2"
  stage="checking the same software inventory collection after recreation"
  curl --silent --show-error --fail --header "Authorization: Bearer $token" \
    "$api_url/api/v2/agents/$tenant_id/$agent_id/software-inventory" > "$evidence/software-after-restart.json"
  inventory_status_complete "$evidence/software-after-restart.json" "$tenant_id" "$agent_id" "$software_inventory_collection_id"
  jq -e --slurpfile before "$evidence/software-status.json" \
    '.lastSuccessfulScanUtc == $before[0].lastSuccessfulScanUtc' "$evidence/software-after-restart.json" >/dev/null
  record_client_acceptance software-inventory-preserved-after-restart
}

request_oidc_access_token() (
  local username="$1" state="$2"
  local redirect_uri authorization_url response callback_location callback_code token_response access_token id_token refresh_token
  local refresh_response refreshed_access_token refreshed_id_token claims_json token_cookie_jar
  redirect_uri="http://127.0.0.1:65535/netratel-smoke-callback"
  token_cookie_jar="$(mktemp)"
  trap 'unlink "$token_cookie_jar" 2>/dev/null || true' EXIT
  claims_json="$(oidc_smoke_claims_for_subject "$root/tests/compose/oidc-smoke-login.html" "$username")" || {
    echo "The OIDC smoke fixture has no claims profile for ${username}." >&2
    return 1
  }
  authorization_url="${oidc_authority}/authorize?response_type=code&client_id=netratel-smoke-client&redirect_uri=http%3A%2F%2F127.0.0.1%3A65535%2Fnetratel-smoke-callback&scope=openid%20netratel.api&state=${state}"
  response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" "${oidc_curl_args[@]}" \
    --cookie "$token_cookie_jar" --cookie-jar "$token_cookie_jar" "$authorization_url")"
  callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"

  if [[ -z "$callback_location" ]]; then
    response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" "${oidc_curl_args[@]}" \
      --cookie "$token_cookie_jar" --cookie-jar "$token_cookie_jar" \
      --data-urlencode "username=${username}" \
      --data-urlencode "claims=${claims_json}" "$authorization_url")"
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
  token_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" "${oidc_curl_args[@]}" \
    --data-urlencode 'grant_type=authorization_code' \
    --data-urlencode 'client_id=netratel-smoke-client' \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "redirect_uri=${redirect_uri}" \
    --data-urlencode "code=${callback_code}" \
    "$oidc_authority/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  id_token="$(jq -r '.id_token // empty' <<<"$token_response")"
  refresh_token="$(jq -r '.refresh_token // empty' <<<"$token_response")"
  [[ -n "$access_token" && -n "$id_token" && -n "$refresh_token" ]] || {
    echo "The test OIDC provider did not issue the expected access, ID and refresh tokens." >&2
    return 1
  }

  verify_oidc_smoke_token_claims "$access_token" "$claims_json" "$username" "access token" "$oidc_authority" || return 1
  verify_oidc_smoke_token_claims "$id_token" "$claims_json" "$username" "ID token" "$oidc_authority" || return 1

  refresh_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" "${oidc_curl_args[@]}" \
    --data-urlencode 'grant_type=refresh_token' \
    --data-urlencode 'client_id=netratel-smoke-client' \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "refresh_token=${refresh_token}" \
    "$oidc_authority/token")"
  refreshed_access_token="$(jq -r '.access_token // empty' <<<"$refresh_response")"
  refreshed_id_token="$(jq -r '.id_token // empty' <<<"$refresh_response")"
  [[ -n "$refreshed_access_token" && -n "$refreshed_id_token" ]] || {
    echo "The test OIDC provider did not preserve the expected claims across token refresh for ${username}." >&2
    return 1
  }
  verify_oidc_smoke_token_claims "$refreshed_access_token" "$claims_json" "$username" "refreshed access token" "$oidc_authority" || return 1
  verify_oidc_smoke_token_claims "$refreshed_id_token" "$claims_json" "$username" "refreshed ID token" "$oidc_authority" || return 1

  printf '%s' "$access_token"
)

verify_directory_denial() {
  local access_token="$1" path="$2" status
  status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    --header "Authorization: Bearer ${access_token}" "${api_url}${path}")"
  [[ "$status" == 403 ]] || {
    echo "Expected the authenticated client-directory request ${path} to be forbidden, got HTTP ${status}." >&2
    return 1
  }
}

prepare_oidc_directory_authorization_fixtures() {
  local operator_access_token="$1" tenant_response role_response role_id
  local tenant_admin_token tenant_admin_self tenant_admin_principal_id
  local unprivileged_token unprivileged_self unprivileged_principal_id
  local tenant_admin_scope unprivileged_scope

  tenant_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${operator_access_token}" \
    --header 'Content-Type: application/json' \
    --data '{"name":"compose-smoke-unassigned","description":"Disposable second Compose smoke tenant","location":"test","domains":[],"autoUpdate":false}' \
    "${api_url}/api/v1/tenants/")"
  unassigned_tenant_id="$(jq -r '.tenantId // empty' <<<"$tenant_response")"
  [[ "$unassigned_tenant_id" =~ ^[1-9][0-9]*$ && "$unassigned_tenant_id" != "$tenant_id" ]] || {
    echo "Compose smoke could not create a second distinct tenant for scoped authorization checks." >&2
    return 1
  }

  tenant_admin_token="$(request_oidc_access_token netratel-test-tenant-admin compose-smoke-tenant-admin)"
  tenant_admin_self="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${tenant_admin_token}" \
    "${api_url}/api/v2/access/self")"
  tenant_admin_principal_id="$(jq -r '.principalId // empty' <<<"$tenant_admin_self")"
  [[ -n "$tenant_admin_principal_id" ]] || {
    echo "The validated tenant-administrator OIDC subject did not resolve to an application principal." >&2
    return 1
  }

  unprivileged_token="$(request_oidc_access_token netratel-test-unprivileged compose-smoke-unprivileged)"
  unprivileged_self="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${unprivileged_token}" \
    "${api_url}/api/v2/access/self")"
  unprivileged_principal_id="$(jq -r '.principalId // empty' <<<"$unprivileged_self")"
  [[ -n "$unprivileged_principal_id" && "$unprivileged_principal_id" != "$tenant_admin_principal_id" ]] || {
    echo "The two validated OIDC subjects did not resolve to distinct application principals." >&2
    return 1
  }

  role_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${operator_access_token}" \
    "${api_url}/api/v2/access/roles")"
  role_id="$(jq -r '.[] | select(.name == "TenantAdministrator") | .id' <<<"$role_response")"
  [[ -n "$role_id" && "$role_id" != null ]] || {
    echo "The operator could not resolve the built-in TenantAdministrator role." >&2
    return 1
  }

  curl --silent --show-error --fail --output /dev/null \
    --request PUT \
    --header "Authorization: Bearer ${operator_access_token}" \
    --header 'Content-Type: application/json' \
    --data "$(jq -nc --arg roleId "$role_id" --argjson tenantId "$tenant_id" '{roleId:$roleId,tenantId:$tenantId}')" \
    "${api_url}/api/v2/access/principals/${tenant_admin_principal_id}/assignments"

  tenant_admin_scope="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${tenant_admin_token}" \
    "${api_url}/api/v2/access/tenants")"
  jq -e --argjson expectedTenant "$tenant_id" \
    'length == 1 and .[0].tenantId == $expectedTenant' >/dev/null <<<"$tenant_admin_scope" || {
    echo "The tenant-administrator principal did not receive exactly its assigned tenant scope." >&2
    return 1
  }

  unprivileged_scope="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${unprivileged_token}" \
    "${api_url}/api/v2/access/tenants")"
  jq -e 'length == 0' >/dev/null <<<"$unprivileged_scope" || {
    echo "The OIDC principal without a persisted role unexpectedly received tenant scope." >&2
    return 1
  }

  verify_directory_denial "$tenant_admin_token" "/api/v2/client-presence/?tenantId=${tenant_id}"
  verify_directory_denial "$tenant_admin_token" "/api/v2/client-presence/?tenantId=${unassigned_tenant_id}"
  verify_directory_denial "$unprivileged_token" "/api/v2/client-presence/"
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
  printf 'Last gateway command state: %s\n' "${command_state:-unavailable}" >&2
  docker logs "$gateway_client" >&2 || true
  docker cp "${gateway_client}:/var/lib/netratel/logs" - >&2 || true
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

dispatch_disposable_command() {
  local access_token="$1" payload="$2" correlation_id="$3"
  local request_json http_status response_code presence_status presence_state
  request_json="$(jq -nc --arg payload "$payload" --arg correlation_id "$correlation_id" \
    '{taskType:"exec-shell-cmd", payloadJson:$payload, environment:0, correlationId:$correlation_id}')"
  http_status="$(curl --connect-timeout 5 --max-time 15 --silent --output "$command_response_path" \
    --write-out '%{http_code}' --header "Authorization: Bearer ${access_token}" \
    --header 'Content-Type: application/json' --data "$request_json" \
    "${api_url}/api/v2/agents/${tenant_id}/${agent_id}/commands" 2>/dev/null || true)"

  if [[ "$http_status" == 202 ]]; then
    cat "$command_response_path"
    return 0
  fi

  echo "Disposable command dispatch failed with HTTP ${http_status:-000}." >&2
  if [[ "$http_status" == 409 ]]; then
    response_code="$(jq -er '.code | select(. == "agent_command_session_unavailable")' \
      "$command_response_path" 2>/dev/null || true)"
    [[ -n "$response_code" ]] || response_code="unavailable"
    read -r presence_status presence_state < <(read_current_gateway_presence "$access_token")
    printf 'Gateway response code=%s; current Client presence HTTP=%s state=%s; container state=%s\n' \
      "$response_code" "${presence_status:-unavailable}" "${presence_state:-unavailable}" \
      "$(gateway_client_container_state)" >&2
  fi
  # Never publish raw Client logs here: they may contain enrollment or request
  # data. Retain only bounded, allowlisted transport/lifecycle classifications.
  docker logs --tail 500 "$gateway_client" 2>&1 |
    python3 "$root/tools/ci/summarize-gateway-log.py" >&2 || true
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
    --token-url "$oidc_loopback_token_url" \
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
    --arg token_url "$oidc_loopback_token_url" \
    '{apiBaseUrl:$api_base_url,oidcTokenUrl:$token_url,oidcClientId:"netratel-cli-smoke-client",oidcUsername:"netratel-mcp-smoke",oidcAppPassword:"synthetic-compose-only-password",oidcScope:"netratel.api"}' \
    > "$mcp_stdio_config_path"
  chmod 600 "$mcp_stdio_config_path"

  set +e
  mcp_response="$(NETRATEL_MCP_CONFIG="$mcp_stdio_config_path" NETRATEL_MCP_INSTANCE=dev \
    python3 - "$mcp_assembly" "$mcp_stdio_error_path" <<'PY'
import json, os, select, subprocess, sys, time

requests = (
    b'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"compose-archive-smoke","version":"1"}}}\n',
    b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n',
    b'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"netratel_tenants","arguments":{"operation":"list"}}}\n',
)
with open(sys.argv[2], "wb") as errors:
    process = subprocess.Popen(["dotnet", sys.argv[1]], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=errors, env=os.environ.copy())
    for request in requests:
        process.stdin.write(request)
    process.stdin.flush()
    pending = b""
    responses = []
    response_ids = set()
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline and response_ids != {1, 2}:
        readable, _, _ = select.select([process.stdout], [], [], max(0, deadline - time.monotonic()))
        if not readable:
            break
        chunk = os.read(process.stdout.fileno(), 65536)
        if not chunk:
            break
        pending += chunk
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            if line.strip():
                responses.append(line.rstrip(b"\r"))
                try:
                    response_id = json.loads(line).get("id")
                except (ValueError, AttributeError):
                    response_id = None
                if response_id in (1, 2):
                    response_ids.add(response_id)
    process.stdin.close()
    try:
        status = process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        status = process.wait(timeout=5)
    for response in responses:
        sys.stdout.buffer.write(response + b"\n")
    raise SystemExit(status)
PY
  )"
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

if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  source_images_override_path="$(mktemp --suffix=.yaml)"
  python3 - "$NETRATEL_SOURCE_STACK_IMAGES" "$source_images_override_path" <<'PY'
import json, re, subprocess, sys
from pathlib import Path
record = json.loads(Path(sys.argv[1]).read_text())
if (record['sourceSha'], record['productVersion']) != (
        subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
        subprocess.check_output(['python3', 'tools/ci/product-version.py'], text=True).strip()):
    raise ValueError('Prepared source images differ from the selected deployment')
lines = ['services:']
for role in ('api', 'web', 'migrations'):
    image = record['roles'][role]
    if image['reference'] != f'netratel-{role}:manual-deployment' or not re.fullmatch('sha256:[a-f0-9]{64}', image['Id']):
        raise ValueError('Invalid prepared source image role')
    lines += [f'  {role}:', f'    image: {image["reference"]}', '    pull_policy: never']
Path(sys.argv[2]).write_text('\n'.join(lines) + '\n')
PY
  compose_args+=(-f "$source_images_override_path")
  compose_up_args=(--no-build --detach)
fi

stage="starting disposable Compose services"
if ! docker compose --project-name "$project" "${compose_args[@]}" up "${compose_up_args[@]}"; then
  docker compose --project-name "$project" "${compose_args[@]}" ps --all >&2 || true
  docker compose --project-name "$project" "${compose_args[@]}" \
    logs --no-color --tail 200 migrations >&2 || true
  exit 1
fi
if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  for role in api web migrations; do
    actual_container="$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q "$role")"
    expected_image="$(jq -r --arg role "$role" '.roles[$role].Id' "$NETRATEL_SOURCE_STACK_IMAGES")"
    [[ -n "$actual_container" && "$(docker inspect --format '{{.Image}}' "$actual_container")" == "$expected_image" ]] || {
      echo "The actual ${role} container does not use its prepared production image." >&2; exit 1;
    }
  done
fi
if [[ "$mode" == source ]]; then
  stage="recording the actual Web publish dependency boundary"
  web_container="$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q web)"
  [[ -n "$web_container" ]] || { echo "Web container was not created." >&2; exit 1; }
  python3 - "$web_container" <<'PY'
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tarfile

container = sys.argv[1]
files, dependencies, static_endpoints = [], None, None
with subprocess.Popen(["docker", "cp", container + ":/app/.", "-"], stdout=subprocess.PIPE) as copied:
    with tarfile.open(fileobj=copied.stdout, mode="r|") as archive:
        for entry in archive:
            if not entry.isfile():
                continue
            content = archive.extractfile(entry).read()
            files.append({"path": entry.name, "bytes": entry.size, "sha256": hashlib.sha256(content).hexdigest()})
            if Path(entry.name).name == "NetRatel.Web.deps.json":
                dependencies = json.loads(content)
            if Path(entry.name).name == "NetRatel.Web.staticwebassets.endpoints.json":
                static_endpoints = json.loads(content)["Endpoints"]
    if copied.wait() != 0:
        raise SystemExit("Unable to inventory the actual Web application files.")
if not files or dependencies is None or not static_endpoints:
    raise SystemExit("Web distribution inventory, runtime dependencies or static endpoint manifest is missing.")
paths = {Path(entry["path"]).as_posix() for entry in files}
missing_static_targets = sorted({endpoint["AssetFile"] for endpoint in static_endpoints
    if Path(endpoint["AssetFile"]).is_absolute() or ".." in Path(endpoint["AssetFile"]).parts
    or "wwwroot/" + endpoint["AssetFile"] not in paths})
monaco_prefix = "wwwroot/_content/BlazorMonaco/"
monaco_files = [entry for entry in files if Path(entry["path"]).as_posix().startswith(monaco_prefix)]
nls_prefix = monaco_prefix + "lib/monaco-editor/min/vs/nls.messages."
alternate_nls = [entry["path"] for entry in monaco_files
    if Path(entry["path"]).as_posix().startswith(nls_prefix)
    and Path(entry["path"]).name != "nls.messages.js.js"]
required_nls = {monaco_prefix + "lib/monaco-editor/min/vs/" + name
    for name in ["nls.messages-loader.js", "nls.messages.js.js"]}
libraries = sorted(dependencies["libraries"])
unexpected = [name for name in libraries if name.split("/")[0] == "NetRatel.Infrastructure"
              or name.startswith(("Microsoft.EntityFrameworkCore", "Npgsql"))]
image = subprocess.check_output(["docker", "inspect", "--format", "{{.Image}}", container], text=True).strip()
image_bytes = int(subprocess.check_output(["docker", "image", "inspect", "--format", "{{.Size}}", image], text=True))
receipt = {"sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
           "imageId": image, "uncompressedImageBytes": image_bytes,
           "applicationBytes": sum(entry["bytes"] for entry in files), "libraries": libraries,
           "staticAssetEndpoints": len(static_endpoints), "missingStaticAssetTargets": missing_static_targets,
           "monacoFiles": len(monaco_files), "monacoBytes": sum(entry["bytes"] for entry in monaco_files),
           "alternateMonacoNlsFiles": alternate_nls,
           "unexpectedDatabaseDependencies": unexpected, "files": sorted(files, key=lambda entry: entry["path"])}
output = Path("TestResults/oidc-compose/web-distribution.json")
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(receipt, indent=2) + "\n")
print(f"Actual Web image: {receipt['applicationBytes']} application bytes across {len(files)} files; {len(libraries)} runtime libraries.")
if unexpected:
    raise SystemExit("Web image still contains database dependencies: " + ", ".join(unexpected))
if missing_static_targets:
    raise SystemExit("Web static endpoints target missing application files: " + ", ".join(missing_static_targets))
if alternate_nls or not required_nls.issubset(paths):
    raise SystemExit("Web image must retain the English NLS bundle and loader without alternate locale bundles.")
PY
fi
stage="waiting for migrations"
wait_for_migrations

stage="waiting for disposable OIDC issuer"
curl --retry 20 --retry-connrefused --fail --silent --show-error \
  --resolve "$oidc_resolve" "${oidc_curl_args[@]}" "${oidc_authority}/isalive" >/dev/null

stage="waiting for the Web application"
wait_for_web
stage="resolving the HTTPS Web ingress"
web_proxy_address="$(docker compose --project-name "$project" "${compose_args[@]}" port web-proxy 9444)"
if [[ "$auth_environment" == Production ]]; then
  web_public_url="https://${web_proxy_address}"
  web_curl_args=(--cacert "$trust_bundle_path")
fi
stage="checking anonymous API rejection"
wait_for_status 401 "${web_public_url}/api/v1/tenants"

unauthenticated_status="$(curl --silent "${web_curl_args[@]}" --output /dev/null --write-out '%{http_code}' "${web_public_url}/api/v1/tenants")"
[[ "$unauthenticated_status" == 401 ]] || {
  echo "Expected the protected API route to reject an anonymous request, got ${unauthenticated_status}." >&2
  exit 1
}

stage="starting interactive OIDC challenge"
authorization_location="$(curl --silent --show-error --output /dev/null --dump-header - \
  "${web_curl_args[@]}" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  "${web_public_url}/auth/oidc?returnUrl=%2Ftenants" \
  | awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }')"
[[ "$authorization_location" == "${oidc_authority}/authorize"* ]] || {
  echo "The Web application did not challenge the configured generic OIDC provider." >&2
  exit 1
}

stage="loading OIDC authorization page"
curl --silent --show-error --fail --resolve "$oidc_resolve" "${oidc_curl_args[@]}" \
  --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  "$authorization_location" >/dev/null

stage="authenticating disposable OIDC operator"
operator_claims_json="$(oidc_smoke_claims_for_subject "$root/tests/compose/oidc-smoke-login.html" netratel-test-operator)" || {
  echo "The OIDC smoke fixture has no claims profile for the operator." >&2
  exit 1
}
callback_response="$(curl --silent --show-error --dump-header - \
  --resolve "$oidc_resolve" "${oidc_curl_args[@]}" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  --data-urlencode 'username=netratel-test-operator' \
  --data-urlencode "claims=${operator_claims_json}" \
  "$authorization_location")"
callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$callback_response")"

if [[ -n "$callback_location" ]]; then
  [[ "$callback_location" == "${web_public_url}/signin-oidc"* ]] || {
    echo "The generic OIDC provider returned an unexpected authorization-code callback: ${callback_location}." >&2
    exit 1
  }

  authenticated_status="$(curl --silent --show-error \
    "${web_curl_args[@]}" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    --output /dev/null --write-out '%{http_code}' "$callback_location")"
else
  callback_action="$(sed -n 's/.*<form action="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  callback_code="$(sed -n 's/.*name="code" value="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  callback_state="$(sed -n 's/.*name="state" value="\([^"]*\)".*/\1/p' <<<"$callback_response" | head -n 1)"
  [[ "$callback_action" == "${web_public_url}/signin-oidc" && -n "$callback_code" && -n "$callback_state" ]] || {
    echo "The generic OIDC provider did not return a usable authorization-code callback." >&2
    exit 1
  }

  authenticated_status="$(curl --silent --show-error \
    "${web_curl_args[@]}" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
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
  "${web_curl_args[@]}" --output /dev/null --write-out '%{http_code}' "${web_public_url}/api/v1/tenants")"
[[ "$protected_status" == 200 ]] || {
  echo "The authenticated OIDC session could not access the protected API route, got ${protected_status}." >&2
  docker compose --project-name "$project" "${compose_args[@]}" \
    logs --no-color --tail 200 web api >&2 || true
  exit 1
}

stage="creating disposable tenant"
tenant_response="$(curl --silent --show-error --fail "${web_curl_args[@]}" --cookie "$cookie_jar" \
  --header 'Content-Type: application/json' \
  --data '{"name":"compose-smoke","description":"Disposable Compose smoke tenant","location":"test","domains":[],"autoUpdate":false}' \
  "${web_public_url}/api/v1/tenants/")"
tenant_id="$(jq -r '.tenantId // empty' <<<"$tenant_response")"
[[ "$tenant_id" =~ ^[1-9][0-9]*$ ]] || { echo "Compose smoke could not create a synthetic tenant." >&2; exit 1; }

stage="verifying packaged CLI read"
verify_cli_archive_scoped_read "${NETRATEL_CLI_SMOKE_ARCHIVE:-}"
stage="verifying packaged stdio MCP read"
verify_mcp_stdio_archive_scoped_read "${NETRATEL_MCP_STDIO_SMOKE_ARCHIVE:-}"

stage="issuing disposable Client enrollment"
enrollment_response="$(curl --silent --show-error --fail "${web_curl_args[@]}" --cookie "$cookie_jar" \
  --header 'Content-Type: application/json' \
  --data '{"validForMinutes":5,"maxUses":1,"note":"Disposable Compose smoke enrollment"}' \
  "${web_public_url}/api/v1/tenants/${tenant_id}/enrollment-codes")"
enrollment_code="$(jq -r '.enrollmentCode // empty' <<<"$enrollment_response")"
[[ -n "$enrollment_code" ]] || { echo "Compose smoke could not issue a short-lived enrollment code." >&2; exit 1; }

docker volume create "$client_volume" >/dev/null
# Docker creates named volumes as root-owned.  Prepare the disposable state
# volume once so the published image still runs under its normal unprivileged
# `netratel` account for enrollment, token renewal, and gateway presence.
docker run --rm --user 0:0 --volume "${client_volume}:/var/lib/netratel" \
  --entrypoint /bin/sh "$client_image" -c 'chown -R netratel:netratel /var/lib/netratel'
stage="enrolling the disposable Client"
if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  python3 tools/ci/record-stack-image-comparison.py readiness-start --results "$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")"
fi
docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
  "$client_image" --api http://api:9222 --enroll "$enrollment_code"
record_client_acceptance enrollment
stage="validating disposable Client authentication"
set +e
auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
  "$client_image" --api http://api:9222 --auth-check 2>&1)"
auth_check_status=$?
set -e
if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
  jq -n --argjson exitCode "$auth_check_status" '{exitCode:$exitCode}' > "$NETRATEL_CLIENT_ARCHIVE_EVIDENCE/initial-auth.json"
fi
printf '%s\n' "$auth_check_output"
if (( auth_check_status != 0 )); then
  stage="Client auth check exited ${auth_check_status}: $(grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1)"
  exit "$auth_check_status"
fi
agent_id="$(sed -nE 's/.*[Aa]gent[Ii]d=([0-9A-Fa-f-]{36}).*/\1/ip' <<<"$auth_check_output" | head -n 1)"
[[ "$agent_id" =~ ^[0-9A-Fa-f-]{36}$ ]] || {
  echo "Disposable Client auth check did not report its enrolled agent ID." >&2
  exit 1
}
echo "Disposable Client authenticated as agent ${agent_id}."
record_client_acceptance persisted-authentication

if [[ -n "$inventory_evidence_directory" ]]; then
  operator_access_token="$(request_oidc_access_token netratel-test-operator compose-smoke-api)"
  prepare_software_inventory_acceptance "$operator_access_token" "$inventory_evidence_directory" "$inventory_state_boundary"
fi
stage="establishing Client gateway sessions"
start_gateway_client
wait_for_gateway_sessions
record_client_acceptance initial-gateway-sessions

stage="requesting direct operator token"
if [[ -z "${operator_access_token:-}" ]]; then
  operator_access_token="$(request_oidc_access_token netratel-test-operator compose-smoke-api)"
fi
if [[ "$auth_environment" == Production ]]; then
  stage="provisioning scoped OIDC directory identities"
  prepare_oidc_directory_authorization_fixtures "$operator_access_token"
fi
stage="running browser OIDC rehearsal"
run_browser_oidc_smoke
stage="waiting for Client telemetry"
wait_for_telemetry "$operator_access_token"
record_client_acceptance telemetry
stage="checking current Client gateway presence"
wait_for_current_gateway_presence "$operator_access_token"
if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  python3 tools/ci/record-stack-image-comparison.py readiness-end --results "$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")"
fi
record_client_acceptance initial-authoritative-presence
if [[ "$mode" == source && -n "$log_evidence_directory" ]]; then
  verify_runtime_log_acceptance "$operator_access_token" "$log_evidence_directory" initial
fi
if [[ -n "$inventory_evidence_directory" ]]; then
  verify_software_inventory_acceptance "$operator_access_token" "$inventory_evidence_directory" "$inventory_state_boundary"
fi
# The public Linux client image deliberately includes Bash, and the gateway
# command contract accepts an explicit executor. Do not rely on the image's
# default /bin/sh implementation for this end-to-end execution assertion.
command_payload='{"preferred":3,"command":"printf netratel-compose-smoke","timeoutSeconds":10}'
stage="waiting for disposable command execution"
command_response="$(dispatch_disposable_command "$operator_access_token" "$command_payload" compose-smoke-harmless)"
command_id="$(jq -r '.commandId // empty' <<<"$command_response")"
[[ "$command_id" =~ ^[0-9a-f]{32}$ ]] || {
  echo "The command authority did not return a valid disposable command ID." >&2
  exit 1
}
wait_for_command_status "$operator_access_token" "$command_id" 4
record_client_acceptance command-completed

cancel_payload='{"command":"sleep 20","timeoutSeconds":30}'
stage="waiting for disposable command cancellation"
cancel_response="$(dispatch_disposable_command "$operator_access_token" "$cancel_payload" compose-smoke-cancel)"
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
record_client_acceptance command-cancelled

retain_gateway_summary before-restart
docker rm -f "$gateway_client" >/dev/null
if [[ -n "${NETRATEL_CLIENT_ARCHIVE_EVIDENCE:-}" ]]; then
  stage="validating persisted Client identity after restart"
  set +e
  restart_auth_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$client_image" --api http://api:9222 --auth-check 2>&1)"
  restart_auth_status=$?
  set -e
  jq -n --argjson exitCode "$restart_auth_status" '{exitCode:$exitCode}' > "$NETRATEL_CLIENT_ARCHIVE_EVIDENCE/restart-auth.json"
  (( restart_auth_status == 0 )) || { echo "Persisted Client auth check failed after restart (exit ${restart_auth_status})." >&2; exit "$restart_auth_status"; }
  restarted_agent_id="$(sed -nE 's/.*[Aa]gent[Ii]d=([0-9A-Fa-f-]{36}).*/\1/ip' <<<"$restart_auth_output" | head -n 1)"
  [[ "${restarted_agent_id,,}" == "${agent_id,,}" ]] || { echo 'Client identity changed after restart.' >&2; exit 1; }
  record_client_acceptance restart-persisted-identity
fi
stage="reestablishing Client gateway sessions after restart"
start_gateway_client
wait_for_gateway_sessions
if [[ -n "$inventory_evidence_directory" ]]; then
  wait_for_current_gateway_presence "$operator_access_token"
  record_client_acceptance restarted-authoritative-presence
  verify_software_inventory_after_restart "$operator_access_token" "$inventory_evidence_directory"
fi
if [[ "$mode" == source && -n "$log_evidence_directory" ]]; then
  verify_runtime_log_acceptance "$operator_access_token" "$log_evidence_directory" restarted
fi
retain_gateway_summary after-restart
if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  stage="sampling post-workload process RSS"
  python3 tools/ci/record-stack-image-comparison.py memory \
    --results "$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")" \
    --api-container "$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q api)" \
    --web-container "$(docker compose --project-name "$project" "${compose_args[@]}" ps -a -q web)" \
    --client-container "$gateway_client"
fi
docker rm -f "$gateway_client" >/dev/null

stage="validating Web logout"
logout_headers="$(curl --silent --show-error --dump-header - --output /dev/null \
  "${web_curl_args[@]}" --cookie "$cookie_jar" --cookie-jar "$cookie_jar" "${web_public_url}/auth/logout")"
grep -qi '^set-cookie:.*\.AspNetCore\.Cookies=;' <<<"$logout_headers" || {
  echo "Logout did not clear the Web authentication cookie." >&2
  exit 1
}

record_client_acceptance source-oidc-deployment-completed
if [[ "$mode" == source && -n "${NETRATEL_SOURCE_STACK_IMAGES:-}" ]]; then
  stage="recording complete source and published production image sets"
  python3 tools/ci/record-stack-image-comparison.py capture --results "$(dirname "$NETRATEL_SOURCE_STACK_IMAGES")"
fi
echo "${mode} Compose generic OIDC smoke test passed."
