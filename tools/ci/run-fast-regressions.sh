#!/usr/bin/env bash
set -euo pipefail

# Run prebuilt default regressions only. Deep functional suites are opt-in.
results_directory="${1:-TestResults/fast}"
if [[ "$#" -gt 1 ]]; then
  echo "Usage: $0 [results-directory]" >&2
  exit 2
fi
mkdir -p "$results_directory"

assemblies=(
  NetRatel.Tests
  NetRatel.API.IntegrationTests
  NetRatel.Web.ComponentTests
)
test_arguments=(
  --configuration Release --no-build --max-parallel-test-modules 1
  --filter-not-trait category=compose category=hosted category=manual-integration
  --results-directory "$results_directory" --report-trx
)

record_stage() {
  local assembly="$1" duration="$2" status="$3"
  echo "Fast regressions: $assembly duration=${duration}s status=$status" || true
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '%s: %ss (%s)\n\n' "$assembly" "$duration" "$status" >> "$GITHUB_STEP_SUMMARY" || true
  fi
}

for assembly in "${assemblies[@]}"; do
  project="src/NetRatel/$assembly/$assembly.csproj"
  report="$results_directory/fast-$assembly.trx"
  rm -f "$report"
  stage_start="$SECONDS"
  if dotnet test --project "$project" "${test_arguments[@]}" \
      --report-trx-filename "fast-$assembly.trx"; then
    if python3 tools/ci/verify-mtp-trx.py "$report"; then
      record_stage "$assembly" "$((SECONDS - stage_start))" passed
    else
      status="$?"
      record_stage "$assembly" "$((SECONDS - stage_start))" "receipt-failed($status)"
      exit "$status"
    fi
  else
    status="$?"
    record_stage "$assembly" "$((SECONDS - stage_start))" "failed($status)"
    exit "$status"
  fi
done
