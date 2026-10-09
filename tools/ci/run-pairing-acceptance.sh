#!/usr/bin/env bash
set -euo pipefail
: "${NETRATEL_PAIRING_SOURCE_ROOT:?Set the explicit current NetRatel source root}"
: "${RATELDESK_PAIRING_SOURCE_ROOT:?Set the explicit current RatelDesk source root}"
: "${NETRATEL_PAIRING_RUNTIME_ROOT:?Set a fresh disposable runtime directory}"
for PAIRING_SOURCE_VARIABLE in NETRATEL_PAIRING_SOURCE_ROOT RATELDESK_PAIRING_SOURCE_ROOT; do
  PAIRING_SOURCE_DIRECTORY="${!PAIRING_SOURCE_VARIABLE}"
  case "$PAIRING_SOURCE_DIRECTORY" in /*) ;; *) printf '%s\n' 'Source roots must be absolute' >&2; exit 2 ;; esac
  [[ -d "$PAIRING_SOURCE_DIRECTORY" ]] || { printf '%s\n' 'An explicit source checkout is missing' >&2; exit 2; }
  if [[ "${PAIRING_REQUIRE_CLEAN_SOURCE:-0}" == 1 && -n "$(git -C "$PAIRING_SOURCE_DIRECTORY" status --porcelain --untracked-files=all)" ]]; then
    printf '%s\n' 'Commit-bound acceptance requires clean source checkouts' >&2
    exit 2
  fi
done
export PAIRING_EXPECTED_NETRATEL_SHA="$(git -C "$NETRATEL_PAIRING_SOURCE_ROOT" rev-parse HEAD)"
export PAIRING_EXPECTED_RATELDESK_SHA="$(git -C "$RATELDESK_PAIRING_SOURCE_ROOT" rev-parse HEAD)"
case "$NETRATEL_PAIRING_RUNTIME_ROOT" in /*) ;; *) printf '%s\n' 'Runtime root must be absolute' >&2; exit 2 ;; esac
if [[ -e "$NETRATEL_PAIRING_RUNTIME_ROOT/actual-pair" || -e "$NETRATEL_PAIRING_RUNTIME_ROOT/acceptance-receipt.json" || -e "$NETRATEL_PAIRING_RUNTIME_ROOT/pending-receipt.json" ]]; then
  printf '%s\n' 'A fresh fixture and receipt directory is required' >&2
  exit 2
fi
PAIRING_HELPER_ROOT="$NETRATEL_PAIRING_SOURCE_ROOT/tools/testing/pairing"
[[ -f "$PAIRING_HELPER_ROOT/native_pair.py" && -f "$PAIRING_HELPER_ROOT/browser_pair.js" && -f "$PAIRING_HELPER_ROOT/business_pair.js" ]] || {
  printf '%s\n' 'Current source-pair acceptance helpers are missing' >&2
  exit 2
}
mkdir -p "$NETRATEL_PAIRING_RUNTIME_ROOT"
chmod 700 "$NETRATEL_PAIRING_RUNTIME_ROOT"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$NETRATEL_PAIRING_RUNTIME_ROOT/dotnet-cli}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$NETRATEL_PAIRING_RUNTIME_ROOT/nuget}"
export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$NETRATEL_PAIRING_RUNTIME_ROOT/playwright-browsers}"
export npm_config_cache="${npm_config_cache:-$NETRATEL_PAIRING_RUNTIME_ROOT/npm-cache}"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_PAIRING_HOST="${DOTNET_HOST_PATH:-$(command -v dotnet)}"
[[ "$("$DOTNET_PAIRING_HOST" --version)" == 10.0.* ]] || { printf '%s\n' '.NET 10 SDK is required' >&2; exit 2; }
command -v python3 >/dev/null
command -v node >/dev/null
command -v docker >/dev/null
command -v openssl >/dev/null
build() {
  "$DOTNET_PAIRING_HOST" build "$1" --configuration Debug -m:1 -nr:false > "$NETRATEL_PAIRING_RUNTIME_ROOT/$2-build.log" 2>&1
}
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj" nr-migrations
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.API/NetRatel.API.csproj" nr-api
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Web/NetRatel.Web.csproj" nr-web
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Client/NetRatel.Client.csproj" nr-client
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/Helpdesk.API/Helpdesk.API.csproj" rd-api
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj" rd-web
npm install --prefix "$NETRATEL_PAIRING_RUNTIME_ROOT/browser" --no-audit --no-fund @playwright/test@1.63.0 > "$NETRATEL_PAIRING_RUNTIME_ROOT/browser-install.log" 2>&1
PAIRING_BROWSER_ARGS=(install chromium)
if [[ "${PAIRING_BROWSER_WITH_DEPS:-0}" == 1 ]]; then PAIRING_BROWSER_ARGS=(install --with-deps chromium); fi
node "$NETRATEL_PAIRING_RUNTIME_ROOT/browser/node_modules/@playwright/test/cli.js" "${PAIRING_BROWSER_ARGS[@]}" > "$NETRATEL_PAIRING_RUNTIME_ROOT/browser-install-engine.log" 2>&1
cleanup_failure() {
  local exit_status=$?
  trap - EXIT
  if (( exit_status != 0 )); then
    python3 "$PAIRING_HELPER_ROOT/native_pair.py" cleanup --purge || true
    printf '%s\n' 'Actual product-pair acceptance failed; no passed acceptance receipt was produced' >&2
  fi
  exit "$exit_status"
}
trap cleanup_failure EXIT
python3 "$PAIRING_HELPER_ROOT/native_pair.py" start
node "$PAIRING_HELPER_ROOT/browser_pair.js"
node "$PAIRING_HELPER_ROOT/business_pair.js"
node "$PAIRING_HELPER_ROOT/delete_pair.js"
python3 "$PAIRING_HELPER_ROOT/final_receipt.py" prepare
python3 "$PAIRING_HELPER_ROOT/native_pair.py" cleanup --purge
python3 "$PAIRING_HELPER_ROOT/final_receipt.py" finalize
