#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-rc3-postgresql-oidc-upgrade-${GITHUB_RUN_ID:-local}-${RANDOM}"
legacy_api_image="${NETRATEL_RC3_LEGACY_API_IMAGE:?set NETRATEL_RC3_LEGACY_API_IMAGE}"
legacy_migrations_image="${NETRATEL_RC3_LEGACY_MIGRATIONS_IMAGE:?set NETRATEL_RC3_LEGACY_MIGRATIONS_IMAGE}"
legacy_web_image="${NETRATEL_RC3_LEGACY_WEB_IMAGE:?set NETRATEL_RC3_LEGACY_WEB_IMAGE}"
current_api_image="${NETRATEL_RC3_CURRENT_API_IMAGE:?set NETRATEL_RC3_CURRENT_API_IMAGE}"
current_migrations_image="${NETRATEL_RC3_CURRENT_MIGRATIONS_IMAGE:?set NETRATEL_RC3_CURRENT_MIGRATIONS_IMAGE}"
current_web_image="${NETRATEL_RC3_CURRENT_WEB_IMAGE:?set NETRATEL_RC3_CURRENT_WEB_IMAGE}"
bundle="${NETRATEL_RC3_UPGRADE_COMPOSE_BUNDLE:?set NETRATEL_RC3_UPGRADE_COMPOSE_BUNDLE}"
bundle_extract_directory="$(mktemp -d)"
agent_key_path="$(mktemp)"
tls_key_path="$(mktemp)"
tls_certificate_path="$(mktemp)"
tls_bundle_path="$(mktemp --suffix=.pfx)"
stage="initializing rc.3 PostgreSQL/OIDC upgrade smoke"
active_compose=()
legacy_principal_count=""

[[ -s "$bundle" ]] || { echo "NETRATEL_RC3_UPGRADE_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
tar -xzf "$bundle" -C "$bundle_extract_directory"
[[ -f "$bundle_extract_directory/compose.images.yaml" ]] || { echo "The extracted release bundle is missing compose.images.yaml." >&2; exit 1; }

legacy_compose=(docker compose --project-name "$project" -f release/compose.images.yaml -f tests/compose/oidc-smoke.compose.yaml)
current_compose=(docker compose --project-name "$project" -f "$bundle_extract_directory/compose.images.yaml" -f tests/compose/oidc-smoke.compose.yaml)

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=rc.3 PostgreSQL/OIDC upgrade smoke failed::${stage}" >&2
    if (( ${#active_compose[@]} > 0 )); then
      "${active_compose[@]}" ps --all >&2 || true
      "${active_compose[@]}" logs --no-color --tail 250 postgres migrations api web oidc >&2 || true
    fi
  fi
  if (( ${#active_compose[@]} > 0 )); then
    "${active_compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  fi
  unlink "$agent_key_path" "$tls_key_path" "$tls_certificate_path" "$tls_bundle_path" 2>/dev/null || true
  find "$bundle_extract_directory" -depth -delete 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

configure_images() {
  export NETRATEL_API_IMAGE="$1"
  export NETRATEL_MIGRATIONS_IMAGE="$2"
  export NETRATEL_WEB_IMAGE="$3"
}

wait_for_migrations() {
  local migration_id status
  migration_id="$("${active_compose[@]}" ps -a -q migrations)"
  [[ -n "$migration_id" ]] || { echo "Migration container was not created." >&2; return 1; }
  for _ in $(seq 1 90); do
    status="$(docker inspect --format '{{.State.Status}}' "$migration_id")"
    if [[ "$status" == exited ]]; then
      [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]] || return 1
      return 0
    fi
    sleep 1
  done
  echo "Migration container did not complete within 90 seconds." >&2
  return 1
}

wait_for_web() {
  for _ in $(seq 1 90); do
    if curl --connect-timeout 2 --silent --output /dev/null --write-out '%{http_code}' "$web_url/" | grep -Eq '^(200|302)$'; then
      return 0
    fi
    sleep 1
  done
  echo "The Web application did not become ready." >&2
  return 1
}

run_browser_oidc_smoke() {
  local proxy_address
  proxy_address="$("${active_compose[@]}" port web-proxy 9444)"
  NETRATEL_BROWSER_SMOKE_WEB_URL="$web_url" \
    NETRATEL_BROWSER_SMOKE_PROXY_URL="https://${proxy_address}" \
    NETRATEL_BROWSER_SMOKE_USERNAME=netratel-test-operator \
    dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build --filter 'FullyQualifiedName~OidcComposeBrowserSmokeTests'
}

durable_oidc_principal_count() {
  "${active_compose[@]}" exec -T postgres psql -U netratel -d netratel -Atqc \
    'SELECT COUNT(*) FROM "ApplicationPrincipals" WHERE "ExternalIssuer" IS NOT NULL AND "ExternalSubject" IS NOT NULL;'
}

export POSTGRES_PASSWORD=synthetic-rc3-upgrade-postgres-password
export OIDC_AUTHORITY=https://issuer.example.invalid
export OIDC_CLIENT_ID=synthetic-rc3-upgrade-client
export OIDC_API_SCOPE=netratel.api
export OIDC_API_AUDIENCE=netratel.api
export OIDC_TOKEN_ENDPOINT=https://issuer.example.invalid/oauth/token
export OIDC_ADMIN_GROUP_ID=synthetic-rc3-upgrade-group
export OIDC_CLIENT_SECRET=synthetic-rc3-upgrade-oidc-secret
export NETRATEL_WEB_PORT="${NETRATEL_RC3_UPGRADE_WEB_PORT:-18084}"
export NETRATEL_OIDC_TEST_PORT="${NETRATEL_RC3_UPGRADE_OIDC_PORT:-18085}"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$agent_key_path"
export NETRATEL_SMOKE_TLS_CERT_PASSWORD=synthetic-rc3-upgrade-certificate-password
export NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"
export NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
export NETRATEL_SMOKE_TLS_CERT_PATH="$tls_bundle_path"
export NETRATEL_SMOKE_TLS_CERTIFICATE_PATH="$tls_certificate_path"
export NETRATEL_SMOKE_TLS_KEY_PATH="$tls_key_path"
web_url="http://127.0.0.1:${NETRATEL_WEB_PORT}"

openssl ecparam -name prime256v1 -genkey -noout -out "$agent_key_path"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=gateway' \
  -keyout "$tls_key_path" -out "$tls_certificate_path" >/dev/null 2>&1
openssl pkcs12 -export -out "$tls_bundle_path" -inkey "$tls_key_path" -in "$tls_certificate_path" \
  -passout "pass:${NETRATEL_SMOKE_TLS_CERT_PASSWORD}" >/dev/null 2>&1
chmod 0644 "$agent_key_path" "$tls_key_path" "$tls_certificate_path" "$tls_bundle_path"

stage="building the OIDC browser probe"
dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
pwsh src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium

stage="starting published rc.3 PostgreSQL/OIDC images"
configure_images "$legacy_api_image" "$legacy_migrations_image" "$legacy_web_image"
active_compose=("${legacy_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the published rc.3 OIDC browser journey"
run_browser_oidc_smoke
legacy_principal_count="$(durable_oidc_principal_count)"
[[ "$legacy_principal_count" =~ ^[1-9][0-9]*$ ]] || { echo "Published rc.3 OIDC login did not persist an external principal." >&2; exit 1; }

stage="upgrading the published PostgreSQL/OIDC state with extracted candidate images"
"${active_compose[@]}" down --remove-orphans
configure_images "$current_api_image" "$current_migrations_image" "$current_web_image"
active_compose=("${current_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the upgraded PostgreSQL/OIDC browser journey"
run_browser_oidc_smoke
[[ "$(durable_oidc_principal_count)" == "$legacy_principal_count" ]] || {
  echo "The upgraded PostgreSQL/OIDC stack did not retain the durable rc.3 external principal set." >&2
  exit 1
}
