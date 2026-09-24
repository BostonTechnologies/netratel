#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

source tools/ci/load-prior-release.sh

project="netratel-postgresql-oidc-upgrade-${GITHUB_RUN_ID:-local}-${RANDOM}"
legacy_api_image="${NETRATEL_UPGRADE_PRIOR_API_IMAGE:?set NETRATEL_UPGRADE_PRIOR_API_IMAGE}"
legacy_migrations_image="${NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE}"
legacy_web_image="${NETRATEL_UPGRADE_PRIOR_WEB_IMAGE:?set NETRATEL_UPGRADE_PRIOR_WEB_IMAGE}"
legacy_client_image="${NETRATEL_UPGRADE_PRIOR_CLIENT_IMAGE:?set NETRATEL_UPGRADE_PRIOR_CLIENT_IMAGE}"
legacy_version="${NETRATEL_UPGRADE_PRIOR_VERSION:?set NETRATEL_UPGRADE_PRIOR_VERSION}"
current_api_image="${NETRATEL_UPGRADE_CURRENT_API_IMAGE:?set NETRATEL_UPGRADE_CURRENT_API_IMAGE}"
current_migrations_image="${NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE}"
current_web_image="${NETRATEL_UPGRADE_CURRENT_WEB_IMAGE:?set NETRATEL_UPGRADE_CURRENT_WEB_IMAGE}"
bundle="${NETRATEL_UPGRADE_COMPOSE_BUNDLE:?set NETRATEL_UPGRADE_COMPOSE_BUNDLE}"
bundle_extract_directory="$(mktemp -d)"
agent_key_path="$(mktemp)"
cookie_jar="$(mktemp)"
tls_key_path="$(mktemp)"
tls_certificate_path="$(mktemp)"
tls_bundle_path="$(mktemp --suffix=.pfx)"
stage="initializing ${legacy_version} PostgreSQL/OIDC upgrade smoke"
active_compose=()
legacy_principal_count=""
client_volume="${project}-client-state"
native_directory="$(mktemp -d)"
native_user_created=false
native_image_container=""

[[ -s "$bundle" ]] || { echo "NETRATEL_UPGRADE_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
tar -xzf "$bundle" -C "$bundle_extract_directory"
[[ -f "$bundle_extract_directory/compose.images.yaml" ]] || { echo "The extracted release bundle is missing compose.images.yaml." >&2; exit 1; }

legacy_compose=(docker compose --project-name "$project" -f release/compose.images.yaml -f tests/compose/oidc-smoke.compose.yaml)
current_compose=(docker compose --project-name "$project" -f "$bundle_extract_directory/compose.images.yaml" -f tests/compose/oidc-smoke.compose.yaml)

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=PostgreSQL/OIDC upgrade smoke failed::${stage}" >&2
    if (( ${#active_compose[@]} > 0 )); then
      "${active_compose[@]}" ps --all >&2 || true
      "${active_compose[@]}" logs --no-color --tail 250 postgres migrations api web oidc >&2 || true
    fi
  fi
  if (( ${#active_compose[@]} > 0 )); then
    "${active_compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  fi
  unlink "$agent_key_path" "$tls_key_path" "$tls_certificate_path" "$tls_bundle_path" 2>/dev/null || true
  unlink "$cookie_jar" 2>/dev/null || true
  docker volume rm "$client_volume" >/dev/null 2>&1 || true
  if [[ -n "$native_image_container" ]]; then
    docker rm "$native_image_container" >/dev/null 2>&1 || true
  fi
  if [[ "$native_user_created" == true ]]; then
    sudo userdel --remove netratel >/dev/null 2>&1 || true
  fi
  sudo find "$native_directory" -depth -delete 2>/dev/null || true
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
  local expected_version="$1"
  local expect_current_shell="$2"
  local proxy_address
  proxy_address="$("${active_compose[@]}" port web-proxy 9444)"
  NETRATEL_BROWSER_SMOKE_WEB_URL="$web_url" \
    NETRATEL_BROWSER_SMOKE_PROXY_URL="https://${proxy_address}" \
    NETRATEL_BROWSER_SMOKE_USERNAME=netratel-test-operator \
    NETRATEL_BROWSER_SMOKE_EXPECTED_VERSION="$expected_version" \
    NETRATEL_BROWSER_SMOKE_EXPECT_CURRENT_SHELL="$expect_current_shell" \
    dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build --filter 'FullyQualifiedName~OidcComposeBrowserSmokeTests'
}

durable_oidc_principal_count() {
  "${active_compose[@]}" exec -T postgres psql -U netratel -d netratel -Atqc \
    'SELECT COUNT(*) FROM "ApplicationPrincipals" WHERE "ExternalIssuer" IS NOT NULL AND "ExternalSubject" IS NOT NULL;'
}

request_operator_access_token() {
  local redirect_uri authorization_url response callback_location callback_code token_response access_token
  redirect_uri="http://127.0.0.1:65535/netratel-smoke-callback"
  authorization_url="http://host.docker.internal:${NETRATEL_OIDC_TEST_PORT}/default/authorize?response_type=code&client_id=netratel-smoke-client&redirect_uri=http%3A%2F%2F127.0.0.1%3A65535%2Fnetratel-smoke-callback&scope=openid%20netratel.api&state=postgresql-oidc-upgrade"
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
    "http://host.docker.internal:${NETRATEL_OIDC_TEST_PORT}/default/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  [[ -n "$access_token" ]] || {
    echo "The test OIDC provider did not issue a direct API access token." >&2
    return 1
  }
  printf '%s' "$access_token"
}

seed_historical_oidc_principal() {
  local access_token issuer subject principal_id
  access_token="$(request_operator_access_token)"
  issuer="$(node -e 'const token = process.argv[1]; const payload = token.split(".")[1]; process.stdout.write(JSON.parse(Buffer.from(payload, "base64url").toString("utf8")).iss ?? "")' "$access_token")"
  subject="$(node -e 'const token = process.argv[1]; const payload = token.split(".")[1]; process.stdout.write(JSON.parse(Buffer.from(payload, "base64url").toString("utf8")).sub ?? "")' "$access_token")"
  [[ -n "$issuer" && -n "$subject" ]] || {
    echo "The disposable OIDC token did not contain the verified issuer and subject needed for the historical fixture." >&2
    return 1
  }
  principal_id="$(tr -d '-' </proc/sys/kernel/random/uuid)"
  "${active_compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U netratel -d netratel \
    -v principal_id="$principal_id" -v issuer="$issuer" -v subject="$subject" -q <<'SQL'
-- Published prior release already projects the browser identity. Older fixtures may
-- need the same stable issuer/subject principal seeded explicitly.
INSERT INTO "ApplicationPrincipals" ("Id", "ExternalIssuer", "ExternalSubject", "CreatedAtUtc")
VALUES (:'principal_id', :'issuer', :'subject', CURRENT_TIMESTAMP)
ON CONFLICT ("ExternalIssuer", "ExternalSubject") DO NOTHING;
SQL
}

enroll_legacy_client() {
  local access_token tenant_response tenant_id enrollment_response enrollment_code auth_check_output auth_check_status
  access_token="$(request_operator_access_token)"
  tenant_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" --header 'Content-Type: application/json' \
    --data '{"name":"postgresql-oidc-upgrade","description":"Disposable historical upgrade tenant","location":"test","domains":[],"autoUpdate":false}' \
    "${api_url}/api/v1/tenants/")"
  tenant_id="$(jq -r '.tenantId // empty' <<<"$tenant_response")"
  [[ "$tenant_id" =~ ^[1-9][0-9]*$ ]] || {
    echo "Published ${legacy_version} OIDC authority could not create the historical tenant." >&2
    return 1
  }
  enrollment_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" --header 'Content-Type: application/json' \
    --data '{"validForMinutes":5,"maxUses":1,"note":"Disposable prior-release PostgreSQL/OIDC upgrade enrollment"}' \
    "${api_url}/api/v1/tenants/${tenant_id}/enrollment-codes")"
  enrollment_code="$(jq -r '.enrollmentCode // empty' <<<"$enrollment_response")"
  [[ -n "$enrollment_code" ]] || {
    echo "Published ${legacy_version} OIDC authority could not issue a historical Client enrollment code." >&2
    return 1
  }

  docker volume create "$client_volume" >/dev/null
  docker run --rm --user 0:0 --volume "${client_volume}:/var/lib/netratel" \
    --entrypoint /bin/sh "$legacy_client_image" -c 'chown -R netratel:netratel /var/lib/netratel'
  docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --enroll "$enrollment_code" >/dev/null
  set +e
  auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --auth-check 2>&1)"
  auth_check_status=$?
  set -e
  if (( auth_check_status != 0 )); then
    echo "Published ${legacy_version} Client authentication failed immediately after enrollment." >&2
    grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1 >&2 || true
    return 1
  fi
}

verify_legacy_client_after_upgrade() {
  local auth_check_output auth_check_status
  set +e
  auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --auth-check 2>&1)"
  auth_check_status=$?
  set -e
  if (( auth_check_status != 0 )); then
    echo "The persisted ${legacy_version} Client could not authenticate against the upgraded candidate API." >&2
    grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1 >&2 || true
    return 1
  fi
}

verify_native_legacy_client_after_upgrade() {
  local machine_identity native_status
  if id netratel >/dev/null 2>&1 || [[ -e /var/lib/netratel ]]; then
    echo "The disposable runner already has a netratel account or state directory." >&2
    return 1
  fi
  sudo useradd --system --create-home --home-dir /var/lib/netratel --shell /usr/sbin/nologin netratel
  native_user_created=true
  chmod 755 "$native_directory"
  mkdir -p "$native_directory/app" "$native_directory/state"
  native_image_container="$(docker create "$legacy_client_image")"
  docker cp "${native_image_container}:/app/." "$native_directory/app/"
  docker rm "$native_image_container" >/dev/null
  native_image_container=""
  docker run --rm --volume "${client_volume}:/source:ro" \
    --volume "${native_directory}/state:/target" alpine:3.22 \
    sh -ceu 'cp -a /source/. /target/'
  [[ -s "$native_directory/state/agent.dat" && -s "$native_directory/state/.netratel-credential-machine-id" ]] || {
    echo "The published client did not persist a transferable credential identity." >&2
    return 1
  }
  sudo cp -a "$native_directory/state/." /var/lib/netratel/
  sudo chown -R netratel:netratel /var/lib/netratel
  machine_identity="$(cat "$native_directory/state/.netratel-credential-machine-id")"
  if sudo -u netratel env "NetRatel_CREDENTIAL_MACHINE_ID=${machine_identity}" \
      "$native_directory/app/NetRatel.Client" --api "$api_url" --auth-check \
      >"$native_directory/native-auth-check.log" 2>&1; then
    native_status=0
  else
    native_status=$?
  fi
  if (( native_status != 0 )); then
    echo "Published ${legacy_version} native Client could not authenticate with its existing identity after the candidate API upgrade." >&2
    return 1
  fi
}

export POSTGRES_PASSWORD=synthetic-postgresql-oidc-upgrade-postgres-password
export OIDC_AUTHORITY=https://issuer.example.invalid
export OIDC_CLIENT_ID=synthetic-postgresql-oidc-upgrade-client
export OIDC_API_SCOPE=netratel.api
export OIDC_API_AUDIENCE=netratel.api
export OIDC_TOKEN_ENDPOINT=https://issuer.example.invalid/oauth/token
export OIDC_ADMIN_GROUP_ID=synthetic-postgresql-oidc-upgrade-group
export OIDC_CLIENT_SECRET=synthetic-postgresql-oidc-upgrade-oidc-secret
export NETRATEL_WEB_PORT="${NETRATEL_UPGRADE_OIDC_WEB_PORT:-18084}"
export NETRATEL_OIDC_TEST_PORT="${NETRATEL_UPGRADE_OIDC_PORT:-18085}"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$agent_key_path"
export NETRATEL_SMOKE_TLS_CERT_PASSWORD=synthetic-postgresql-oidc-upgrade-certificate-password
export NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"
export NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
export NETRATEL_SMOKE_TLS_CERT_PATH="$tls_bundle_path"
export NETRATEL_SMOKE_TLS_CERTIFICATE_PATH="$tls_certificate_path"
export NETRATEL_SMOKE_TLS_KEY_PATH="$tls_key_path"
web_url="http://127.0.0.1:${NETRATEL_WEB_PORT}"
api_url="http://127.0.0.1:${NETRATEL_API_TEST_PORT:-9222}"
oidc_resolve="host.docker.internal:${NETRATEL_OIDC_TEST_PORT}:127.0.0.1"

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

stage="starting published ${legacy_version} PostgreSQL/OIDC images"
configure_images "$legacy_api_image" "$legacy_migrations_image" "$legacy_web_image"
active_compose=("${legacy_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the published ${legacy_version} OIDC browser journey"
run_browser_oidc_smoke "$legacy_version" false
stage="recording an existing ${legacy_version} PostgreSQL OIDC principal fixture"
seed_historical_oidc_principal
legacy_principal_count="$(durable_oidc_principal_count)"
[[ "$legacy_principal_count" == 1 ]] || { echo "The historical PostgreSQL fixture did not retain exactly one external issuer/subject principal." >&2; exit 1; }
stage="enrolling and authenticating a published ${legacy_version} Client"
enroll_legacy_client

stage="upgrading the published PostgreSQL/OIDC state with extracted candidate images"
"${active_compose[@]}" down --remove-orphans
configure_images "$current_api_image" "$current_migrations_image" "$current_web_image"
active_compose=("${current_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the upgraded PostgreSQL/OIDC browser journey"
run_browser_oidc_smoke "v$(python3 tools/ci/product-version.py)" true
[[ "$(durable_oidc_principal_count)" == "$legacy_principal_count" ]] || {
  echo "The upgraded PostgreSQL/OIDC stack did not retain the durable ${legacy_version} external principal set." >&2
  exit 1
}
stage="authenticating the persisted published ${legacy_version} Client against candidate images"
verify_legacy_client_after_upgrade
stage="authenticating the published ${legacy_version} Client natively with its persisted identity"
verify_native_legacy_client_after_upgrade
echo "Published ${legacy_version} PostgreSQL/OIDC and persisted Client authentication continuity passed."
