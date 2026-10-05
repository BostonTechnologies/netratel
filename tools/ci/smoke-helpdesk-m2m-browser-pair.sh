#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
pins_file="${NETRATEL_OWNER_PAIR_PINS_FILE:?Supply the independently verified published-peer and hosted candidate image handoff.}"
[[ -s "$pins_file" ]] || { echo "The actual owner-pair image handoff is absent." >&2; exit 1; }
python3 tools/ci/verify-owner-pair-receipts.py --pins-file "$pins_file" --check-pins-only
export NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY="${NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY:-$root/TestResults/helpdesk-owner-compose}"
mkdir -p "$NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY"
[[ -z "$(find "$NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY" -mindepth 1 -maxdepth 1 -print -quit)" ]] || { echo "Use a fresh owner-pair evidence directory; prior evidence is preserved." >&2; exit 1; }
test_project="src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj"
test_dll="src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/NetRatel.Web.PlaywrightTests.dll"
[[ -s "$test_dll" ]] || { echo "The existing hosted browser-project build must include the proposed owner tests." >&2; exit 1; }

# Existing local-first hosted infrastructure restores/builds this project and
# installs Chromium. This runner does not introduce another build or weaken that gate.
dotnet test --project "$test_project" --configuration Release --no-build --no-restore \
  --filter-class NetRatel.Web.PlaywrightTests.HelpdeskM2MComposeCeremonyTests \
  --results-directory "$NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY" \
  --report-trx --report-trx-filename owner-browser.trx
python3 tools/ci/verify-mtp-trx.py "$NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY/owner-browser.trx" --expected-executed 6
python3 tools/ci/verify-owner-pair-receipts.py "$NETRATEL_OWNER_PAIR_EVIDENCE_DIRECTORY" --pins-file "$pins_file"
