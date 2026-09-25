#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

source tools/ci/load-prior-release.sh

project="netratel-postgresql-oidc-upgrade-${GITHUB_RUN_ID:-local}-${RANDOM}"
legacy_api_image="${NETRATEL_UPGRADE_PRIOR_API_IMAGE:?set NETRATEL_UPGRADE_PRIOR_API_IMAGE}"
legacy_migrations_image="${NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE}"
legacy_web_image="${NETRATEL_UPGRADE_PRIOR_WEB_IMAGE:?set NETRATEL_UPGRADE_PRIOR_WEB_IMAGE}"
legacy_client_image="${NETRATEL_UPGRADE_PRIOR_CLIENT_IMAGE:?set NETRATEL_UPGRADE_PRIOR_CLIENT_IMAGE}"
legacy_version="${NETRATEL_UPGRADE_PRIOR_VERSION:?set NETRATEL_UPGRADE_PRIOR_VERSION}"
current_api_image="${NETRATEL_UPGRADE_CURRENT_API_IMAGE:?set NETRATEL_UPGRADE_CURRENT_API_IMAGE}"
current_migrations_image="${NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE:?set NETRATEL_UPGRADE_CURRENT_MIGRATIONS_IMAGE}"
current_web_image="${NETRATEL_UPGRADE_CURRENT_WEB_IMAGE:?set NETRATEL_UPGRADE_CURRENT_WEB_IMAGE}"
candidate_client_archive="${NETRATEL_UPGRADE_CURRENT_CLIENT_ARCHIVE:?set NETRATEL_UPGRADE_CURRENT_CLIENT_ARCHIVE}"
bundle="${NETRATEL_UPGRADE_COMPOSE_BUNDLE:?set NETRATEL_UPGRADE_COMPOSE_BUNDLE}"
bundle_extract_directory="$(mktemp -d)"
agent_key_path="$(mktemp)"
cookie_jar="$(mktemp)"
tls_key_path="$(mktemp)"
tls_certificate_path="$(mktemp)"
tls_bundle_path="$(mktemp --suffix=.pfx)"
stage="initializing ${legacy_version} PostgreSQL/OIDC upgrade smoke"
active_compose=()
legacy_principal_count=""
client_volume="${project}-client-state"
native_directory="$(mktemp -d)"
native_user_created=false
native_image_container=""
upgrade_tenant_id=""
upgrade_machine_identity=""
client_unit="netratel-client.service"
updater_unit="netratel-update.service"
native_services_installed=false

[[ -s "$bundle" ]] || { echo "NETRATEL_UPGRADE_COMPOSE_BUNDLE is missing: $bundle" >&2; exit 1; }
[[ -s "$candidate_client_archive" ]] || { echo "The candidate Linux Client archive is missing." >&2; exit 1; }
tar -xzf "$bundle" -C "$bundle_extract_directory"
[[ -f "$bundle_extract_directory/compose.images.yaml" ]] || { echo "The extracted release bundle is missing compose.images.yaml." >&2; exit 1; }

legacy_compose=(docker compose --project-name "$project" -f release/compose.images.yaml -f tests/compose/oidc-smoke.compose.yaml)
current_compose=(docker compose --project-name "$project" -f "$bundle_extract_directory/compose.images.yaml" -f tests/compose/oidc-smoke.compose.yaml)

cleanup() {
  local status=$?
  if [[ "$native_services_installed" == true ]]; then
    sudo systemctl stop "$client_unit" "$updater_unit" >/dev/null 2>&1 || true
    sudo unlink "/etc/systemd/system/$client_unit" >/dev/null 2>&1 || true
    sudo unlink "/etc/systemd/system/$updater_unit" >/dev/null 2>&1 || true
    sudo unlink /etc/sudoers.d/netratel-upgrade-smoke >/dev/null 2>&1 || true
    sudo systemctl daemon-reload >/dev/null 2>&1 || true
    sudo systemctl reset-failed "$client_unit" "$updater_unit" >/dev/null 2>&1 || true
  fi
  if (( status != 0 )); then
    echo "::error title=PostgreSQL/OIDC upgrade smoke failed::${stage}" >&2
    if (( ${#active_compose[@]} > 0 )); then
      "${active_compose[@]}" ps --all >&2 || true
      "${active_compose[@]}" logs --no-color --tail 250 postgres migrations api web oidc >&2 || true
    fi
  fi
  if (( ${#active_compose[@]} > 0 )); then
    "${active_compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  fi
  unlink "$agent_key_path" "$tls_key_path" "$tls_certificate_path" "$tls_bundle_path" 2>/dev/null || true
  unlink "$cookie_jar" 2>/dev/null || true
  docker volume rm "$client_volume" >/dev/null 2>&1 || true
  if [[ -n "$native_image_container" ]]; then
    docker rm "$native_image_container" >/dev/null 2>&1 || true
  fi
  if [[ "$native_user_created" == true ]]; then
    sudo userdel --remove netratel >/dev/null 2>&1 || true
  fi
  sudo find "$native_directory" -depth -delete 2>/dev/null || true
  find "$bundle_extract_directory" -depth -delete 2>/dev/null || true
  return "$status"
}
trap cleanup EXIT

configure_images() {
  export NETRATEL_API_IMAGE="$1"
  export NETRATEL_MIGRATIONS_IMAGE="$2"
  export NETRATEL_WEB_IMAGE="$3"
}

wait_for_migrations() {
  local migration_id status
  migration_id="$("${active_compose[@]}" ps -a -q migrations)"
  [[ -n "$migration_id" ]] || { echo "Migration container was not created." >&2; return 1; }
  for _ in $(seq 1 90); do
    status="$(docker inspect --format '{{.State.Status}}' "$migration_id")"
    if [[ "$status" == exited ]]; then
      [[ "$(docker inspect --format '{{.State.ExitCode}}' "$migration_id")" == 0 ]] || return 1
      return 0
    fi
    sleep 1
  done
  echo "Migration container did not complete within 90 seconds." >&2
  return 1
}

wait_for_web() {
  for _ in $(seq 1 90); do
    if curl --connect-timeout 2 --silent --output /dev/null --write-out '%{http_code}' "$web_url/" | grep -Eq '^(200|302)$'; then
      return 0
    fi
    sleep 1
  done
  echo "The Web application did not become ready." >&2
  return 1
}

run_browser_oidc_smoke() {
  local expected_version="$1"
  local expect_current_shell="$2"
  local proxy_address
  proxy_address="$("${active_compose[@]}" port web-proxy 9444)"
  NETRATEL_BROWSER_SMOKE_WEB_URL="$web_url" \
    NETRATEL_BROWSER_SMOKE_PROXY_URL="https://${proxy_address}" \
    NETRATEL_BROWSER_SMOKE_USERNAME=netratel-test-operator \
    NETRATEL_BROWSER_SMOKE_EXPECTED_VERSION="$expected_version" \
    NETRATEL_BROWSER_SMOKE_EXPECT_CURRENT_SHELL="$expect_current_shell" \
    dotnet test src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj \
      --configuration Release --no-build --filter 'FullyQualifiedName~OidcComposeBrowserSmokeTests'
}

durable_oidc_principal_count() {
  "${active_compose[@]}" exec -T postgres psql -U netratel -d netratel -Atqc \
    'SELECT COUNT(*) FROM "ApplicationPrincipals" WHERE "ExternalIssuer" IS NOT NULL AND "ExternalSubject" IS NOT NULL;'
}

request_operator_access_token() {
  local redirect_uri authorization_url response callback_location callback_code token_response access_token
  redirect_uri="http://127.0.0.1:65535/netratel-smoke-callback"
  authorization_url="http://host.docker.internal:${NETRATEL_OIDC_TEST_PORT}/default/authorize?response_type=code&client_id=netratel-smoke-client&redirect_uri=http%3A%2F%2F127.0.0.1%3A65535%2Fnetratel-smoke-callback&scope=openid%20netratel.api&state=postgresql-oidc-upgrade"
  response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" \
    --cookie "$cookie_jar" --cookie-jar "$cookie_jar" "$authorization_url")"
  callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"

  if [[ -z "$callback_location" ]]; then
    response="$(curl --silent --show-error --dump-header - --resolve "$oidc_resolve" \
      --cookie "$cookie_jar" --cookie-jar "$cookie_jar" \
      --data-urlencode 'username=netratel-test-operator' "$authorization_url")"
    callback_location="$(awk 'BEGIN { IGNORECASE = 1 } /^location: / { sub(/^[^:]*: /, ""); sub(/\r$/, ""); print; exit }' <<<"$response")"
  fi

  if [[ -n "$callback_location" ]]; then
    [[ "$callback_location" == "${redirect_uri}"* ]] || {
      echo "The test OIDC provider returned an unexpected direct-token callback." >&2
      return 1
    }
    callback_code="$(node -e 'const callback = new URL(process.argv[1]); process.stdout.write(callback.searchParams.get("code") ?? "")' "$callback_location")"
  else
    callback_code="$(sed -n 's/.*name="code" value="\([^"]*\)".*/\1/p' <<<"$response" | head -n 1)"
  fi

  [[ -n "$callback_code" ]] || {
    echo "The test OIDC provider did not return an authorization code for direct API verification." >&2
    return 1
  }
  token_response="$(curl --silent --show-error --fail --resolve "$oidc_resolve" \
    --data-urlencode 'grant_type=authorization_code' \
    --data-urlencode 'client_id=netratel-smoke-client' \
    --data-urlencode 'client_secret=synthetic-compose-only-secret' \
    --data-urlencode "redirect_uri=${redirect_uri}" \
    --data-urlencode "code=${callback_code}" \
    "http://host.docker.internal:${NETRATEL_OIDC_TEST_PORT}/default/token")"
  access_token="$(jq -r '.access_token // empty' <<<"$token_response")"
  [[ -n "$access_token" ]] || {
    echo "The test OIDC provider did not issue a direct API access token." >&2
    return 1
  }
  printf '%s' "$access_token"
}

seed_historical_oidc_principal() {
  local access_token issuer subject principal_id
  access_token="$(request_operator_access_token)"
  issuer="$(node -e 'const token = process.argv[1]; const payload = token.split(".")[1]; process.stdout.write(JSON.parse(Buffer.from(payload, "base64url").toString("utf8")).iss ?? "")' "$access_token")"
  subject="$(node -e 'const token = process.argv[1]; const payload = token.split(".")[1]; process.stdout.write(JSON.parse(Buffer.from(payload, "base64url").toString("utf8")).sub ?? "")' "$access_token")"
  [[ -n "$issuer" && -n "$subject" ]] || {
    echo "The disposable OIDC token did not contain the verified issuer and subject needed for the historical fixture." >&2
    return 1
  }
  principal_id="$(tr -d '-' </proc/sys/kernel/random/uuid)"
  "${active_compose[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U netratel -d netratel \
    -v principal_id="$principal_id" -v issuer="$issuer" -v subject="$subject" -q <<'SQL'
-- Published prior release already projects the browser identity. Older fixtures may
-- need the same stable issuer/subject principal seeded explicitly.
INSERT INTO "ApplicationPrincipals" ("Id", "ExternalIssuer", "ExternalSubject", "CreatedAtUtc")
VALUES (:'principal_id', :'issuer', :'subject', CURRENT_TIMESTAMP)
ON CONFLICT ("ExternalIssuer", "ExternalSubject") DO NOTHING;
SQL
}

enroll_legacy_client() {
  local access_token tenant_response tenant_id enrollment_response enrollment_code auth_check_output auth_check_status
  access_token="$(request_operator_access_token)"
  tenant_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" --header 'Content-Type: application/json' \
    --data '{"name":"postgresql-oidc-upgrade","description":"Disposable historical upgrade tenant","location":"test","domains":[],"autoUpdate":false}' \
    "${api_url}/api/v1/tenants/")"
  tenant_id="$(jq -r '.tenantId // empty' <<<"$tenant_response")"
  [[ "$tenant_id" =~ ^[1-9][0-9]*$ ]] || {
    echo "Published ${legacy_version} OIDC authority could not create the historical tenant." >&2
    return 1
  }
  upgrade_tenant_id="$tenant_id"
  enrollment_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" --header 'Content-Type: application/json' \
    --data '{"validForMinutes":5,"maxUses":1,"note":"Disposable prior-release PostgreSQL/OIDC upgrade enrollment"}' \
    "${api_url}/api/v1/tenants/${tenant_id}/enrollment-codes")"
  enrollment_code="$(jq -r '.enrollmentCode // empty' <<<"$enrollment_response")"
  [[ -n "$enrollment_code" ]] || {
    echo "Published ${legacy_version} OIDC authority could not issue a historical Client enrollment code." >&2
    return 1
  }

  docker volume create "$client_volume" >/dev/null
  docker run --rm --user 0:0 --volume "${client_volume}:/var/lib/netratel" \
    --entrypoint /bin/sh "$legacy_client_image" -c 'chown -R netratel:netratel /var/lib/netratel'
  docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --enroll "$enrollment_code" >/dev/null
  set +e
  auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --auth-check 2>&1)"
  auth_check_status=$?
  set -e
  if (( auth_check_status != 0 )); then
    echo "Published ${legacy_version} Client authentication failed immediately after enrollment." >&2
    grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1 >&2 || true
    return 1
  fi
}

verify_legacy_client_after_upgrade() {
  local auth_check_output auth_check_status
  set +e
  auth_check_output="$(docker run --rm --network "${project}_default" --volume "${client_volume}:/var/lib/netratel" \
    "$legacy_client_image" --api http://api:9222 --auth-check 2>&1)"
  auth_check_status=$?
  set -e
  if (( auth_check_status != 0 )); then
    echo "The persisted ${legacy_version} Client could not authenticate against the upgraded candidate API." >&2
    grep -E '^.*\[Auth(Check)?\]' <<<"$auth_check_output" | tail -n 1 >&2 || true
    return 1
  fi
}

verify_native_legacy_client_after_upgrade() {
  local machine_identity native_status agent_path identity_path
  if id netratel >/dev/null 2>&1 || [[ -e /var/lib/netratel ]]; then
    echo "The disposable runner already has a netratel account or state directory." >&2
    return 1
  fi
  sudo useradd --system --create-home --home-dir /var/lib/netratel --shell /usr/sbin/nologin netratel
  native_user_created=true
  chmod 755 "$native_directory"
  mkdir -p "$native_directory/app" "$native_directory/state"
  native_image_container="$(docker create "$legacy_client_image")"
  docker cp "${native_image_container}:/app/." "$native_directory/app/"
  docker rm "$native_image_container" >/dev/null
  native_image_container=""
  sudo chown -R netratel:netratel "$native_directory/app"
  docker run --rm --volume "${client_volume}:/source:ro" \
    --volume "${native_directory}/state:/target" alpine:3.22 \
    sh -ceu 'cp -a /source/. /target/'
  agent_path="$(sudo find "$native_directory/state" -type f -name agent.dat -print -quit)"
  identity_path="$(sudo find "$native_directory/state" -type f -name .netratel-credential-machine-id -print -quit)"
  [[ -n "$agent_path" && -n "$identity_path" ]] && sudo test -s "$agent_path" && sudo test -s "$identity_path" || {
    echo "The published client state lacks an agent credential or persistent machine identity." >&2
    echo "Credential candidates: $(sudo find "$native_directory/state" -type f -name agent.dat | wc -l); identity candidates: $(sudo find "$native_directory/state" -type f -name .netratel-credential-machine-id | wc -l)." >&2
    return 1
  }
  sudo cp "$agent_path" /var/lib/netratel/agent.dat
  sudo cp "$identity_path" /var/lib/netratel/.netratel-credential-machine-id
  sudo chown -R netratel:netratel /var/lib/netratel
  machine_identity="$(sudo python3 - "$identity_path" <<'PY'
from pathlib import Path
import sys
print(Path(sys.argv[1]).read_text(encoding="utf-8-sig").strip())
PY
)"
  [[ -n "$machine_identity" ]] || {
    echo "The persisted client machine identity is empty." >&2
    return 1
  }
  upgrade_machine_identity="$machine_identity"
  if sudo -u netratel env HOME=/var/lib/netratel \
      NETRATEL_POWERSHELL_HOME=/var/lib/netratel/powershell \
      "NetRatel_CREDENTIAL_MACHINE_ID=${machine_identity}" \
      "$native_directory/app/NetRatel.Client" --api "$api_url" --auth-check \
      >"$native_directory/native-auth-check.log" 2>&1; then
    native_status=0
  else
    native_status=$?
  fi
  if (( native_status != 0 )); then
    echo "Published ${legacy_version} native Client could not authenticate with its existing identity after the candidate API upgrade." >&2
    python3 - "$native_directory/native-auth-check.log" "$native_status" <<'PY' >&2
import pathlib, sys
diagnostics = pathlib.Path(sys.argv[1]).read_text(errors="replace")
for label, pattern in (
    ("filesystem permission", "UnauthorizedAccessException"),
    ("missing runtime dependency", "Failed to create CoreCLR"),
    ("network or TLS", "HttpRequestException"),
    ("credential protection", "CryptographicException"),
    ("native dependency", "cannot open shared object file"),
):
    if pattern in diagnostics:
        print(f"Native authentication diagnostic category: {label}.")
        break
else:
    print(f"Native authentication diagnostic category: other; process exit {sys.argv[2]}.")
PY
    return 1
  fi
}

exercise_server_offered_client_update() {
  local version access_token agents prior_agent_id tenant_response update_request release_response
  local client_root updater_path candidate_zip attempt_response attempt_id attempt_state attempt_detail
  version="$(python3 tools/ci/product-version.py)"
  access_token="$(request_operator_access_token)"
  agents="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v1/tenants/${upgrade_tenant_id}/agents")"
  [[ "$(jq -r '.total' <<<"$agents")" == 1 ]] || {
    echo "The published client did not retain exactly one enrolled agent before update." >&2
    return 1
  }
  prior_agent_id="$(jq -r '.items[0].agentId // empty' <<<"$agents")"
  [[ "$prior_agent_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
    echo "The published client agent identity is missing before update." >&2
    return 1
  }

  (
    cd "$(dirname "$candidate_client_archive")"
    sha256sum --check --status SHA256SUMS
  ) || { echo "The candidate Client archive checksum is invalid." >&2; return 1; }
  candidate_zip="$native_directory/candidate-update.zip"
  python3 - "$candidate_client_archive" "$candidate_zip" <<'PY'
import pathlib, shutil, stat, sys, tarfile, zipfile

source, target = sys.argv[1:]
prefix = "netratel-client-linux-x64"
seen = set()
with tarfile.open(source, "r:gz") as archive, zipfile.ZipFile(target, "w") as output:
    for entry in archive:
        parts = pathlib.PurePosixPath(entry.name).parts
        if not parts or parts[0] != prefix or ".." in parts or entry.name.startswith("/"):
            raise SystemExit("Candidate Client archive contains an unsafe path.")
        if entry.isdir():
            continue
        if len(parts) < 2 or not entry.isfile():
            raise SystemExit("Candidate Client archive contains an unsupported entry.")
        name = "/".join(parts[1:])
        if name in seen:
            raise SystemExit("Candidate Client archive contains a duplicate entry.")
        seen.add(name)
        info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
        info.create_system = 3
        info.external_attr = (stat.S_IFREG | (entry.mode & 0o777)) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with archive.extractfile(entry) as item, output.open(info, "w") as destination:
            shutil.copyfileobj(item, destination)
if "netratel-client-manifest.json" not in seen or "NetRatel.Client" not in seen:
    raise SystemExit("Candidate Client archive is missing its update manifest or executable.")
PY

  client_root="$native_directory/client-root"
  updater_path="$client_root/updater/netratel-update.sh"
  mkdir -p "$client_root/updater" "$client_root/versions" "$native_directory/bin" \
    "$native_directory/bundle" "$native_directory/logs"
  sudo chown netratel:netratel "$native_directory/bundle" "$native_directory/logs"
  [[ -s "$native_directory/app/updater/netratel-update.sh" ]] || {
    echo "The published Client image is missing its installed Linux updater." >&2
    return 1
  }
  install -m 0755 "$native_directory/app/updater/netratel-update.sh" "$updater_path"
  tar -xOzf "$candidate_client_archive" netratel-client-linux-x64/updater/netratel-update.sh \
    > "$client_root/updater/.netratel-update.sh.replacement"
  chmod 0755 "$client_root/updater/.netratel-update.sh.replacement"
  mv -f "$client_root/updater/.netratel-update.sh.replacement" "$updater_path"
  ln -s "$native_directory/app" "$client_root/current"
  [[ ! -e /etc/sudoers.d/netratel-upgrade-smoke && ! -L /etc/sudoers.d/netratel-upgrade-smoke &&
     ! -e "/etc/systemd/system/$client_unit" && ! -L "/etc/systemd/system/$client_unit" &&
     ! -e "/etc/systemd/system/$updater_unit" && ! -L "/etc/systemd/system/$updater_unit" ]] &&
    ! systemctl cat "$client_unit" >/dev/null 2>&1 &&
    ! systemctl cat "$updater_unit" >/dev/null 2>&1 || {
    echo "The disposable runner already has a NetRatel service unit." >&2
    return 1
  }
  native_services_installed=true
  cat > "$native_directory/bin/systemctl" <<'SH'
#!/bin/sh
if [ "$#" -eq 2 ] && [ "$1" = start ] && [ "$2" = netratel-update.service ]; then
  exec /usr/bin/sudo -n /usr/bin/systemctl start netratel-update.service
fi
exit 1
SH
  chmod 0755 "$native_directory/bin/systemctl"
  sudo tee /etc/sudoers.d/netratel-upgrade-smoke >/dev/null <<'SUDOERS'
netratel ALL=(root) NOPASSWD: /usr/bin/systemctl start netratel-update.service
SUDOERS
  sudo chmod 0440 /etc/sudoers.d/netratel-upgrade-smoke
  sudo visudo -cf /etc/sudoers.d/netratel-upgrade-smoke >/dev/null
  sudo tee "/etc/systemd/system/$client_unit" >/dev/null <<UNIT
[Unit]
Description=Disposable NetRatel published Client upgrade smoke
After=network-online.target

[Service]
Type=simple
User=netratel
WorkingDirectory=$client_root/current
ExecStart=$client_root/current/NetRatel.Client --service --api $api_url
Restart=always
RestartSec=2
Environment=HOME=/var/lib/netratel
Environment=PATH=$native_directory/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
Environment=NETRATEL_POWERSHELL_HOME=/var/lib/netratel/powershell
Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_RUNNING_IN_CONTAINER=false
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$native_directory/bundle
Environment=NetRatel_CREDENTIAL_MACHINE_ID=$upgrade_machine_identity
Environment=NetRatel_CLIENT_LOG_DIR=$native_directory/logs
Environment=NetRatelCLIENT__Client__ApiBaseUrl=$api_url
Environment=NetRatelCLIENT__Client__AutoUpdate__Mode=Service
Environment=NetRatelCLIENT__Client__AutoUpdate__Channel=Prerelease
Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
Environment=NetRatelCLIENT__Gateway__Endpoint=https://127.0.0.1:$NETRATEL_GATEWAY_TEST_PORT
Environment=NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka
Environment=SSL_CERT_FILE=$tls_certificate_path

[Install]
WantedBy=multi-user.target
UNIT
  sudo tee "/etc/systemd/system/$updater_unit" >/dev/null <<UNIT
[Unit]
Description=Disposable NetRatel Client updater smoke

[Service]
Type=oneshot
TimeoutStartSec=240
ExecStart=$updater_path
Environment=NetRatel_UPDATE_ROOT=$client_root
Environment=NetRatel_UPDATE_STATE=/var/lib/netratel/update
Environment=NetRatel_CLIENT_SERVICE=$client_unit
UNIT
  sudo systemctl daemon-reload
  sudo systemctl start "$client_unit"
  for _ in $(seq 1 60); do
    if sudo test -s /var/lib/netratel/update/presence.json; then break; fi
    sleep 1
  done
  if ! sudo test -s /var/lib/netratel/update/presence.json; then
    echo "The published native Client did not acknowledge gateway presence within one minute." >&2
    echo "Client service state: $(systemctl is-active "$client_unit" 2>/dev/null || true)." >&2
    systemctl show "$client_unit" --property=ExecMainStatus,Result,NRestarts --no-pager >&2 || true
    sudo python3 - "$native_directory/logs" "$client_unit" <<'PY' >&2
import pathlib, re, subprocess, sys

logs = pathlib.Path(sys.argv[1])
lines = []
for path in logs.glob("*.log"):
    lines.extend(path.read_text(errors="replace").splitlines())
signals = {
    "authentication succeeded": "Access token acquired",
    "authentication failed": "Failed to acquire access token",
    "enrollment required": "Enrollment is required before starting the service",
    "API validation failed": "ApiBaseUrl is unreachable",
    "presence started": "Starting authenticated Akka presence",
    "presence admitted": "Presence admitted",
    "gateway session failed": "Gateway session failed",
    "update staging failed": "Akka update staging failed",
    "update acknowledgement failed": "Client update acknowledgement was ignored",
    "fatal startup failure": "[FATAL] Unhandled:",
}
for label, marker in signals.items():
    print(f"Native Client {label}: {sum(marker in line for line in lines)}.")
failures = [re.search(r"Gateway session failed: ([A-Za-z]+)", line)
            for line in lines if "Gateway session failed:" in line]
types = [match.group(1) for match in failures if match]
if types:
    print(f"Last gateway exception type: {types[-1]}.")
exception_types = sorted(set(re.findall(r"(?:System|Microsoft|Grpc)\.[A-Za-z.]*Exception", "\n".join(lines))))
print(f"Native Client exception types: {', '.join(exception_types[:8]) or 'none'}.")
print(f"Native Client log files: {len(list(logs.glob('*.log')))}.")
state = pathlib.Path("/var/lib/netratel/update")
print(f"Native Client update state directory exists: {state.is_dir()}.")
print(f"Native Client update state entry count: {len(list(state.iterdir())) if state.is_dir() else 0}.")
ignored = re.findall(r"Client update acknowledgement was ignored[^\n]*?: ([A-Za-z]+Exception):", "\n".join(lines))
print(f"Native Client update acknowledgement exception types: {', '.join(sorted(set(ignored))) or 'none'}.")
settings = logs.parent / "app" / "clientsettings.json"
print(f"Native Client legacy settings override exists: {settings.is_file()}.")
journal = subprocess.run(["journalctl", "--unit", sys.argv[2], "--no-pager", "--output=cat", "--lines=200"],
                         capture_output=True, text=True, check=False).stdout
journal_exceptions = sorted(set(re.findall(r"(?:System|Microsoft|Grpc)\.[A-Za-z.]*Exception", journal)))
print(f"Native service journal exception types: {', '.join(journal_exceptions[:8]) or 'none'}.")
for label, marker in (
    ("bundle extraction failure", "Failed to extract"),
    ("runtime initialization failure", "Failed to create CoreCLR"),
    ("logging permission failure", "[LoggingError]"),
    ("authentication succeeded", "Access token acquired"),
    ("authentication failed", "Failed to acquire access token"),
    ("update acknowledgement failed", "Client update acknowledgement was ignored"),
):
    print(f"Native service journal {label}: {journal.count(marker)}.")
PY
    if [[ -e /.dockerenv ]]; then
      echo "The runner has a container marker that disables service auto-update." >&2
    fi
    return 1
  fi

  upload_response="$(curl --silent --show-error --fail-with-body --max-time 300 \
    --header "Authorization: Bearer ${access_token}" \
    --form rid=linux-x64 --form "version=${version}" \
    --form "file=@${candidate_zip};type=application/zip" \
    "${api_url}/api/v1/client-artifacts/upload")"
  [[ "$(jq -r '.artifact.version // empty' <<<"$upload_response")" == "$version" ]] || {
    echo "The candidate Client package was not stored and published." >&2
    return 1
  }
  release_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v1/client-updates/releases?runtimeId=linux-x64")"
  jq -e --arg version "$version" \
    'any(.[]; .version == $version and .channel == "prerelease" and .enabled == true)' \
    <<<"$release_response" >/dev/null || {
      echo "The explicitly uploaded candidate is not a published prerelease update." >&2
      return 1
    }
  tenant_response="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v1/tenants/${upgrade_tenant_id}")"
  update_request="$(jq --arg version "$version" \
    '{name,description,location,domains,contactPerson,contactEmail,
      autoUpdate:true,autoUpdateChannel:"prerelease",autoUpdateTargetVersion:$version}' \
    <<<"$tenant_response")"
  curl --silent --show-error --fail-with-body --request PUT \
    --header "Authorization: Bearer ${access_token}" --header 'Content-Type: application/json' \
    --data "$update_request" "${api_url}/api/v1/tenants/${upgrade_tenant_id}" >/dev/null

  attempt_id=""
  for _ in $(seq 1 240); do
    attempt_response="$(curl --silent --show-error --fail \
      --header "Authorization: Bearer ${access_token}" \
      "${api_url}/api/v1/client-updates/attempts?clientIdentity=${prior_agent_id}")"
    attempt_id="$(jq -r --arg version "$version" --argjson tenant "$upgrade_tenant_id" \
      '[.[] | select(.version == $version and .tenantId == $tenant)] | first | .attemptId // empty' \
      <<<"$attempt_response")"
    attempt_state="$(jq -r --arg version "$version" --argjson tenant "$upgrade_tenant_id" \
      '[.[] | select(.version == $version and .tenantId == $tenant)] | first | .status // empty' \
      <<<"$attempt_response")"
    if [[ "$attempt_state" == Accepted ]]; then break; fi
    if [[ "$attempt_state" == FailedPreActivation || "$attempt_state" == RolledBack ||
          "$attempt_state" == RollbackUnverified ]]; then
      echo "The published Client update attempt ended in $attempt_state." >&2
      return 1
    fi
    if [[ "$attempt_state" == Activating ]] && systemctl is-failed --quiet "$updater_unit"; then
      break
    fi
    sleep 2
  done
  [[ "$attempt_state" == Accepted && -n "$attempt_id" ]] || {
    echo "The published Client did not accept the server-offered candidate within eight minutes." >&2
    echo "Last observed server attempt state: ${attempt_state:-none}." >&2
    if sudo test -s /var/lib/netratel/update/presence.json; then
      echo "The native Client recorded an acknowledged gateway presence." >&2
    else
      echo "The native Client did not record an acknowledged gateway presence." >&2
    fi
    systemctl show "$updater_unit" --property=ActiveState,SubState,Result,ExecMainStatus --no-pager >&2 || true
    systemctl show "$client_unit" --property=ActiveState,SubState,Result,ExecMainStatus,NRestarts --no-pager >&2 || true
    sudo python3 - /var/lib/netratel/update "$client_root" "$version" "$updater_unit" <<'PY' >&2
import json, pathlib, re, subprocess, sys

state, root, version, unit = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3], sys.argv[4]
owner = state.stat().st_uid if state.is_dir() else None
for name in ("request.json", "ready.json", "result.json", "state.json", "activation.json"):
    path = state / name
    present = path.is_file()
    print(f"Native updater {name} exists: {present}; readable by state owner: {present and path.stat().st_uid == owner and bool(path.stat().st_mode & 0o400)}.")
    if present and name in ("result.json", "state.json", "activation.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            for field in ("state", "failureCode", "stage", "errorCode"):
                value = data.get(field)
                if isinstance(value, str) and value:
                    print(f"Native updater {name} {field}: {value if re.fullmatch(r'[A-Za-z0-9_.=-]{1,64}', value) else 'other'}.")
        except (OSError, ValueError):
            print(f"Native updater {name} could not be parsed.")
print(f"Native updater candidate target active: {(root / 'current').resolve() == root / 'versions' / version}.")
journal = subprocess.run(["journalctl", "--unit", unit, "--no-pager", "--output=cat", "--lines=200"],
                         capture_output=True, text=True, check=False).stdout
for label, marker in (
    ("cutover started", "Cutover starting"),
    ("checksum verified", "Checksum verified"),
    ("client service stop requested", "Stopping netratel-client.service"),
    ("candidate service started", "waiting for readiness marker"),
    ("candidate service failed to start", "did not become active after cutover"),
    ("candidate accepted", "accepted."),
    ("rollback started", "Rolling back NetRatel client update"),
    ("activation timed out", "did not become ready"),
    ("updater failed", "client update failed with exit code"),
):
    print(f"Native updater journal {label}: {journal.count(marker)}.")
logs = root.parent / "logs"
lines = [line for path in logs.glob("*.log") for line in path.read_text(errors="replace").splitlines()]
for label, marker in (
    ("candidate started", f"Application {version} starting"),
    ("authentication succeeded", "Access token acquired"),
    ("authentication failed", "Failed to acquire access token"),
    ("enrollment required", "Enrollment is required before starting the service"),
    ("API unavailable", "ApiBaseUrl is unreachable"),
    ("gateway session failed", "Gateway session failed"),
    ("fatal startup failure", "[FATAL] Unhandled:"),
):
    print(f"Native client log {label}: {sum(marker in line for line in lines)}.")
exceptions = sorted(set(re.findall(r"(?:System|Microsoft|Grpc)\.[A-Za-z.]*Exception", "\n".join(lines))))
print(f"Native client log exception types: {', '.join(exceptions[:8]) or 'none'}.")
PY
    return 1
  }
  attempt_detail="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v1/client-updates/attempts/${attempt_id}")"
  jq -e --arg version "$version" --arg agent "$prior_agent_id" \
    --argjson tenant "$upgrade_tenant_id" \
    '.state == "Accepted" and .targetVersion == $version and .agentId == $agent and
      .tenantId == $tenant and .readmittedAtUtc != null and .confirmedAtUtc != null' \
    <<<"$attempt_detail" >/dev/null || {
      echo "The update attempt lacks same-agent readmission and confirmation." >&2
      return 1
    }
  [[ "$(readlink -f "$client_root/current")" == "$client_root/versions/$version" ]] || {
    echo "The accepted update did not activate the candidate Client package." >&2
    return 1
  }
  agents="$(curl --silent --show-error --fail \
    --header "Authorization: Bearer ${access_token}" \
    "${api_url}/api/v1/tenants/${upgrade_tenant_id}/agents")"
  [[ "$(jq -r '.total' <<<"$agents")" == 1 &&
     "$(jq -r '.items[0].agentId' <<<"$agents")" == "$prior_agent_id" ]] || {
    echo "The accepted update changed the enrolled agent identity or tenant scope." >&2
    return 1
  }
  echo "Published ${legacy_version} Client accepted the server-offered ${version} update with its original tenant and agent identity."
}

export POSTGRES_PASSWORD=synthetic-postgresql-oidc-upgrade-postgres-password
export OIDC_AUTHORITY=https://issuer.example.invalid
export OIDC_CLIENT_ID=synthetic-postgresql-oidc-upgrade-client
export OIDC_API_SCOPE=netratel.api
export OIDC_API_AUDIENCE=netratel.api
export OIDC_TOKEN_ENDPOINT=https://issuer.example.invalid/oauth/token
export OIDC_ADMIN_GROUP_ID=synthetic-postgresql-oidc-upgrade-group
export OIDC_CLIENT_SECRET=synthetic-postgresql-oidc-upgrade-oidc-secret
export NETRATEL_WEB_PORT="${NETRATEL_UPGRADE_OIDC_WEB_PORT:-18084}"
export NETRATEL_OIDC_TEST_PORT="${NETRATEL_UPGRADE_OIDC_PORT:-18085}"
export NETRATEL_GATEWAY_TEST_PORT="${NETRATEL_UPGRADE_GATEWAY_PORT:-19443}"
export NETRATEL_SMOKE_CLIENT_UPDATES_ENABLED=true
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$agent_key_path"
export NETRATEL_SMOKE_TLS_CERT_PASSWORD=synthetic-postgresql-oidc-upgrade-certificate-password
export NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"
export NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
export NETRATEL_SMOKE_TLS_CERT_PATH="$tls_bundle_path"
export NETRATEL_SMOKE_TLS_CERTIFICATE_PATH="$tls_certificate_path"
export NETRATEL_SMOKE_TLS_KEY_PATH="$tls_key_path"
web_url="http://127.0.0.1:${NETRATEL_WEB_PORT}"
api_url="http://127.0.0.1:${NETRATEL_API_TEST_PORT:-9222}"
oidc_resolve="host.docker.internal:${NETRATEL_OIDC_TEST_PORT}:127.0.0.1"

openssl ecparam -name prime256v1 -genkey -noout -out "$agent_key_path"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=gateway' \
  -addext 'subjectAltName=DNS:gateway,DNS:localhost,IP:127.0.0.1' \
  -keyout "$tls_key_path" -out "$tls_certificate_path" >/dev/null 2>&1
openssl pkcs12 -export -out "$tls_bundle_path" -inkey "$tls_key_path" -in "$tls_certificate_path" \
  -passout "pass:${NETRATEL_SMOKE_TLS_CERT_PASSWORD}" >/dev/null 2>&1
chmod 0644 "$agent_key_path" "$tls_key_path" "$tls_certificate_path" "$tls_bundle_path"

stage="building the OIDC browser probe"
dotnet restore src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj
dotnet build src/NetRatel/NetRatel.Web.PlaywrightTests/NetRatel.Web.PlaywrightTests.csproj --configuration Release --no-restore
pwsh src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium

stage="starting published ${legacy_version} PostgreSQL/OIDC images"
configure_images "$legacy_api_image" "$legacy_migrations_image" "$legacy_web_image"
active_compose=("${legacy_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the published ${legacy_version} OIDC browser journey"
run_browser_oidc_smoke "$legacy_version" false
stage="recording an existing ${legacy_version} PostgreSQL OIDC principal fixture"
seed_historical_oidc_principal
legacy_principal_count="$(durable_oidc_principal_count)"
[[ "$legacy_principal_count" == 1 ]] || { echo "The historical PostgreSQL fixture did not retain exactly one external issuer/subject principal." >&2; exit 1; }
stage="enrolling and authenticating a published ${legacy_version} Client"
enroll_legacy_client

stage="upgrading the published PostgreSQL/OIDC state with extracted candidate images"
"${active_compose[@]}" down --remove-orphans
configure_images "$current_api_image" "$current_migrations_image" "$current_web_image"
active_compose=("${current_compose[@]}")
"${active_compose[@]}" up --detach
wait_for_migrations
curl --retry 20 --retry-connrefused --fail --silent --show-error "http://127.0.0.1:${NETRATEL_OIDC_TEST_PORT}/isalive" >/dev/null
wait_for_web
stage="authenticating through the upgraded PostgreSQL/OIDC browser journey"
run_browser_oidc_smoke "v$(python3 tools/ci/product-version.py)" true
[[ "$(durable_oidc_principal_count)" == "$legacy_principal_count" ]] || {
  echo "The upgraded PostgreSQL/OIDC stack did not retain the durable ${legacy_version} external principal set." >&2
  exit 1
}
stage="authenticating the persisted published ${legacy_version} Client against candidate images"
verify_legacy_client_after_upgrade
stage="authenticating the published ${legacy_version} Client natively with its persisted identity"
verify_native_legacy_client_after_upgrade
stage="updating the enrolled published Client through the established native updater"
exercise_server_offered_client_update
echo "Published ${legacy_version} PostgreSQL/OIDC and persisted Client authentication continuity passed."
