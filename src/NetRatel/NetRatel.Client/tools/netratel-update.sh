#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="${NetRatel_UPDATE_ROOT:-/opt/netratel/client}"
STATE_DIR="${NetRatel_UPDATE_STATE:-/var/lib/netratel/update}"
SERVICE_NAME="${NetRatel_CLIENT_SERVICE:-netratel-client.service}"
REQUEST_PATH="${NetRatel_UPDATE_REQUEST:-${STATE_DIR}/request.json}"
LOCK_PATH="${STATE_DIR}/update.lock"
VERSIONS_DIR="${ROOT_DIR}/versions"
STAGING_DIR="${ROOT_DIR}/staging"
FAILED_DIR="${ROOT_DIR}/failed"
CURRENT_LINK="${ROOT_DIR}/current"

log() {
  local line
  line="$(printf '%s [UpdateService] %s' "$(date -u +%FT%TZ)" "$*")"
  printf '%s\n' "$line"
  if [ -n "${LOG_PATH:-}" ]; then
    mkdir -p "$(dirname "$LOG_PATH")" 2>/dev/null || true
    printf '%s\n' "$line" >> "$LOG_PATH" 2>/dev/null || true
  fi
}
json_value() {
  local key="$1"
  python3 - "$REQUEST_PATH" "$key" <<'PY'
import json, sys
# Requests are written by both current and legacy coordinators.  Some .NET
# writers emit a UTF-8 BOM, which is valid UTF-8 but rejected by Python's
# plain ``utf-8`` codec when it appears at the beginning of JSON.
with open(sys.argv[1], encoding="utf-8-sig") as f:
    data = json.load(f)
print(data.get(sys.argv[2], ""))
PY
}
write_result() {
  local status="$1"
  local message="$2"
  local error="${3:-}"
  local path="${RESULT_PATH:-${STATE_DIR}/result.json}"
  mkdir -p "$(dirname "$path")"
  python3 - "$path" "$status" "$message" "$error" <<'PY'
import json, os, sys, datetime
path, status, message, error = sys.argv[1:5]
payload = {
  "schema": "netratel.update.result.v2",
  "attemptId": os.environ.get("ATTEMPT_ID") or "",
  "releaseId": os.environ.get("RELEASE_ID") or "",
  "runtimeId": os.environ.get("RUNTIME_ID") or "",
  "fromVersion": os.environ.get("FROM_VERSION") or "",
  "toVersion": os.environ.get("VERSION") or "",
  "version": os.environ.get("VERSION") or "",
  "state": status,
  "failureCode": error,
  "message": message,
  "previousTarget": os.environ.get("PREVIOUS_TARGET") or "",
  "activeTarget": os.environ.get("ACTIVE_TARGET") or "",
  "error": error,
  "completedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
}
temporary = path + ".tmp"
temporary = f"{path}.{os.getpid()}.{os.urandom(4).hex()}.tmp"
with open(temporary, "w", encoding="utf-8") as f:
  json.dump(payload, f, indent=2)
os.chmod(temporary, 0o600)
os.replace(temporary, path)
PY
}
write_state() {
  local status="$1"
  local version="$2"
  python3 - "$STATE_PATH" "$status" "$version" <<'PY'
import json, os, sys, datetime
path, state, version = sys.argv[1:4]
payload={"state":state,"version":version,
         "updatedAtUtc":datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00","Z")}
temporary=f"{path}.{os.getpid()}.{os.urandom(4).hex()}.tmp"
with open(temporary,"w",encoding="utf-8") as f: json.dump(payload,f,separators=(",",":"))
os.chmod(temporary,0o600)
os.replace(temporary,path)
PY
}
ready_matches() {
  python3 - "$READY_PATH" "$ATTEMPT_ID" "$RELEASE_ID" "$VERSION" <<'PY'
import json, os, sys
try:
    with open(sys.argv[1], encoding="utf-8-sig") as f: data=json.load(f)
    ok=(data.get("schema")=="netratel.update.ready.v2" and data.get("attemptId")==sys.argv[2]
        and data.get("releaseId")==sys.argv[3] and data.get("version")==sys.argv[4]
        and bool(data.get("confirmationId")))
    raise SystemExit(0 if ok else 1)
except Exception:
    raise SystemExit(1)
PY
}
on_error() {
  local code=$?
  trap - ERR
  log "NetRatel client update failed with exit code $code."
  if [ "${CUTOVER_STARTED:-false}" = true ]; then
    rollback_update "post_cutover_failure_exit_$code"
  else
    write_result "FailedPreActivation" "NetRatel client update failed before completion." "exitCode=$code"
  fi
  exit "$code"
}
trap on_error ERR

mkdir -p "$STATE_DIR" "$VERSIONS_DIR" "$STAGING_DIR" "$FAILED_DIR"
exec 9>"$LOCK_PATH"
if ! flock -n 9; then
  log "Another NetRatel update is already running."
  exit 0
fi

if [ ! -s "$REQUEST_PATH" ]; then
  log "No NetRatel update request found at $REQUEST_PATH."
  exit 0
fi

VERSION="$(json_value toVersion)"
if [ -z "$VERSION" ]; then VERSION="$(json_value version)"; fi
PACKAGE_PATH="$(json_value packagePath)"
SHA256="$(json_value sha256)"
READY_PATH="$(json_value readyPath)"
CURRENT_VERSION="$(json_value fromVersion)"
if [ -z "$CURRENT_VERSION" ]; then CURRENT_VERSION="$(json_value currentVersion)"; fi
LOG_PATH="$(json_value logPath)"
RESULT_PATH="$(json_value resultPath)"
if [ -z "$RESULT_PATH" ]; then RESULT_PATH="${STATE_DIR}/result.json"; fi
RELEASE_ID="$(json_value releaseId)"
ATTEMPT_ID="$(json_value attemptId)"
RUNTIME_ID="$(json_value runtimeId)"
PRESENCE_PATH="$(json_value presencePath)"
FROM_VERSION="$CURRENT_VERSION"
export VERSION RELEASE_ID ATTEMPT_ID RUNTIME_ID FROM_VERSION RESULT_PATH

if [ -z "$VERSION" ] || [ -z "$PACKAGE_PATH" ] || [ -z "$SHA256" ] || [ -z "$ATTEMPT_ID" ] || [ -z "$RELEASE_ID" ]; then
  log "Update request is missing version, packagePath, or sha256."
  write_result "FailedPreActivation" "Update request is missing version, packagePath, or sha256."
  exit 1
fi
if ! python3 - "$VERSION" "$CURRENT_VERSION" <<'PY'
import re, sys
pattern = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")
def parse(value):
    match = pattern.fullmatch(value)
    if not match:
        raise ValueError("invalid semantic version")
    core = tuple(map(int, value.split('+', 1)[0].split('-', 1)[0].split('.')))
    prerelease = value.split('+', 1)[0].split('-', 1)
    prerelease = () if len(prerelease) == 1 else tuple(prerelease[1].split('.'))
    if any(part.isdigit() and len(part) > 1 and part.startswith('0') for part in prerelease):
        raise ValueError("invalid numeric prerelease identifier")
    return core, bool(prerelease), prerelease
def compare_prerelease(left, right):
    for a, b in zip(left, right):
        if a == b:
            continue
        a_numeric, b_numeric = a.isdigit(), b.isdigit()
        if a_numeric and b_numeric:
            return (int(a) > int(b)) - (int(a) < int(b))
        if a_numeric != b_numeric:
            return -1 if a_numeric else 1
        return (a > b) - (a < b)
    return (len(left) > len(right)) - (len(left) < len(right))
try:
    candidate = sys.argv[1]
    current = sys.argv[2]
    candidate_core, candidate_pre, candidate_tag = parse(candidate)
    current_core, current_pre, current_tag = parse(current)
    if candidate_core < current_core or (candidate_core == current_core and (
        (candidate_pre and not current_pre) or
        (candidate_pre and current_pre and compare_prerelease(candidate_tag, current_tag) <= 0) or
        (candidate_pre == current_pre and not candidate_pre))):
        raise ValueError("candidate is not newer")
except Exception:
    raise SystemExit(1)
PY
then
  log "Update request contains an invalid or non-increasing version."
  write_result "FailedPreActivation" "Update request version is invalid or not newer than the running version." "invalid_version"
  exit 1
fi
if [ ! -s "$PACKAGE_PATH" ]; then
  log "Staged package was not found at $PACKAGE_PATH."
  write_result "FailedPreActivation" "Staged package was not found at $PACKAGE_PATH."
  exit 1
fi

STATE_PATH="${STATE_DIR}/state.json"
ZIP_PATH="$PACKAGE_PATH"
EXTRACT_DIR="$(mktemp -d "${STAGING_DIR}/.${VERSION}.XXXXXX")"
TARGET_DIR="${VERSIONS_DIR}/${VERSION}"
PREVIOUS_TARGET=""
if [ -L "$CURRENT_LINK" ]; then
  PREVIOUS_TARGET="$(readlink -f "$CURRENT_LINK" || true)"
fi
export PREVIOUS_TARGET
CUTOVER_STARTED=false

rollback_update() {
  local reason="$1"
  CUTOVER_STARTED=false
  log "Rolling back NetRatel client update $VERSION. reason=$reason"
  systemctl stop "$SERVICE_NAME" || true
  if systemctl is-active --quiet "$SERVICE_NAME"; then
    log "Rollback could not stop $SERVICE_NAME; leaving the current target untouched."
    write_result "RollbackUnverified" "Rollback could not quiesce the client service." "rollback_stop_failed"
    return
  fi
  mkdir -p "$FAILED_DIR"
  if [ -d "$TARGET_DIR" ]; then
    mv "$TARGET_DIR" "${FAILED_DIR}/${VERSION}-$(date -u +%Y%m%d%H%M%S)" || true
  fi
  if [ -n "$PREVIOUS_TARGET" ] && [ -d "$PREVIOUS_TARGET" ]; then
    ln -sfn "$PREVIOUS_TARGET" "${CURRENT_LINK}.next"
    mv -Tf "${CURRENT_LINK}.next" "$CURRENT_LINK"
  fi
  ACTIVE_TARGET="$(readlink -f "$CURRENT_LINK" || true)"
  export ACTIVE_TARGET
  rm -f "$PRESENCE_PATH"
  systemctl start "$SERVICE_NAME"
  if ! systemctl is-active --quiet "$SERVICE_NAME"; then
    write_result "RollbackUnverified" "Rollback restored the link but the client service did not become active." "rollback_start_failed"
    return
  fi
  local rollback_deadline=$((SECONDS + ${NetRatel_UPDATE_ROLLBACK_CHECKIN_TIMEOUT:-120}))
  local rollback_verified=false
  while [ "$SECONDS" -lt "$rollback_deadline" ]; do
    if [ -s "$PRESENCE_PATH" ] && grep -q "\"version\"[[:space:]]*:[[:space:]]*\"$CURRENT_VERSION\"" "$PRESENCE_PATH"; then
      rollback_verified=true
      break
    fi
    sleep 5
  done
  local suspension_path="${STATE_DIR}/suspension.json"
  python3 - "$suspension_path" "$ATTEMPT_ID" "$RELEASE_ID" "$reason" <<'PY'
import json, os, sys, datetime
path=sys.argv[1]
payload={"schema":"netratel.update.suspension.v1","attemptId":sys.argv[2],"releaseId":sys.argv[3],
         "reason":sys.argv[4],"suspendedAtUtc":datetime.datetime.now(datetime.timezone.utc).isoformat()}
with open(path+".tmp","w",encoding="utf-8") as f: json.dump(payload,f,indent=2)
os.chmod(path+".tmp",0o600)
os.replace(path+".tmp",path)
PY
  write_state "rolled_back" "$VERSION"
  log "Rollback completed for NetRatel client update $VERSION."
  if [ "$rollback_verified" = true ]; then
    write_result "RolledBack" "Update activation failed; rollback reconnected." "$reason"
  else
    write_result "RollbackUnverified" "Rollback was applied but gateway check-in was not observed." "rollback_checkin_timeout"
  fi
}

log "Cutover starting for NetRatel client update $VERSION."
write_state "verifying_package" "$VERSION"
ACTUAL_SHA="$(sha256sum "$ZIP_PATH" | awk '{print $1}')"
if [ "${ACTUAL_SHA,,}" != "${SHA256,,}" ]; then
  log "Checksum mismatch for $VERSION."
  write_result "FailedPreActivation" "Checksum mismatch for $VERSION."
  exit 1
fi
log "Checksum verified for NetRatel client update $VERSION."

unzip -o "$ZIP_PATH" -d "$EXTRACT_DIR" >/dev/null
python3 - "$EXTRACT_DIR/netratel-client-manifest.json" "$VERSION" "$RUNTIME_ID" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding="utf-8-sig") as f: manifest=json.load(f)
assert manifest.get("schema")=="netratel.client.manifest.v1"
assert manifest.get("product")=="NetRatel.Client"
assert manifest.get("version")==sys.argv[2]
assert manifest.get("runtimeId")==sys.argv[3]
commit=manifest.get("commitSha", "")
assert len(commit)==40 and all(ch in "0123456789abcdefABCDEF" for ch in commit)
assert os.path.isfile(os.path.join(os.path.dirname(sys.argv[1]), manifest.get("executable", "")))
PY
CLIENT_EXE="${EXTRACT_DIR}/NetRatel.Client"
if [ ! -f "$CLIENT_EXE" ]; then
  CLIENT_EXE="${EXTRACT_DIR}/NetRatel.Client"
fi
if [ ! -f "$CLIENT_EXE" ]; then
  log "Client executable was not found in staged artifact."
  write_result "FailedPreActivation" "Client executable was not found in staged artifact."
  exit 1
fi
chmod +x "$CLIENT_EXE"

if [ -e "$TARGET_DIR" ]; then
  log "Immutable target $TARGET_DIR already exists; refusing to replace it."
  write_result "FailedPreActivation" "Immutable target version already exists." "target_exists"
  exit 1
fi

write_state "applying" "$VERSION"
log "Stopping $SERVICE_NAME for NetRatel client update $VERSION."
CUTOVER_STARTED=true
systemctl stop "$SERVICE_NAME"
if systemctl is-active --quiet "$SERVICE_NAME"; then
  log "$SERVICE_NAME remained active after stop."
  write_result "FailedPreActivation" "Client service did not stop before activation." "service_stop_failed"
  exit 1
fi
mv "$EXTRACT_DIR" "$TARGET_DIR"
ln -sfn "$TARGET_DIR" "${CURRENT_LINK}.next"
mv -Tf "${CURRENT_LINK}.next" "$CURRENT_LINK"
ACTIVE_TARGET="$(readlink -f "$CURRENT_LINK" || true)"
export ACTIVE_TARGET
systemctl start "$SERVICE_NAME"
if ! systemctl is-active --quiet "$SERVICE_NAME"; then
  log "$SERVICE_NAME did not become active after cutover."
  exit 1
fi
log "Started $SERVICE_NAME with NetRatel client update $VERSION; waiting for readiness marker."

write_state "verifying" "$VERSION"
deadline=$((SECONDS + ${NetRatel_UPDATE_ACTIVATION_TIMEOUT:-180}))
while [ "$SECONDS" -lt "$deadline" ]; do
  if [ -s "$READY_PATH" ] && ready_matches; then
    CUTOVER_STARTED=false
    write_state "accepted" "$VERSION"
    find "$VERSIONS_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | awk 'NR>3 {print $2}' | xargs -r rm -rf
    log "NetRatel client update $VERSION accepted."
    write_result "Accepted" "NetRatel client update $VERSION accepted."
    exit 0
  fi
  sleep 5
done

log "NetRatel client update $VERSION did not become ready; rolling back."
trap - ERR
rollback_update "activation_timeout"
exit 2
