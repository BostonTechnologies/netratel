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
# run alone before overlapping the two disjoint Core groups and API suite.
run_assembly() {
  local assembly="$1" group="$2" project report stage_start status
  shift 2
  project="src/NetRatel/$assembly/$assembly.csproj"
  report="$results_directory/fast-$group.trx"
  rm -f "$report"
  stage_start="$SECONDS"
  if dotnet test --project "$project" "${test_arguments[@]}" "$@" \
      --report-trx-filename "fast-$group.trx"; then
    if python3 tools/ci/verify-mtp-trx.py "$report"; then
      record_stage "$group" "$((SECONDS - stage_start))" passed
    else
      status="$?"
      record_stage "$group" "$((SECONDS - stage_start))" "receipt-failed($status)"
      return "$status"
    fi
  else
    status="$?"
    record_stage "$group" "$((SECONDS - stage_start))" "failed($status)"
    return "$status"
  fi
}

# Native xUnit discovery uses the same prebuilt binary and the same category
# exclusions as MTP execution. It runs no cases and supplies a growing coverage
# manifest, including theories, rather than a fixed expected pass count.
dotnet src/NetRatel/NetRatel.Tests/bin/Release/net10.0/NetRatel.Tests.dll \
  -list full/json -preEnumerateTheories \
  -trait- category=compose -trait- category=hosted -trait- category=manual-integration \
  > "$results_directory/core-discovery.json"

# UI polling has short deadlines. Keep this group free of backend CPU/DB load.
component_assembly="NetRatel.Web.ComponentTests"
run_assembly "$component_assembly" "$component_assembly" > "$results_directory/fast-$component_assembly.log" 2>&1 &
component_pid="$!"
result=0
if wait "$component_pid"; then
  cat "$results_directory/fast-$component_assembly.log"
else
  result="$?"
  cat "$results_directory/fast-$component_assembly.log"
fi

pids=(); groups=()
launch() {
  local assembly="$1" group="$2"
  shift 2
  run_assembly "$assembly" "$group" "$@" > "$results_directory/fast-$group.log" 2>&1 &
  pids+=("$!")
  groups+=("$group")
}
# Each process owns a separate randomized PostgreSQL server and isolated test
# databases. Splitting the shared collection removes its cross-namespace serial
# queue while preserving every existing case and category exclusion.
launch NetRatel.Tests NetRatel.Tests.Infrastructure \
  --filter-namespace NetRatel.Tests.Infrastructure 'NetRatel.Tests.Infrastructure.*'
launch NetRatel.Tests NetRatel.Tests.Remaining \
  --filter-not-namespace NetRatel.Tests.Infrastructure 'NetRatel.Tests.Infrastructure.*'
launch NetRatel.API.IntegrationTests NetRatel.API.IntegrationTests

# Wait for every group and keep each command/receipt failure. Separate logs keep
# concurrent test output readable and retain diagnostics with the TRX artifacts.
for index in "${!groups[@]}"; do
  group="${groups[$index]}"
  if wait "${pids[$index]}"; then
    cat "$results_directory/fast-$group.log"
  else
    status="$?"
    cat "$results_directory/fast-$group.log"
    if [[ "$result" -eq 0 ]]; then
      result="$status"
    fi
  fi
done
if (( result == 0 )); then
  python3 tools/ci/verify-core-partitions.py --discovery "$results_directory/core-discovery.json" \
    --infrastructure "$results_directory/fast-NetRatel.Tests.Infrastructure.trx" \
    --remaining "$results_directory/fast-NetRatel.Tests.Remaining.trx" \
    --output "$results_directory/core-partition-coverage.json" || result="$?"
fi
exit "$result"
