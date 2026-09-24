#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

bundle="${NETRATEL_UPGRADE_COMPOSE_BUNDLE:?set NETRATEL_UPGRADE_COMPOSE_BUNDLE}"
legacy_api="${NETRATEL_UPGRADE_PRIOR_API_IMAGE:?set NETRATEL_UPGRADE_PRIOR_API_IMAGE}"
legacy_migrations="${NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE}"
legacy_web="${NETRATEL_UPGRADE_PRIOR_WEB_IMAGE:?set NETRATEL_UPGRADE_PRIOR_WEB_IMAGE}"
current_api="${NETRATEL_UPGRADE_CURRENT_API_IMAGE:?set NETRATEL_UPGRADE_CURRENT_API_IMAGE}"
current_migrations="${NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE}"
current_web="${NETRATEL_UPGRADE_CURRENT_WEB_IMAGE:?set NETRATEL_UPGRADE_CURRENT_WEB_IMAGE}"
project="netratel-postgresql-local-upgrade-${GITHUB_RUN_ID:-local}-${RANDOM}"
web_port="${NETRATEL_UPGRADE_LOCAL_WEB_PORT:-18082}"
web_url="http://127.0.0.1:${web_port}"
api_port="${NETRATEL_UPGRADE_LOCAL_API_PORT:-18083}"
api_url="http://127.0.0.1:${api_port}"
bundle_dir="$(mktemp -d)"
key_dir="$(mktemp -d)"
cookie_jar="$(mktemp)"
key_path="$key_dir/agent-auth-private.pem"
prior_version="${NETRATEL_UPGRADE_PRIOR_VERSION:?set NETRATEL_UPGRADE_PRIOR_VERSION}"
stage="preparing ${prior_version} PostgreSQL/local upgrade"
admin_email="local-upgrade-admin@example.test"
admin_password="synthetic local upgrade passphrase"
tenant_name="Legacy local upgrade tenant"
cli_dir=""

[[ -s "$bundle" ]] || { echo "Review Compose bundle is missing." >&2; exit 1; }
tar -xzf "$bundle" -C "$bundle_dir"
[[ -f "$bundle_dir/compose.images.yaml" ]] || { echo "Review bundle has no image Compose recipe." >&2; exit 1; }
compose=(docker compose --project-name "$project" -f "$bundle_dir/compose.images.yaml" \
  -f "$root/tests/compose/postgresql-local-upgrade-api.compose.yaml")

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=PostgreSQL/local upgrade smoke failed::${stage}" >&2
    "${compose[@]}" ps --all >&2 || true
    "${compose[@]}" logs --no-color --tail 200 migrations api web >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "${project}_api-data" "${project}_web-keys" >/dev/null 2>&1 || true
  find "$bundle_dir" "$key_dir" -depth -delete 2>/dev/null || true
  if [[ -n "$cli_dir" ]]; then find "$cli_dir" -depth -delete 2>/dev/null || true; fi
  unlink "$cookie_jar" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

chmod 711 "$key_dir"
openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
docker run --rm --volume "$key_dir:/keys" alpine:3.22 \
  sh -ceu 'chown 1654:1654 /keys/agent-auth-private.pem && chmod 600 /keys/agent-auth-private.pem'
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"
export NETRATEL_WEB_PORT="$web_port"
export NETRATEL_AUTHENTICATION_MODE=Local
export POSTGRES_PASSWORD=synthetic-postgresql-local-upgrade-postgres-password

configure_images() {
  export NETRATEL_API_IMAGE="$1"
  export NETRATEL_MIGRATIONS_IMAGE="$2"
  export NETRATEL_WEB_IMAGE="$3"
}

wait_for_migrations() {
  local migration_id status
  migration_id="$("${compose[@]}" ps -a -q migrations)"
  [[ -n "$migration_id" ]] || { echo "Migration container was not created." >&2; return 1; }
  for _ in $(seq 1 90); do
    status="$(docker inspect --format '{{.State.Status}}' "$migration_id")"
    if [[ "$status" == exited ]]; then
      [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]]
      return
    fi
    sleep 1
  done
  echo "Migrations did not complete within 90 seconds." >&2
  return 1
}

wait_for_state() {
  local jq_filter="$1"
  for _ in $(seq 1 90); do
    if curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e "$jq_filter" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  echo "Expected bootstrap state did not appear within 90 seconds." >&2
  return 1
}

login_and_read_tenant() {
  local login_request login_status tenant_status
  login_request="$(jq -n --arg email "$admin_email" --arg password "$admin_password" \
    '{email:$email,password:$password,rememberMe:false}')"
  login_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    --header 'Content-Type: application/json' --data "$login_request" "$web_url/api/v2/local-auth/login")"
  [[ "$login_status" == 204 ]] || { echo "Existing local administrator could not sign in (HTTP $login_status)." >&2; return 1; }
  tenant_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cookie "$cookie_jar" "$web_url/api/v1/tenants")"
  [[ "$tenant_status" == 200 ]] || { echo "Signed-in local administrator could not read the retained tenant." >&2; return 1; }
}

user_id() {
  "${compose[@]}" exec -T postgres psql -U netratel -d netratel -Atqc \
    'SELECT "Id" FROM "LocalUsers" WHERE "NormalizedEmail" = '\''LOCAL-UPGRADE-ADMIN@EXAMPLE.TEST'\'';'
}

check_cli_credential() {
  local status
  set +e
  NETRATEL_CLI_API_BASE_URL="$web_url/api" NETRATEL_CLI_INTEGRATION_TOKEN="$credential_secret" \
    "$cli_executable" telemetry agent --tenant-id 1 --agent-id 00000000-0000-0000-0000-000000000001 >/dev/null 2>&1
  status=$?
  set -e
  [[ "$status" == 3 ]] || { echo "The retained integration credential did not reach its authorized missing target." >&2; return 1; }
}

cli_executable="${NETRATEL_UPGRADE_CURRENT_CLI_EXECUTABLE:-$root/src/NetRatel/NetRatel.Cli/bin/Release/net10.0/netratel}"
if [[ -n "${NETRATEL_UPGRADE_CURRENT_CLI_ARCHIVE:-}" ]]; then
  [[ -s "$NETRATEL_UPGRADE_CURRENT_CLI_ARCHIVE" ]] || { echo "Review CLI archive is missing." >&2; exit 1; }
  cli_dir="$(mktemp -d)"
  tar -xzf "$NETRATEL_UPGRADE_CURRENT_CLI_ARCHIVE" -C "$cli_dir"
  cli_executable="$cli_dir/netratel-cli-linux-x64/netratel"
fi
[[ -x "$cli_executable" ]] || { echo "Review CLI executable is missing." >&2; exit 1; }

stage="starting immutable published ${prior_version} PostgreSQL/local images"
configure_images "$legacy_api" "$legacy_migrations" "$legacy_web"
"${compose[@]}" up --detach
wait_for_migrations
wait_for_state '.setupRequired == true and .isReady == false'

stage="initializing an ${prior_version} local administrator from a private synthetic file"
printf '%s\n' "$admin_password" | "${compose[@]}" exec -T api \
  sh -ceu 'umask 077; cat > /var/netratel/bootstrap/review-password'
"${compose[@]}" exec -T \
  -e Bootstrap__Unattended__PasswordFile=/var/netratel/bootstrap/review-password \
  -e Bootstrap__Unattended__DisplayName='Legacy local administrator' \
  -e Bootstrap__Unattended__Email="$admin_email" \
  -e Bootstrap__Unattended__TenantName="$tenant_name" \
  api dotnet NetRatel.API.dll --initialize-unattended >/dev/null
"${compose[@]}" restart api web >/dev/null
wait_for_state '.isReady == true'
login_and_read_tenant
legacy_user_id="$(user_id)"
[[ -n "$legacy_user_id" ]] || { echo "Legacy administrator identity was not persisted." >&2; exit 1; }

stage="creating a legacy purpose-bound integration credential"
expiry="$(date -u -d '+7 days' +'%Y-%m-%dT%H:%M:%SZ')"
request="$(jq -n --arg expiry "$expiry" \
  '{name:"Legacy telemetry read",purpose:0,expiresAtUtc:$expiry,grants:[{tenantId:1,permission:"telemetry.read"}],instancePermissions:[]}')"
created="$(curl --fail --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  --header 'Content-Type: application/json' --header 'X-NetRatel-Account-Request: 1' \
  --data "$request" "$api_url/api/v2/account/integration-credentials/")"
credential_secret="$(jq -r '.secret // empty' <<<"$created")"
[[ "$credential_secret" == nrt_ic_* ]] || { echo "Legacy integration secret was not issued." >&2; exit 1; }
check_cli_credential

stage="upgrading the retained PostgreSQL/local state with extracted review images"
"${compose[@]}" down --remove-orphans
configure_images "$current_api" "$current_migrations" "$current_web"
"${compose[@]}" up --detach
wait_for_migrations
wait_for_state '.isReady == true and .setupRequired == false'
[[ "$(user_id)" == "$legacy_user_id" ]] || { echo "The local administrator identity changed across the upgrade." >&2; exit 1; }
login_and_read_tenant
check_cli_credential
ready_status="$("${compose[@]}" exec -T api dotnet NetRatel.API.dll --setup-status)"
grep -Fq 'Installation: Ready' <<<"$ready_status"
grep -Fq 'Setup code: Completed' <<<"$ready_status"
unset credential_secret admin_password
echo "Published ${prior_version} PostgreSQL/local upgrade continuity passed."
