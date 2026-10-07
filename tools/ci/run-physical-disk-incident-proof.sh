#!/usr/bin/env bash
# Opt-in hosted physical case selected by the manual integration workflow.
# Build one full Client from this exact checkout and require independent receipts.
set -euo pipefail

: "${GITHUB_WORKSPACE:?The hosted physical lane requires its actual workspace.}"
: "${RUNNER_TEMP:?The hosted physical lane requires its owned work directory parent.}"
: "${GITHUB_SHA:?The hosted physical lane requires its actual test checkout.}"
: "${NETRATEL_REVIEW_SOURCE_SHA:?The reviewed source must be explicit.}"
: "${NETRATEL_REVIEW_TEST_MERGE_SHA:?The actual test checkout must be explicit.}"
source_sha="$(git rev-parse HEAD)"
[[ "$source_sha" == "$GITHUB_SHA" && "$source_sha" == "$NETRATEL_REVIEW_TEST_MERGE_SHA" ]] || {
  echo "The hosted physical lane must use the actual reviewed test checkout." >&2
  exit 1
}

# Reuse the existing current-constants/accepted-publication comparison, leaving
# both the ordinary native receipt verifier and its exactly-two gate unchanged.
companion_pin_lines="$(python3 - <<'PY_PINS'
import importlib.util
from pathlib import Path

path = Path("tools/ci/verify-service-link-native-evidence.py")
spec = importlib.util.spec_from_file_location("verify_service_link_native_evidence", path)
if spec is None or spec.loader is None:
    raise SystemExit("The existing accepted companion identity validator is unavailable.")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
pins = verifier.peer_pins(Path.cwd())
for name in ("PublishedSource", "PublishedVersion", "ApiImage", "WebImage"):
    print(pins[name])
PY_PINS
)"
mapfile -t companion_pins <<< "$companion_pin_lines"
[[ "${#companion_pins[@]}" == 4 ]] || {
  echo "The accepted companion identity must contain the exact source/version/image tuple." >&2
  exit 1
}
PHYSICAL_VERIFIED_COMPANION_SOURCE="${companion_pins[0]}"
PHYSICAL_VERIFIED_COMPANION_VERSION="${companion_pins[1]}"
PHYSICAL_VERIFIED_COMPANION_API_IMAGE="${companion_pins[2]}"
PHYSICAL_VERIFIED_COMPANION_WEB_IMAGE="${companion_pins[3]}"
export NETRATEL_DOTNET_SDK_VERSION="$(dotnet --version)"
export NETRATEL_PHYSICAL_DISK_EVIDENCE_DIRECTORY="${GITHUB_WORKSPACE}/TestResults/physical-disk-incident"
physical_trx="$NETRATEL_PHYSICAL_DISK_EVIDENCE_DIRECTORY/physical-disk-incident.trx"
# Fail on retained dedicated receipts rather than accepting or deleting old
# evidence. Ordinary/native proof files remain outside this directory.
if [[ -e "$physical_trx" ]] || compgen -G "$NETRATEL_PHYSICAL_DISK_EVIDENCE_DIRECTORY/disk-incident-*.json" >/dev/null; then
  echo "The hosted physical invocation requires fresh dedicated receipt paths." >&2
  exit 1
fi

candidate_version="$(dotnet msbuild src/NetRatel/NetRatel.Client/NetRatel.Client.csproj -nologo -getProperty:Version)"
physical_image="netratel-physical-client:${source_sha}"
tools/ci/build-public-image.sh --dockerfile docker/client/Dockerfile.public \
  --image "$physical_image" --version "$candidate_version" --revision "$source_sha"
tools/ci/scan-public-image.sh --image "$physical_image" --version "$candidate_version" --revision "$source_sha"

export NETRATEL_PHYSICAL_CLIENT_IMAGE_ID
NETRATEL_PHYSICAL_CLIENT_IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$physical_image")"
export NETRATEL_PHYSICAL_CLIENT_EXECUTABLE_SHA256
NETRATEL_PHYSICAL_CLIENT_EXECUTABLE_SHA256="$(docker run --rm --user 10001:10001 --cap-drop ALL \
  --security-opt no-new-privileges --network none --entrypoint python3 "$NETRATEL_PHYSICAL_CLIENT_IMAGE_ID" \
  -c 'import hashlib,pathlib; print(hashlib.sha256(pathlib.Path("/app/NetRatel.Client").read_bytes()).hexdigest())')"
export NETRATEL_PHYSICAL_WORK_ROOT
NETRATEL_PHYSICAL_WORK_ROOT="$(mktemp -d "${RUNNER_TEMP}/netratel-physical-work.XXXXXXXX")"
cleanup_physical_work_root() {
  local status=$?
  trap - EXIT
  # The reviewed helper must already have removed its exact owned resources and
  # allowlisted metadata. Never erase retained uncertain ownership recursively.
  if ! rmdir "$NETRATEL_PHYSICAL_WORK_ROOT"; then
    echo "The hosted physical fixture did not completely remove its owned work root." >&2
    status=1
  fi
  exit "$status"
}
trap cleanup_physical_work_root EXIT

# This hosted Category case is explicitly selected by the manual physical suite,
# outside the default fast regressions. It runs once when requested and fails
# that manual job on any prerequisite/producer/protocol/evidence/cleanup error.
dotnet test --project src/NetRatel/NetRatel.API.IntegrationTests/NetRatel.API.IntegrationTests.csproj \
  --configuration Release --no-build --max-parallel-test-modules 1 \
  --filter-class NetRatel.API.IntegrationTests.ServiceLinks.PhysicalDiskIncidentTests \
  --results-directory TestResults/physical-disk-incident \
  --report-trx --report-trx-filename physical-disk-incident.trx
python3 tools/ci/verify-mtp-trx.py TestResults/physical-disk-incident/physical-disk-incident.trx \
  --expected-executed 1

# The following values must come from the actual current checked-out generic
# published-peer constants/accepted published companion receipt. No default or
# guessed future release is permitted. The source/tree IDs come from the real
# checkout; NETRATEL_REVIEW_SOURCE_SHA is the existing reviewed PR/Main context.
python3 tools/ci/verify-physical-disk-incident-evidence.py \
  --directory "$NETRATEL_PHYSICAL_DISK_EVIDENCE_DIRECTORY" \
  --trx TestResults/physical-disk-incident/physical-disk-incident.trx \
  --schema tools/ci/physical-disk-incident-proof.schema.json \
  --source-sha "$(git rev-parse HEAD)" --tree-sha "$(git rev-parse 'HEAD^{tree}')" \
  --reviewed-source-sha "$NETRATEL_REVIEW_SOURCE_SHA" \
  --client-image-id "$NETRATEL_PHYSICAL_CLIENT_IMAGE_ID" \
  --client-executable-sha256 "$NETRATEL_PHYSICAL_CLIENT_EXECUTABLE_SHA256" \
  --candidate-version "$candidate_version" \
  --companion-source "$PHYSICAL_VERIFIED_COMPANION_SOURCE" \
  --companion-version "$PHYSICAL_VERIFIED_COMPANION_VERSION" \
  --companion-api-image "$PHYSICAL_VERIFIED_COMPANION_API_IMAGE" \
  --companion-web-image "$PHYSICAL_VERIFIED_COMPANION_WEB_IMAGE"
