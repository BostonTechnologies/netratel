#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-smoke-${GITHUB_RUN_ID:-local}-${RANDOM}"
key_path="$(mktemp)"
wait_for_migrations() {
  local container_id state exit_code
  container_id="$(docker compose --project-name "$project" -f compose.yaml ps -a -q migrations)"
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

cleanup() {
  docker compose --project-name "$project" -f compose.yaml down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  unlink "$key_path" 2>/dev/null || true
}
trap cleanup EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
chmod 600 "$key_path"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"

docker compose --project-name "$project" -f compose.yaml up --build --detach
wait_for_migrations

status="$(curl --retry 20 --retry-connrefused --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:${NETRATEL_WEB_PORT:-8080}/auth/oidc)"
if [[ "$status" != 302 ]]; then
  docker compose --project-name "$project" -f compose.yaml logs --no-color >&2 || true
  echo "Expected the configured OIDC challenge endpoint to redirect (302), got $status." >&2
  exit 1
fi

echo "Source Compose smoke test passed."
