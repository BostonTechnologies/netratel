#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-rc3-local-upgrade-${GITHUB_RUN_ID:-local}-${RANDOM}"
web_port="${NETRATEL_RC3_UPGRADE_WEB_PORT:-18083}"
web_url="http://127.0.0.1:${web_port}"
legacy_api_image="${NETRATEL_RC3_LEGACY_API_IMAGE:?set NETRATEL_RC3_LEGACY_API_IMAGE}"
legacy_migrations_image="${NETRATEL_RC3_LEGACY_MIGRATIONS_IMAGE:?set NETRATEL_RC3_LEGACY_MIGRATIONS_IMAGE}"
legacy_web_image="${NETRATEL_RC3_LEGACY_WEB_IMAGE:?set NETRATEL_RC3_LEGACY_WEB_IMAGE}"
current_api_image="${NETRATEL_RC3_CURRENT_API_IMAGE:?set NETRATEL_RC3_CURRENT_API_IMAGE}"
current_migrations_image="${NETRATEL_RC3_CURRENT_MIGRATIONS_IMAGE:?set NETRATEL_RC3_CURRENT_MIGRATIONS_IMAGE}"
current_web_image="${NETRATEL_RC3_CURRENT_WEB_IMAGE:?set NETRATEL_RC3_CURRENT_WEB_IMAGE}"
bundle="${NETRATEL_RC3_UPGRADE_COMPOSE_BUNDLE:-}"
bundle_extract_directory=""
compose_file=compose.sqlite.yaml
cookie_jar="$(mktemp)"
backup_directory="$(mktemp -d)"
stage="initializing rc.3 local upgrade and restore smoke"
sqlite_volume="${project}_sqlite-data"
api_volume="${project}_api-data"
web_keys_volume="${project}_web-keys"

if [[ -n "$bundle" ]]; then
  [[ -s "$bundle" ]] || { echo "NETRATEL_RC3_UPGRADE_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
  bundle_extract_directory="$(mktemp -d)"
  tar -xzf "$bundle" -C "$bundle_extract_directory"
  compose_file="$bundle_extract_directory/compose.local-sqlite.yaml"
  [[ -f "$compose_file" ]] || { echo "The extracted release bundle is missing compose.local-sqlite.yaml." >&2; exit 1; }
fi

compose=(docker compose --project-name "$project" -f "$compose_file" -f tests/compose/rc3-upgrade-images.compose.yaml)

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=rc.3 local upgrade and restore smoke failed::${stage}" >&2
    "${compose[@]}" ps --all >&2 || true
    "${compose[@]}" logs --no-color --tail 250 migrations api web >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "$sqlite_volume" "$api_volume" "$web_keys_volume" >/dev/null 2>&1 || true
  unlink "$cookie_jar" 2>/dev/null || true
  find "$backup_directory" -depth -delete 2>/dev/null || true
  if [[ -n "$bundle_extract_directory" ]]; then
    find "$bundle_extract_directory" -depth -delete 2>/dev/null || true
  fi
  return "$status"
}
trap cleanup EXIT

configure_images() {
  export NETRATEL_RC3_UPGRADE_API_IMAGE="$1"
  export NETRATEL_RC3_UPGRADE_MIGRATIONS_IMAGE="$2"
  export NETRATEL_RC3_UPGRADE_WEB_IMAGE="$3"
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
      [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]] || return 1
      return 0
    fi
    sleep 1
  done

  echo "Migration container did not complete within 90 seconds." >&2
  return 1
}

wait_for_ready() {
  for _ in $(seq 1 90); do
    if curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e '.isReady == true and .setupRequired == false' >/dev/null; then
      return 0
    fi
    sleep 1
  done

  echo "The upgraded Web application did not report a ready setup state." >&2
  return 1
}

wait_for_unconfigured_setup_surface() {
  for _ in $(seq 1 90); do
    if curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" | jq -e '.setupRequired == true and .isReady == false' >/dev/null; then
      return 0
    fi
    sleep 1
  done

  echo "The published rc.3 Web application did not expose its unconfigured setup surface." >&2
  return 1
}

verify_existing_local_administrator() {
  stage="signing in with the persisted rc.3 local administrator"
  curl --connect-timeout 5 --fail --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
    --header 'Content-Type: application/json' \
    --data '{"email":"rc3-upgrade-admin@example.test","password":"rc3 upgrade local passphrase","rememberMe":false}' \
    "$web_url/api/v2/local-auth/login" >/dev/null
  curl --connect-timeout 5 --fail --silent --show-error --cookie "$cookie_jar" \
    "$web_url/api/v1/tenants" | jq -e 'type == "array" and length == 1 and .[0].name == "rc.3 upgrade tenant"' >/dev/null
}

backup_legacy_state() {
  stage="backing up the complete rc.3 local state set"
  docker run --rm \
    --volume "${sqlite_volume}:/source/sqlite:ro" \
    --volume "${api_volume}:/source/api:ro" \
    --volume "${web_keys_volume}:/source/web:ro" \
    --volume "${backup_directory}:/backup" \
    alpine:3.22 sh -ceu 'tar -C /source -czf /backup/rc3-local-state.tar.gz sqlite api web'
  [[ -s "$backup_directory/rc3-local-state.tar.gz" ]]
}

restore_legacy_state() {
  stage="restoring the complete rc.3 local state set into clean volumes"
  docker volume create "$sqlite_volume" >/dev/null
  docker volume create "$api_volume" >/dev/null
  docker volume create "$web_keys_volume" >/dev/null
  docker run --rm \
    --volume "${sqlite_volume}:/target/sqlite" \
    --volume "${api_volume}:/target/api" \
    --volume "${web_keys_volume}:/target/web" \
    --volume "${backup_directory}:/backup:ro" \
    alpine:3.22 sh -ceu 'tar -C /target -xzf /backup/rc3-local-state.tar.gz'
}

export NETRATEL_WEB_PORT="$web_port"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY=/dev/null

stage="starting the published rc.3 images against an empty local SQLite volume"
configure_images "$legacy_api_image" "$legacy_migrations_image" "$legacy_web_image"
"${compose[@]}" up --detach --no-build
wait_for_migrations
wait_for_unconfigured_setup_surface

stage="creating a durable rc.3 local administrator and tenant"
setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
curl --connect-timeout 5 --fail --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  --header "Origin: $web_url" --header 'Content-Type: application/json' \
  --data "$(jq -n --arg proof "$setup_proof" '{proof:$proof}')" \
  "$web_url/api/v2/setup/claim" >/dev/null
curl --connect-timeout 5 --fail --silent --show-error --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
  --header "Origin: $web_url" --header 'Content-Type: application/json' \
  --data '{"tenantName":"rc.3 upgrade tenant","displayName":"rc.3 upgrade administrator","email":"rc3-upgrade-admin@example.test","password":"rc3 upgrade local passphrase"}' \
  "$web_url/api/v2/setup/initialize" >/dev/null
wait_for_ready
verify_existing_local_administrator
backup_legacy_state

stage="upgrading the live rc.3 local state with the current public images"
"${compose[@]}" down --remove-orphans
configure_images "$current_api_image" "$current_migrations_image" "$current_web_image"
"${compose[@]}" up --detach --no-build
wait_for_migrations
wait_for_ready
verify_existing_local_administrator

stage="discarding the upgraded volumes before restore validation"
"${compose[@]}" down --volumes --remove-orphans
restore_legacy_state

stage="migrating the restored rc.3 local state with the current public images"
"${compose[@]}" up --detach --no-build
wait_for_migrations
wait_for_ready
verify_existing_local_administrator
