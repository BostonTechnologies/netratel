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
PAIRING_STAGE=build
PAIRING_BUILD_LOG=''
cleanup_failure() {
  local exit_status=$?
  trap - EXIT
  if (( exit_status != 0 )); then
    # Fixed stage labels and compiler codes are safe to retain. Runtime logs,
    # exception messages and the private fixture can contain credentials.
    PAIRING_FAILURE_EXIT="$exit_status" PAIRING_FAILURE_STAGE="$PAIRING_STAGE" \
      PAIRING_FAILURE_BUILD_LOG="$PAIRING_BUILD_LOG" python3 - <<'PY'
import json, os, re, shutil
from pathlib import Path
root = Path(os.environ['NETRATEL_PAIRING_RUNTIME_ROOT'])
output = Path(os.environ.get('NETRATEL_PAIRING_EVIDENCE_DIRECTORY', str(root / 'evidence')))
output.mkdir(parents=True, exist_ok=True)
log_name = os.environ['PAIRING_FAILURE_BUILD_LOG']
codes = []
if log_name:
    log = root / log_name
    if log.is_file():
        codes = sorted(set(re.findall(r'\berror ((?:CS|MSB|NU|NETSDK)\d+)\b', log.read_text(errors='replace'))))
receipt = {'outcome': 'failed', 'stage': os.environ['PAIRING_FAILURE_STAGE'],
           'exitCode': int(os.environ['PAIRING_FAILURE_EXIT']), 'compilerErrorCodes': codes}
browser_receipt = root / 'actual-pair/browser-evidence/receipt.json'
if browser_receipt.is_file():
    records = json.loads(browser_receipt.read_text()).get('results', [])
    receipt['browserStages'] = [{'stage': row['stage'], 'status': row['status'],
        'loginHttpStatus': row.get('loginHttpStatus')} for row in records
        if re.fullmatch(r'[a-z0-9-]{1,100}', row.get('stage', '')) and row.get('status') in ['passed', 'failed']]
    failure = next((row for row in reversed(records) if row.get('status') == 'failed'), {})
    reason = failure.get('reason', '')
    # Retain only a known Playwright operation prefix, never arbitrary exception text.
    receipt['browserFailureOperation'] = next((prefix for prefix in
        ['browserType.launch', 'page.goto', 'locator.waitFor', 'locator.fill', 'locator.click',
         'expect.toBeVisible', 'expect.toHaveURL', 'expect.toContainText', 'expect.toBe']
        if reason.startswith(prefix)), 'unclassified')
    source_failure = next((row for row in records if row.get('stage') == 'rendered-source-failure'), {})
    line = source_failure.get('sourceLine')
    receipt['browserSourceLine'] = line if isinstance(line, int) and 0 < line < 10000 else None
    for screenshot in browser_receipt.parent.glob('rendered-failure-*.png'):
        if re.fullmatch(r'rendered-failure-[0-9]+\.png', screenshot.name) and screenshot.stat().st_size <= 2 * 1024 * 1024:
            shutil.copyfile(screenshot, output / screenshot.name)
(output / 'failure-stage.json').write_text(json.dumps(receipt, indent=2) + '\n')
PY
    if [[ -f "$NETRATEL_PAIRING_RUNTIME_ROOT/actual-pair/private-state.json" ]]; then
      python3 "$PAIRING_HELPER_ROOT/native_pair.py" cleanup --purge || true
    fi
    printf '%s\n' 'Actual product-pair acceptance failed; no passed acceptance receipt was produced' >&2
  fi
  exit "$exit_status"
}
trap cleanup_failure EXIT
build() {
  PAIRING_STAGE="$2-build"
  PAIRING_BUILD_LOG="$2-build.log"
  # The workflow's NetRatel VERSION would otherwise become MSBuild's Version
  # property in the companion checkout as well. Each root owns its own version.
  env -u VERSION "$DOTNET_PAIRING_HOST" build "$1" --configuration Debug -m:1 -nr:false > "$NETRATEL_PAIRING_RUNTIME_ROOT/$2-build.log" 2>&1
}
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Migrations/NetRatel.Migrations.csproj" nr-migrations
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.API/NetRatel.API.csproj" nr-api
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Web/NetRatel.Web.csproj" nr-web
build "$NETRATEL_PAIRING_SOURCE_ROOT/src/NetRatel/NetRatel.Client/NetRatel.Client.csproj" nr-client
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/Helpdesk.API/Helpdesk.API.csproj" rd-api
build "$RATELDESK_PAIRING_SOURCE_ROOT/src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj" rd-web
PAIRING_BUILD_LOG=''
PAIRING_STAGE=browser-install
npm install --prefix "$NETRATEL_PAIRING_RUNTIME_ROOT/browser" --no-audit --no-fund @playwright/test@1.63.0 > "$NETRATEL_PAIRING_RUNTIME_ROOT/browser-install.log" 2>&1
PAIRING_BROWSER_ARGS=(install chromium)
if [[ "${PAIRING_BROWSER_WITH_DEPS:-0}" == 1 ]]; then PAIRING_BROWSER_ARGS=(install --with-deps chromium); fi
PAIRING_STAGE=browser-engine-install
node "$NETRATEL_PAIRING_RUNTIME_ROOT/browser/node_modules/@playwright/test/cli.js" "${PAIRING_BROWSER_ARGS[@]}" > "$NETRATEL_PAIRING_RUNTIME_ROOT/browser-install-engine.log" 2>&1
PAIRING_STAGE=product-start
python3 "$PAIRING_HELPER_ROOT/native_pair.py" start
PAIRING_STAGE=browser-pair
node "$PAIRING_HELPER_ROOT/browser_pair.js"
PAIRING_STAGE=business-pair
node "$PAIRING_HELPER_ROOT/business_pair.js"
PAIRING_STAGE=delete-pair
node "$PAIRING_HELPER_ROOT/delete_pair.js"
PAIRING_STAGE=prepare-receipt
python3 "$PAIRING_HELPER_ROOT/final_receipt.py" prepare
PAIRING_STAGE=product-cleanup
python3 "$PAIRING_HELPER_ROOT/native_pair.py" cleanup --purge
PAIRING_STAGE=finalize-receipt
python3 "$PAIRING_HELPER_ROOT/final_receipt.py" finalize
