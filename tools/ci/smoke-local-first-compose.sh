#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

project="netratel-local-first-${GITHUB_RUN_ID:-local}-${RANDOM}"
web_port="${NETRATEL_LOCAL_FIRST_WEB_PORT:-18081}"
web_url="http://127.0.0.1:${web_port}"
key_path="$(mktemp)"
stage="initializing local-first Compose smoke"
compose=(docker compose --project-name "$project" -f compose.sqlite.yaml)

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "::error title=Local-first Compose smoke failed::${stage}" >&2
    "${compose[@]}" ps --all >&2 || true
    "${compose[@]}" logs --no-color --tail 250 migrations api web >&2 || true
  fi
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  unlink "$key_path" 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

openssl ecparam -name prime256v1 -genkey -noout -out "$key_path"
# The disposable key is mounted read-only into an unprivileged container and is
# removed by the trap above. It is never emitted to logs or test output.
chmod 644 "$key_path"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$key_path"
export NETRATEL_WEB_PORT="$web_port"

stage="building and starting SQLite Compose profile"
"${compose[@]}" up --build --detach

stage="waiting for migration runner"
migration_id="$("${compose[@]}" ps -a -q migrations)"
[[ -n "$migration_id" ]] || { echo "Migration container was not created." >&2; exit 1; }
migration_complete=false
for _ in $(seq 1 90); do
  status="$(docker inspect --format '{{.State.Status}}' "$migration_id")"
  if [[ "$status" == exited ]]; then
    [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]] || exit 1
    migration_complete=true
    break
  fi
  sleep 1
done
[[ "$migration_complete" == true ]] || { echo "Migration container did not complete within 90 seconds." >&2; exit 1; }

stage="waiting for restricted setup surface"
for _ in $(seq 1 90); do
  if curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null; then
    break
  fi
  sleep 1
done
curl --connect-timeout 2 --fail --silent "$web_url/api/v2/setup/status" >/dev/null

stage="building and running Playwright local-first journey"
dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
playwright_script="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1"
[[ -f "$playwright_script" ]] || { echo "Playwright install script was not produced." >&2; exit 1; }
pwsh "$playwright_script" install --with-deps chromium
setup_proof="$("${compose[@]}" exec -T api cat /var/netratel/bootstrap/setup-proof)"
NETRATEL_LOCAL_FIRST_WEB_URL="$web_url" \
NETRATEL_LOCAL_FIRST_SETUP_PROOF="$setup_proof" \
NETRATEL_LOCAL_FIRST_ADMIN_EMAIL="browser-admin@example.test" \
NETRATEL_LOCAL_FIRST_ADMIN_PASSWORD="browser smoke local passphrase" \
  dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
    --configuration Release --no-build --filter 'FullyQualifiedName~LocalFirstComposeBrowserSmokeTests' \
    --results-directory TestResults/local-first -- --report-trx --report-trx-filename local-first-browser.trx
unset setup_proof
