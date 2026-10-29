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

# Each independent process still runs one MTP module at a time. Components
# run alone before overlapping the independent Core and API backend suites.
run_assembly() {
  local assembly="$1" project report stage_start status
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
      return "$status"
    fi
  else
    status="$?"
    record_stage "$assembly" "$((SECONDS - stage_start))" "failed($status)"
    return "$status"
  fi
}

# UI polling has short deadlines. Keep this group free of backend CPU/DB load.
component_assembly="NetRatel.Web.ComponentTests"
run_assembly "$component_assembly" > "$results_directory/fast-$component_assembly.log" 2>&1 &
component_pid="$!"
result=0
if wait "$component_pid"; then
  cat "$results_directory/fast-$component_assembly.log"
else
  result="$?"
  cat "$results_directory/fast-$component_assembly.log"
fi

pids=()
parallel_assemblies=()
for assembly in "${assemblies[@]}"; do
  if [[ "$assembly" == "$component_assembly" ]]; then
    continue
  fi
  run_assembly "$assembly" > "$results_directory/fast-$assembly.log" 2>&1 &
  pids+=("$!")
  parallel_assemblies+=("$assembly")
done

# Wait for every group and keep each command/receipt failure. Separate logs keep
# concurrent test output readable and retain diagnostics with the TRX artifacts.
for index in "${!parallel_assemblies[@]}"; do
  assembly="${parallel_assemblies[$index]}"
  if wait "${pids[$index]}"; then
    cat "$results_directory/fast-$assembly.log"
  else
    status="$?"
    cat "$results_directory/fast-$assembly.log"
    if [[ "$result" -eq 0 ]]; then
      result="$status"
    fi
  fi
done
exit "$result"
