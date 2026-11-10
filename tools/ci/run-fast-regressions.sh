#!/usr/bin/env bash
set -euo pipefail

# Run prebuilt default regressions only. Deep functional suites are opt-in.
results_directory="${1:-TestResults/fast}"
selection="${2:-all}"
if [[ "$#" -gt 2 || ! "$selection" =~ ^(all|core-components|api)$ ]]; then
  echo "Usage: $0 [results-directory] [all|core-components|api]" >&2
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
# run alone before overlapping backend groups on the selected runner.
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

# PR/main run the Core API namespace and API suite on their existing API runner.
# Tag validation retains the complete default selection in this same helper.
result=0
# Native xUnit discovery uses each runner's prebuilt binary and the same category
# exclusions as MTP execution. The PR gate compares both manifests and verifies
# their complete receipt union, including theories, without fixed pass counts.
dotnet src/NetRatel/NetRatel.Tests/bin/Release/net10.0/NetRatel.Tests.dll \
  -noColor -list full/json -preEnumerateTheories \
  -trait- category=compose -trait- category=hosted -trait- category=manual-integration \
  > "$results_directory/core-discovery.json"

if [[ "$selection" != api ]]; then
  # UI polling has short deadlines. Keep this group free of backend CPU/DB load.
  component_assembly="NetRatel.Web.ComponentTests"
  run_assembly "$component_assembly" "$component_assembly" > "$results_directory/fast-$component_assembly.log" 2>&1 &
  component_pid="$!"
  if wait "$component_pid"; then
    cat "$results_directory/fast-$component_assembly.log"
  else
    result="$?"
    cat "$results_directory/fast-$component_assembly.log"
  fi
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
# queue while preserving every existing case and category exclusion. Divide
# the original Core collection-thread allowance across the solution processes;
# retaining the full allowance in each doubled concurrent migration load.
if [[ "$selection" != api ]]; then
  launch NetRatel.Tests NetRatel.Tests.Infrastructure \
    --max-threads 0.5x \
    --filter-namespace NetRatel.Tests.Infrastructure 'NetRatel.Tests.Infrastructure.*'
  remaining_filters=(--filter-not-namespace NetRatel.Tests.Infrastructure 'NetRatel.Tests.Infrastructure.*')
  if [[ "$selection" == core-components ]]; then
    remaining_filters+=(NetRatel.Tests.API 'NetRatel.Tests.API.*')
  fi
  launch NetRatel.Tests NetRatel.Tests.Remaining \
    --max-threads 0.5x \
    "${remaining_filters[@]}"
fi
if [[ "$selection" == api ]]; then
  launch NetRatel.Tests NetRatel.Tests.API \
    --max-threads 0.5x \
    --filter-namespace NetRatel.Tests.API 'NetRatel.Tests.API.*'
fi
if [[ "$selection" != core-components ]]; then
  launch NetRatel.API.IntegrationTests NetRatel.API.IntegrationTests
fi

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
if (( result == 0 )) && [[ "$selection" == all ]]; then
  python3 tools/ci/verify-core-partitions.py --discovery "$results_directory/core-discovery.json" \
    --infrastructure "$results_directory/fast-NetRatel.Tests.Infrastructure.trx" \
    --remaining "$results_directory/fast-NetRatel.Tests.Remaining.trx" \
    --output "$results_directory/core-partition-coverage.json" || result="$?"
fi
exit "$result"
