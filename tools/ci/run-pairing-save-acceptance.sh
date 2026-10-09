#!/usr/bin/env bash
set -euo pipefail
: "${NETRATEL_PAIRING_SOURCE_ROOT:?Select the corrected NetRatel checkout}"
: "${RATELDESK_PAIRING_SOURCE_ROOT:?Select the corrected RatelDesk checkout}"
: "${NETRATEL_PAIRING_BASELINE_SOURCE_ROOT:?Select the released NetRatel baseline checkout}"
: "${NETRATEL_PAIRING_RUNTIME_ROOT:?Select a fresh disposable runtime root}"
PAIRING_SAVE_ROOT="$NETRATEL_PAIRING_RUNTIME_ROOT"
for PAIRING_SAVE_SOURCE in "$NETRATEL_PAIRING_SOURCE_ROOT" "$RATELDESK_PAIRING_SOURCE_ROOT" "$NETRATEL_PAIRING_BASELINE_SOURCE_ROOT"; do
  [[ "$PAIRING_SAVE_SOURCE" == /* && -d "$PAIRING_SAVE_SOURCE" ]] || { printf '%s\n' 'Explicit absolute source roots are required' >&2; exit 2; }
  [[ -z "$(git -C "$PAIRING_SAVE_SOURCE" status --porcelain --untracked-files=all)" ]] || { printf '%s\n' 'Focused final acceptance requires clean source checkouts' >&2; exit 2; }
done
[[ "$PAIRING_SAVE_ROOT" == /* && ! -e "$PAIRING_SAVE_ROOT" ]] || { printf '%s\n' 'Select a fresh absolute runtime directory' >&2; exit 2; }
export PAIRING_REQUIRE_CLEAN_SOURCE=1
export PAIRING_EXPECTED_NETRATEL_SHA="$(git -C "$NETRATEL_PAIRING_SOURCE_ROOT" rev-parse HEAD)"
export PAIRING_EXPECTED_RATELDESK_SHA="$(git -C "$RATELDESK_PAIRING_SOURCE_ROOT" rev-parse HEAD)"
export PAIRING_EXPECTED_BASELINE_SHA="$(git -C "$NETRATEL_PAIRING_BASELINE_SOURCE_ROOT" rev-parse HEAD)"
mkdir -m 700 -p "$PAIRING_SAVE_ROOT"
PAIRING_SAVE_HELPERS="$NETRATEL_PAIRING_SOURCE_ROOT/tools/testing/pairing"
PAIRING_SAVE_DOTNET="${DOTNET_HOST_PATH:-$(command -v dotnet)}"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$PAIRING_SAVE_ROOT/dotnet-cli}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$PAIRING_SAVE_ROOT/nuget}"
export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-$PAIRING_SAVE_ROOT/playwright-browsers}"
export npm_config_cache="${npm_config_cache:-$PAIRING_SAVE_ROOT/npm-cache}"
build() { "$PAIRING_SAVE_DOTNET" build "$1" --configuration Debug -m:1 -nr:false > "$PAIRING_SAVE_ROOT/$2-build.log" 2>&1; }
build "$NETRATEL_PAIRING_BASELINE_SOURCE_ROOT/src/NetRatel/NetRatel.API/NetRatel.API.csproj" nr-baseline-api
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj" nr-migrations
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.API/NetRatel.API.csproj" nr-api
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Web/NetRatel.Web.csproj" nr-web
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/Helpdesk.API/Helpdesk.API.csproj" rd-api
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj" rd-web
npm install --prefix "$PAIRING_SAVE_ROOT/browser" --no-audit --no-fund @playwright/test@1.63.0 > "$PAIRING_SAVE_ROOT/browser-install.log" 2>&1
node "$PAIRING_SAVE_ROOT/browser/node_modules/@playwright/test/cli.js" install chromium > "$PAIRING_SAVE_ROOT/browser-engine.log" 2>&1
PAIRING_SAVE_ACTIVE=''
cleanup_failure() {
  local status=$?
  trap - EXIT
  if [[ -n "$PAIRING_SAVE_ACTIVE" && -f "$PAIRING_SAVE_ACTIVE/actual-pair/private-state.json" ]]; then
    export NETRATEL_PAIRING_RUNTIME_ROOT="$PAIRING_SAVE_ACTIVE"
    python3 "$PAIRING_SAVE_HELPERS/native_pair.py" cleanup --purge || { printf '%s\n' 'Owned focused fixture cleanup failed' >&2; exit 1; }
  fi
  exit "$status"
}
trap cleanup_failure EXIT
for PAIRING_SAVE_JOURNEY in retained fresh; do
  PAIRING_SAVE_ACTIVE="$PAIRING_SAVE_ROOT/$PAIRING_SAVE_JOURNEY"
  mkdir -m 700 "$PAIRING_SAVE_ACTIVE"
  ln -s "$PAIRING_SAVE_ROOT/browser" "$PAIRING_SAVE_ACTIVE/browser"
  export NETRATEL_PAIRING_RUNTIME_ROOT="$PAIRING_SAVE_ACTIVE"
  python3 "$PAIRING_SAVE_HELPERS/final_save.py" start --journey "$PAIRING_SAVE_JOURNEY"
  node "$PAIRING_SAVE_HELPERS/final_save.js"
  python3 "$PAIRING_SAVE_HELPERS/native_pair.py" cleanup --purge
  python3 "$PAIRING_SAVE_HELPERS/final_save.py" finalize
done
printf '%s\n' 'Focused HTTPS baseline failure, retained-draft correction retry and fresh first Save passed with positive cleanup'
