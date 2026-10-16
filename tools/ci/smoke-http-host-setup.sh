#!/usr/bin/env bash
set -euo pipefail
umask 077

# Optional hosted acceptance only. Reuse the source Compose, public HTTPS,
# unattended bootstrap and pairing fixtures; never publish these images. The
# actual deployment is one Compose document, as in a Komodo Compose editor.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
[[ "${GITHUB_ACTIONS:-false}" == true ]] || {
  echo "Run this disposable source-container smoke through Optional integration validation." >&2
  exit 2
}
image="${NETRATEL_HTTP_HOST_SETUP_SMOKE_IMAGE:?set the hosted source tooling image}"
temporary="$(mktemp -d)"
deployment_uid="$(id -u)"
deployment_gid="$(id -g)"
[[ "$deployment_uid" != 10001 ]] || { echo "The hosted deployment owner must differ from the MCP tooling identity." >&2; exit 2; }
project="netratel-http-host-${GITHUB_RUN_ID}-${RANDOM}"
export COMPOSE_PROJECT_NAME="$project"
export NETRATEL_SETUP_VOLUME="${project}-http-host-setup"
export NETRATEL_CLIENT_VOLUME="${project}-http-client-data"
export NETRATEL_MCP_HTTP_IMAGE="$image"
export NETRATEL_AUTHENTICATION_MODE=Local
export NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential
export NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true
export NETRATEL_ALLOW_INSECURE_LOCALHOST=false
export POSTGRES_PASSWORD=disposable-http-host-postgres-passphrase
export NETRATEL_PUBLIC_HOST=netratel.example.test
export NETRATEL_PUBLIC_API_HOST=netratel-api.example.test
export NETRATEL_HTTPS_BIND_ADDRESS=0.0.0.0
export NETRATEL_HTTPS_PORT=443
export NETRATEL_WEB_PORT=19081
export NETRATEL_PUBLIC_ORIGIN="https://${NETRATEL_PUBLIC_HOST}"
export NETRATEL_PUBLIC_API_ORIGIN="https://${NETRATEL_PUBLIC_API_HOST}"
export NETRATEL_PUBLIC_GATEWAY_ORIGIN="$NETRATEL_PUBLIC_ORIGIN"
export NETRATEL_MCP_PUBLIC_RESOURCE_URI="$NETRATEL_PUBLIC_ORIGIN/mcp"
export NETRATEL_HTTPS_CERTIFICATE="$temporary/tls.crt"
export NETRATEL_HTTPS_PRIVATE_KEY="$temporary/tls.key"
export NETRATEL_AGENT_AUTH_PRIVATE_KEY="$temporary/agent.key"
source_compose=(docker compose -f compose.yaml -f release/compose.public-https.yaml -f release/compose.mcp-http.yaml -f "$temporary/fixture.yaml")
compose=(docker compose -f "$temporary/compose.yaml")
applied=("${compose[@]}")
launcher=(bash "$root/release/setup-http-mcp.sh" --compose-file "$temporary/compose.yaml" --project "$project" --manager external-compose)
host_setup=(--non-interactive
  --api-base-url "$NETRATEL_PUBLIC_API_ORIGIN"
  --api-m2m-authority "$NETRATEL_PUBLIC_ORIGIN" --api-m2m-audience netratel.api
  --existing-m2m-callers unrelated-caller)
stage="preparing disposable HTTP host setup"
retain_failure_diagnostics() {
  # Keep raw diagnostics private. Only bounded redacted copies leave this fixture.
  timeout 10s "${compose[@]}" logs --no-color --tail 120 api web 2>&1 \
    | head -c 524288 > "$temporary/startup.log" || true
  timeout 10s "${compose[@]}" config --format json 2>/dev/null \
    | head -c 524288 > "$temporary/redaction-config.json" || true
  : > "$temporary/container-states.jsonl"
  for service in api web; do
    local container
    container="$("${compose[@]}" ps --all --quiet "$service" 2>/dev/null)" || continue
    [[ -n "$container" ]] || continue
    timeout 5s docker inspect --format \
      '{"service":"'"$service"'","exitCode":{{.State.ExitCode}},"oomKilled":{{.State.OOMKilled}},"status":{{json .State.Status}},"error":{{json .State.Error}}}' \
      "$container" >> "$temporary/container-states.jsonl" 2>/dev/null || true
  done
  python3 - "$temporary" TestResults/http-host-setup/diagnostics <<'PY'
import json, pathlib, re, sys
private, output = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]); output.mkdir(parents=True, exist_ok=True)
values = set()
inventory = private / "redaction-values.json"
if inventory.is_file(): values.update(json.loads(inventory.read_text()))
def collect(item):
    if isinstance(item, dict):
        for key, value in item.items():
            if isinstance(value, str) and value and re.search(r"(?:password|passphrase|clientsecret|sharedkeybase64|(?:^|__)secret)$|^connectionstrings", key, re.I):
                values.add(value)
                values.update(re.findall(r"(?:^|;)Password=([^;]+)", value, re.I))
            else: collect(value)
    elif isinstance(item, list):
        for value in item: collect(value)
try: collect(json.loads((private / "redaction-config.json").read_text()))
except (OSError, json.JSONDecodeError): pass
files = [private / name for name in ("startup.log", "container-states.jsonl", "launcher.log", "verified-launcher.log",
    "fresh-verified-launcher.log", "changed-source-launcher.log", "customer-policy-recipe.log", "customer-policy.json")]
files += sorted(private.glob(".netratel-http-mcp-*/*.log"))
for index, path in enumerate(files):
    if not path.is_file(): continue
    with path.open("rb") as source: value = source.read(524288).decode("utf-8", errors="replace")
    for secret in sorted((value for value in values if len(value) >= 4), key=len, reverse=True):
        value = value.replace(secret, "[REDACTED]")
    value = re.sub(r"nrt_(?:ic|host)_[A-Za-z0-9_.-]+", "[REDACTED]", value)
    value = re.sub(r"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[REDACTED]", value)
    value = re.sub(r"(?i)(Authorization\s*[:=]\s*(?:Bearer|Basic)\s+)\S+", r"\1[REDACTED]", value)
    (output / f"{index:02d}-{path.name}").write_text(value)
PY
  if [[ -f TestResults/http-host-setup/diagnostics/00-startup.log ]]; then
    tail -n 60 TestResults/http-host-setup/diagnostics/00-startup.log >&2
  fi
  if [[ -f TestResults/http-host-setup/diagnostics/01-container-states.jsonl ]]; then
    cat TestResults/http-host-setup/diagnostics/01-container-states.jsonl >&2
  fi
}
cleanup() {
  local status=$?
  if [[ -n "${bounded_pairing:-}" ]]; then
    post /api/integration-pairing/v1/cancel "$(jq '{deviceCapability}' <<< "$bounded_pairing")" >/dev/null 2>&1 || true
  fi
  if (( status != 0 )); then
    echo "::error title=HTTP host setup smoke failed::${stage}" >&2
    for receipt in TestResults/http-host-setup/personal-{saved,reloaded}.json; do
      if [[ -s "$receipt" ]]; then
        jq -r 'if type == "array" then . else .checks // [] end
          | .[] | "\(.name): \(.status): \(.detail // .message // "")"' "$receipt" >&2 || true
      fi
    done
    "${compose[@]}" ps --all >&2 || true
    retain_failure_diagnostics || true
  fi
  "${applied[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  "${compose[@]}" down --volumes --remove-orphans --rmi local >/dev/null 2>&1 || true
  docker volume rm "$NETRATEL_SETUP_VOLUME" "$NETRATEL_CLIENT_VOLUME" >/dev/null 2>&1 || true
  rm -rf "$temporary"
  return "$status"
}
trap cleanup EXIT
chmod 755 "$temporary"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$NETRATEL_HTTPS_PRIVATE_KEY" \
  -out "$NETRATEL_HTTPS_CERTIFICATE" -subj /CN=netratel.example.test \
  -addext 'subjectAltName=DNS:netratel.example.test,DNS:netratel-api.example.test,IP:127.0.0.1' \
  -addext 'basicConstraints=critical,CA:TRUE' -days 1 >/dev/null 2>&1
chmod 644 "$NETRATEL_HTTPS_CERTIFICATE"
chmod 600 "$NETRATEL_HTTPS_PRIVATE_KEY"
cat /etc/ssl/certs/ca-certificates.crt "$NETRATEL_HTTPS_CERTIFICATE" > "$temporary/trust.pem"
chmod 644 "$temporary/trust.pem"
openssl ecparam -name prime256v1 -genkey -noout -out "$NETRATEL_AGENT_AUTH_PRIVATE_KEY"
docker run --rm --volume "$temporary:/fixture" alpine:3.22 \
  sh -ceu 'chown 1654:1654 /fixture/agent.key; chmod 600 /fixture/agent.key'
cat > "$temporary/fixture.yaml" <<EOF
services:
  ingress:
    volumes:
      - "$temporary/ingress.conf:/etc/nginx/conf.d/default.conf:ro"
  mcp-http:
    extra_hosts:
      - "$NETRATEL_PUBLIC_HOST:host-gateway"
      - "$NETRATEL_PUBLIC_API_HOST:host-gateway"
    volumes:
      - "$temporary/trust.pem:/etc/ssl/certs/ca-certificates.crt:ro"
EOF
cat > "$temporary/ingress.conf" <<'EOF'
server {
  listen 443 ssl;
  server_name netratel.example.test;
  ssl_certificate /run/netratel-ingress/tls.crt;
  ssl_certificate_key /run/netratel-ingress/tls.key;
  location / {
    proxy_pass http://web:9111;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-Host $http_host;
    proxy_set_header X-Forwarded-Proto https;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_read_timeout 120s;
  }
}
server {
  listen 443 ssl;
  server_name netratel-api.example.test;
  ssl_certificate /run/netratel-ingress/tls.crt;
  ssl_certificate_key /run/netratel-ingress/tls.key;
  location / {
    proxy_pass http://api:9222;
    proxy_http_version 1.1;
    proxy_set_header Host $http_host;
    proxy_set_header X-Forwarded-Host $http_host;
    proxy_set_header X-Forwarded-Proto https;
    proxy_set_header X-Forwarded-For $remote_addr;
  }
}
EOF
# Begin with the reported manually supplied identity: a read-only outbound JSON,
# dev instance and canonical inline delegation settings. Its backend registration
# is deliberately absent. Saving/adopting configuration alone must not pass.
"${source_compose[@]}" config --format json > "$temporary/compose.yaml"
python3 - "$temporary" "$NETRATEL_PUBLIC_ORIGIN" "$NETRATEL_PUBLIC_API_ORIGIN" <<'PY'
import base64, json, pathlib, secrets, sys
directory, origin, api = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
path = directory / "compose.yaml"
document = json.loads(path.read_text())
services = document["services"]
services["api"]["environment"].update({
    "ASPNETCORE_ENVIRONMENT": "Development", "M2M__Authority": origin,
    "M2M__Audience": "netratel.api", "M2M__AllowedCallerClientIds__0": "unrelated-caller",
    "M2MClients__unrelated_caller__Secret": secrets.token_urlsafe(48),
    "M2MClients__unrelated_caller__AllowedScopes__0": "unrelated.scope",
    "M2MClients__unrelated_caller__AllowedAudiences__0": "unrelated.audience",
    "Integration__HttpMcp__AuthenticationMode": ""})
services["web"]["environment"]["ASPNETCORE_ENVIRONMENT"] = "Development"
mcp = services["mcp-http"]
managed_env = directory / "managed.env"
managed_env.write_text("NETRATEL_ACCEPTANCE_UNRELATED=preserved\nNetRatel__Mcp__Delegation__LifetimeSeconds=90\n")
managed_env.chmod(0o600)
for service in (mcp, services["api"]):
    service["env_file"] = [{"path": str(managed_env), "required": True}]
environment = mcp["environment"]
environment.update({"NETRATEL_MCP_INSTANCE": "dev", "NETRATEL_MCP_CONFIG": "/run/secrets/mcp-outbound-config",
    "NETRATEL_MCP_DEV_API_BASE_URL": api,
    "NetRatel__Mcp__Http__LocalCredentialMode": "true",
    "NetRatel__Mcp__Http__OperatorSurfaceEnabled": "true",
    "NetRatel__Mcp__Delegation__Enabled": "true",
    "NetRatel__Mcp__Delegation__Issuer": "netratel-mcp-dev",
    "NetRatel__Mcp__Delegation__Audience": "netratel-api-dev",
    "NetRatel__Mcp__Delegation__ServicePrincipal": "disposable-mcp-api",
    "NetRatel__Mcp__Delegation__KeyId": "existing-disposable-generation",
    "NetRatel__Mcp__Delegation__SharedKeyBase64": base64.b64encode(secrets.token_bytes(32)).decode()})
services["api"]["environment"].update({key: value for key, value in environment.items()
    if key.startswith("NetRatel__Mcp__Delegation__")})
# No setup/profile mounts initially: preflight must recover this exact failure.
environment.pop("NETRATEL_CLIENT_DIRECTORY", None)
environment.pop("NETRATEL_CLIENT_VOLUME", None)
mcp["volumes"] = [mount for mount in mcp.get("volumes", []) if mount.get("target") != "/var/lib/netratel/clients"]
mcp["volumes"].append({"type": "bind", "source": str(directory / "outbound.json"),
    "target": "/run/secrets/mcp-outbound-config", "read_only": True})
document["volumes"].pop("http-client-data", None)
path.write_text(json.dumps(document, indent=2) + "\n")
outbound = {"instance": "dev", "apiBaseUrl": api,
    "authenticationMode": "ApiM2MClientSecret", "apiM2MTokenUrl": api + "/connect/token",
    "apiM2MClientId": "disposable-mcp-api", "apiM2MClientSecret": secrets.token_urlsafe(48),
    "apiM2MScope": "netratel.api"}
(directory / "outbound.json").write_text(json.dumps(outbound) + "\n")
# Preserve actual disposable secret values for failure-log redaction before the
# outbound file becomes readable only by the MCP identity. Never upload this file.
redactions = [outbound["apiM2MClientSecret"], environment["NetRatel__Mcp__Delegation__SharedKeyBase64"],
    services["api"]["environment"]["M2MClients__unrelated_caller__Secret"],
    "disposable-http-host-postgres-passphrase", "disposable HTTP host administrator passphrase"]
(directory / "redaction-values.json").write_text(json.dumps(redactions))
PY
chmod 600 "$temporary/compose.yaml"
outbound_hash="$(sha256sum "$temporary/outbound.json" | cut -d ' ' -f 1)"
generation_hashes="$(python3 - "$temporary" <<'PY'
import hashlib, json, pathlib, sys
directory = pathlib.Path(sys.argv[1]); document = json.loads((directory / "compose.yaml").read_text())
delegation = document["services"]["mcp-http"]["environment"]["NetRatel__Mcp__Delegation__SharedKeyBase64"]
outbound = json.loads((directory / "outbound.json").read_text())["apiM2MClientSecret"]
print(json.dumps({"delegation": hashlib.sha256(delegation.encode()).hexdigest(),
    "outbound": hashlib.sha256(outbound.encode()).hexdigest()}))
PY
)"
docker run --rm --volume "$temporary:/fixture" alpine:3.22 sh -ceu \
  'chown 10001:10001 /fixture/outbound.json; chmod 400 /fixture/outbound.json'
check_source_ownership() {
  [[ "$(stat -c '%u:%g:%a' "$temporary/compose.yaml")" == "$deployment_uid:$deployment_gid:600" ]]
}
check_source_ownership
original_compose_hash="$(sha256sum "$temporary/compose.yaml" | cut -d ' ' -f 1)"
mkdir -p TestResults/http-host-setup
stage="rejecting absent durable storage before approval without assuming a setup Compose file"
[[ ! -e "$temporary/compose.setup.yaml" ]]
for mode in host client; do
  arguments=(setup --non-interactive --instance "$NETRATEL_PUBLIC_ORIGIN")
  if [[ "$mode" == host ]]; then arguments+=(--mode host); fi
  if "${compose[@]}" run --rm --no-deps -T mcp-http "${arguments[@]}" \
      > "TestResults/http-host-setup/missing-${mode}-storage.txt" 2>&1; then
    echo "Setup unexpectedly accepted a disposable container without persistent ${mode} storage." >&2; exit 1
  fi
  grep -Eiq 'persistent|durable|storage|mount' "TestResults/http-host-setup/missing-${mode}-storage.txt"
done
"${compose[@]}" config --quiet
managed_env_hash="$(sha256sum "$temporary/managed.env" | cut -d ' ' -f 1)"
check_generation() {
  local expected actual kind path
  for kind in delegation outbound; do
    expected="$(jq -er --arg kind "$kind" '.[$kind]' <<<"$generation_hashes")"
    if [[ "$kind" == delegation ]]; then path=/run/netratel-http-host/runtime/delegation.key
    else path=/run/netratel-http-host/runtime/adopted-outbound-m2m.secret; fi
    actual="$("${applied[@]}" exec -T --user 10001 mcp-http cat "$path" \
      | python3 -c 'import hashlib,sys; print(hashlib.sha256(sys.stdin.buffer.read().rstrip(b"\r\n")).hexdigest())')"
    [[ "$actual" == "$expected" ]] || { echo "The existing $kind identity changed." >&2; return 1; }
  done
  [[ "$("${applied[@]}" exec -T --user 10001 mcp-http sha256sum /run/secrets/mcp-outbound-config | cut -d ' ' -f 1)" == "$outbound_hash" ]]
  [[ "$(sha256sum "$temporary/managed.env" | cut -d ' ' -f 1)" == "$managed_env_hash" ]]
  check_source_ownership
}
# Only the owner-facing launcher attaches missing setup/profile volumes. The
# original Compose file remains untouched until its complete export is reviewed.
run_host_setup() {
  local instance="$1" output="$2" status=0
  "${launcher[@]}" -- "${host_setup[@]}" --instance "$instance" > "$temporary/launcher.log" 2>&1 || status=$?
  if (( status != 0 && status != 2 )); then
    echo "The actual single-Compose launcher failed before generating a reviewed document (exit $status)." >&2
    return "$status"
  fi
  # The launcher adds a short human-readable introduction and export location.
  # Retain the executable's exact JSON receipt, with no launch-log fabrication.
  python3 - "$temporary/launcher.log" "$output" <<'PY'
import json, pathlib, sys
source = pathlib.Path(sys.argv[1]).read_text(); decoder = json.JSONDecoder()
for offset, character in enumerate(source):
    if character != "{": continue
    try: value, _ = decoder.raw_decode(source[offset:])
    except json.JSONDecodeError: continue
    if isinstance(value, dict) and "registrationFingerprint" in value:
        pathlib.Path(sys.argv[2]).write_text(json.dumps(value, indent=2) + "\n")
        break
else:
    raise SystemExit("The launcher did not return a host generation receipt; inspect its private log.")
PY
}
curl_args=(--fail --silent --show-error --connect-timeout 2 --max-time 15 \
  --cacert "$NETRATEL_HTTPS_CERTIFICATE" --noproxy "$NETRATEL_PUBLIC_HOST" \
  --resolve "${NETRATEL_PUBLIC_HOST}:${NETRATEL_HTTPS_PORT}:127.0.0.1")
post() {
  local path="$1" body="$2"
  shift 2
  curl "${curl_args[@]}" "$@" --header 'Content-Type: application/json' \
    --data "$body" "$NETRATEL_PUBLIC_ORIGIN$path"
}
account_post() {
  post "$1" "$2" --cookie "$temporary/cookies" --header 'X-NetRatel-Account-Request: 1'
}
poll_protocol() {
  local path="$1" request="$2" expected="$3" interval="$4" response status
  local deadline=$((SECONDS + 45))
  while (( SECONDS < deadline )); do
    [[ "$interval" =~ ^[0-9]+$ ]] && (( interval >= 1 && interval <= 30 )) || {
      echo "The approval protocol returned an invalid polling interval." >&2; return 1;
    }
    (( SECONDS + interval < deadline )) || break
    sleep "$interval"
    response="$(post "$path" "$request")"
    status="$(jq -er '.status' <<<"$response")"
    if [[ "$status" == "$expected" ]]; then printf '%s' "$response"; return; fi
    case "$status" in
      pending|slow_down) interval="$(jq -er '.intervalSeconds' <<<"$response")" ;;
      *) echo "The approval protocol ended without the expected approved state ($status)." >&2; return 1 ;;
    esac
  done
  echo "The approval protocol did not complete within 45 seconds." >&2
  return 1
}
wait_state() {
  local predicate="$1" deadline=$((SECONDS + 90))
  while (( SECONDS < deadline )); do
    if curl "${curl_args[@]}" "$NETRATEL_PUBLIC_ORIGIN/api/v2/setup/status" 2>/dev/null \
      | jq -e "$predicate" >/dev/null; then return; fi
    sleep 1
  done
  echo "The disposable API did not reach the expected bootstrap state within 90 seconds." >&2
  return 1
}
wait_ready() { wait_state '.isReady == true'; }
wait_mcp() {
  local deadline=$((SECONDS + 60))
  while (( SECONDS < deadline )); do
    if "${applied[@]}" exec -T mcp-http curl --fail --silent --max-time 2 \
      http://127.0.0.1:9224/health/live >/dev/null 2>&1; then return; fi
    sleep 1
  done
  echo "The configured disposable MCP host did not become ready within 60 seconds." >&2
  return 1
}

stage="starting the single-Compose Web/API HTTPS fixture with supplied but unprovisioned MCP settings"
"${compose[@]}" up --build --detach postgres volume-init migrations api web ingress
[[ -n "$("${compose[@]}" ps -q api)" ]]
wait_state '.setupRequired == true and .isReady == false'
printf '%s\n' 'disposable HTTP host administrator passphrase' | "${compose[@]}" exec -T api \
  sh -ceu 'umask 077; cat > /var/netratel/bootstrap/http-host-password'
"${compose[@]}" exec -T \
  -e Bootstrap__Unattended__PasswordFile=/var/netratel/bootstrap/http-host-password \
  -e Bootstrap__Unattended__DisplayName='HTTP host smoke administrator' \
  -e Bootstrap__Unattended__Email=http-host-smoke@example.test \
  -e Bootstrap__Unattended__TenantName='HTTP host smoke tenant' \
  api dotnet NetRatel.API.dll --initialize-unattended >/dev/null
"${compose[@]}" restart api web >/dev/null
wait_ready
post /api/v2/local-auth/login \
  '{"email":"http-host-smoke@example.test","password":"disposable HTTP host administrator passphrase","rememberMe":false}' \
  --cookie-jar "$temporary/cookies" >/dev/null
stage="diagnosing supplied configuration without mistaking it for completed backend provisioning"
doctor_status=0
"${compose[@]}" run --rm --no-deps -T mcp-http doctor \
  > TestResults/http-host-setup/before-doctor.json || doctor_status=$?
[[ "$doctor_status" == 2 ]]
jq -e 'type == "array" and any(.[]; .name == "configuration" and .status == "completed")
  and any(.[]; .name == "token acquisition" and .status == "unauthorized" and .httpStatus == 401)
  and all(.[]; .name != "service API authorization")' \
  TestResults/http-host-setup/before-doctor.json >/dev/null
stage="confirming discovery cannot establish the manually configured ingress mode"
capabilities="$(curl "${curl_args[@]}" "$NETRATEL_PUBLIC_ORIGIN/api/http-host-setup/v1/capabilities")"
jq -e --arg origin "$NETRATEL_PUBLIC_ORIGIN" \
  '.protocolVersion == 1 and .publicOrigin == $origin and .authenticationModeKnown == false' <<<"$capabilities" >/dev/null
stage="adopting canonical dev/outbound/delegation settings into the durable setup volume"
run_host_setup "$NETRATEL_PUBLIC_ORIGIN" TestResults/http-host-setup/generated.json
check_source_ownership
[[ "$(sha256sum "$temporary/compose.yaml" | cut -d ' ' -f 1)" == "$original_compose_hash" ]]
jq -e '.state == "configuration-written-apply-and-live-verification-pending"' TestResults/http-host-setup/generated.json >/dev/null
fingerprint="$(jq -er '.registrationFingerprint' TestResults/http-host-setup/generated.json)"
[[ "$fingerprint" =~ ^[[:xdigit:]]{64}$ ]]

stage="approving the origin-bound host generation with the disposable administrator"
host_request="$(post /api/http-host-setup/v1/start "$(jq -n --arg origin "$NETRATEL_PUBLIC_ORIGIN" --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" --arg fingerprint "$fingerprint" '{publicOrigin:$origin,resource:$resource,registrationFingerprint:$fingerprint,servicePrincipal:"disposable-mcp-api"}')")"
code_request="$(jq '{userCode}' <<<"$host_request")"
account_post /api/v2/account/integration-credentials/http-host-setup/lookup "$code_request" \
  | jq -e --arg fingerprint "$fingerprint" '.registrationFingerprint == $fingerprint' >/dev/null
account_post /api/v2/account/integration-credentials/http-host-setup/approve "$code_request" >/dev/null
poll_protocol /api/http-host-setup/v1/poll "$(jq '{deviceCapability}' <<<"$host_request")" approved "$(jq -er '.intervalSeconds' <<<"$host_request")" \
  | jq -e --arg fingerprint "$fingerprint" '.status == "approved" and .registrationFingerprint == $fingerprint' >/dev/null

stage="applying the complete generated Compose document as a Komodo editor redeployment"
jq -e 'any(.result.instructions[]; contains("Komodo"))' TestResults/http-host-setup/generated.json >/dev/null
reviewed_compose="$temporary/.netratel-http-mcp-$project/compose.komodo.yaml"
# The manager receives the complete document, not a transient command-line
# overlay that would vanish on its next deploy. No secret or YAML repair occurs.
test -s "$reviewed_compose"
[[ "$(stat -c %a "$reviewed_compose")" == 600 ]]
import_temporary="$(mktemp "$temporary/.compose-import.XXXXXX")"
cat "$reviewed_compose" > "$import_temporary"
mv "$import_temporary" "$temporary/compose.yaml"
check_source_ownership
applied_compose_hash="$(sha256sum "$temporary/compose.yaml" | cut -d ' ' -f 1)"
"${compose[@]}" config --quiet
"${applied[@]}" up --detach --no-deps api web mcp-http
wait_ready
wait_mcp
"${applied[@]}" exec -T --user 1654 api sh -ceu 'test -r /run/netratel-http-host/runtime/delegation.key; test -r /run/netratel-http-host/runtime/adopted-outbound-m2m.secret; test ! -r /run/netratel-http-host/adopted/delegation.key'
"${applied[@]}" exec -T --user 10001 mcp-http sh -ceu 'test -r /run/netratel-http-host/runtime/delegation.key; test -r /run/netratel-http-host/runtime/mcp-outbound.json; test "$(id -u)" = 10001'
check_generation
saved_request_hash="$("${applied[@]}" exec -T --user 10001 mcp-http sha256sum /setup-output/.http-host.setup.json | cut -d ' ' -f 1)"
"${applied[@]}" exec -T mcp-http sh -ceu \
  'test "$NETRATEL_MCP_INSTANCE" = dev; test "$NetRatel__Mcp__Delegation__ServicePrincipal" = disposable-mcp-api; test "$NetRatel__Mcp__Delegation__LifetimeSeconds" = 90; test "$NETRATEL_ACCEPTANCE_UNRELATED" = preserved'
"${applied[@]}" exec -T api sh -ceu 'test "$NETRATEL_ACCEPTANCE_UNRELATED" = preserved; test "$M2M__AllowedCallerClientIds__0" = unrelated-caller'
stage="verifying token issuance and service API authorization as separate diagnostic stages"
"${applied[@]}" exec -T mcp-http dotnet /app/NetRatel.Mcp.Http.dll doctor \
  > TestResults/http-host-setup/after-doctor.json
jq -e 'type == "array" and length >= 3 and all(.[]; .status == "completed")
  and any(.[]; .name == "token acquisition")
  and any(.[]; .name == "service API authorization" and .httpStatus == 200)' \
  TestResults/http-host-setup/after-doctor.json >/dev/null

stage="obtaining a separate temporary personal MCP verification credential"
pairing="$(post /api/integration-pairing/v1/start "$(jq -n --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" '{name:"HTTP host disposable verification",purpose:1,resource:$resource}')")"
expiry="$(date -u -d '+1 hour' +'%Y-%m-%dT%H:%M:%SZ')"
account_post /api/v2/account/integration-credentials/pairing/approve \
  "$(jq -n --arg code "$(jq -r '.userCode' <<<"$pairing")" --arg expiry "$expiry" '{userCode:$code,expiresAtUtc:$expiry,grants:[{tenantId:1,permission:"telemetry.read"}],instancePermissions:["mcp.discovery.read"]}')" >/dev/null
delivery="$(poll_protocol /api/integration-pairing/v1/poll "$(jq '{deviceCapability}' <<<"$pairing")" issued "$(jq -er '.intervalSeconds' <<<"$pairing")")"
credential="$(jq -er '.delivery.secret' <<<"$delivery")"
[[ "$credential" == nrt_ic_* ]]

mcp_session=""
verification_phase=initial
redact_mcp_response() {
  # Never retain response headers, bearer credentials, or signed assertions.
  # The disposable identity facts and tool schemas are useful failure evidence.
  python3 - "$temporary/mcp-response" "$1" "$temporary/mcp-credential" <<'PY'
import json, pathlib, re, sys
credential = pathlib.Path(sys.argv[3]).read_text().strip()
value = pathlib.Path(sys.argv[1]).read_text()
if credential:
    value = value.replace(credential, "[REDACTED]")
value = re.sub(r"nrt_ic_[A-Za-z0-9_.-]+", "[REDACTED]", value)
value = re.sub(r"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[REDACTED]", value)
try:
    document = json.loads(value)
except json.JSONDecodeError:
    document = {"nonJsonResponse": value}
def redact(item):
    if isinstance(item, dict):
        return {key: "[REDACTED]" if re.search(r"^(authorization|access_?token|refresh_?token|assertion|secret|deviceCapability)$", key, re.I)
                else redact(child) for key, child in item.items()}
    if isinstance(item, list):
        return [redact(child) for child in item]
    return item
pathlib.Path(sys.argv[2]).write_text(json.dumps(redact(document), indent=2) + "\n")
PY
}
umask 077
printf '%s' "$credential" > "$temporary/mcp-credential"
mcp_call() {
  local response payload status=0 headers=()
  if [[ -n "$mcp_session" ]]; then headers=(--header "Mcp-Session-Id: $mcp_session"); fi
  response="$(post /mcp "$1" --no-fail --fail-with-body --header 'Accept: application/json, text/event-stream' \
    --header 'MCP-Protocol-Version: 2025-03-26' --header "Authorization: Bearer $credential" \
    --dump-header "$temporary/mcp-headers" "${headers[@]}")" || status=$?
  payload="$(sed -n 's/^data: *//p' <<<"$response")"
  printf '%s' "${payload:-$response}" > "$temporary/mcp-response"
  redact_mcp_response "$2"
  (( status == 0 )) || return "$status"
  printf '%s' "${payload:-$response}"
}
verify_mcp() {
  mcp_session=""
  stage="verifying ${verification_phase} authenticated MCP initialize"
  mcp_call '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"http-host-setup-smoke","version":"1"}}}' \
    "TestResults/http-host-setup/${verification_phase}-initialize.json" \
    | jq -e '.id == 1 and .result.serverInfo.name == "NetRatel.Mcp.Http"' >/dev/null
  mcp_session="$(awk 'tolower($1) == "mcp-session-id:" {gsub(/\r/, "", $2); print $2}' "$temporary/mcp-headers")"
  stage="verifying ${verification_phase} MCP initialized notification"
  mcp_call '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    "TestResults/http-host-setup/${verification_phase}-initialized.json" >/dev/null
  stage="verifying ${verification_phase} authenticated MCP tool catalog"
  mcp_call '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
    "TestResults/http-host-setup/${verification_phase}-catalog.json" \
    | jq -e '.id == 2 and (.result.tools | length >= 30) and any(.result.tools[]; .name == "netratel_auth")' >/dev/null
  stage="verifying ${verification_phase} netratel_auth/status through signed API delegation"
  mcp_call '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"netratel_auth","arguments":{"operation":"status"}}}' \
    "TestResults/http-host-setup/${verification_phase}-auth-status.json" \
    | jq -e '.id == 3 and (.result.isError // false) == false and .result.structuredContent.success == true
      and .result.structuredContent.data.authenticated == true
      and .result.structuredContent.data.authMode == "mcp_operator_delegation"
      and (.result.structuredContent.data.groups | type == "array" and all(.[]; type == "string" and length > 0))
      and (.result.structuredContent.data.roles | type == "array" and all(.[]; type == "string" and length > 0))' >/dev/null
}
stage="verifying authenticated catalog and a real API-backed read"
verify_mcp

# Execute the copyable administrator guide itself. Only its four documented
# non-secret inputs differ; each execution signs in with its own private cookie
# jar and selects the actual resource-bound credential from the API response.
mkdir "$temporary/policy-curl"
printf 'resolve = "%s:443:127.0.0.1"\nnoproxy = "%s"\n' \
  "$NETRATEL_PUBLIC_API_HOST" "$NETRATEL_PUBLIC_API_HOST" > "$temporary/policy-curl/.curlrc"
run_documented_customer_policy() {
  python3 - docs/integrations/netclaw-http-mcp.md "$temporary/customer-policy-recipe.sh" \
    "$NETRATEL_PUBLIC_API_ORIGIN" "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" dev "$1" <<'PY'
import pathlib, re, shlex, sys
guide, target = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
text = guide.read_text()
begin, end = "<!-- BEGIN NETCLAW_HTTP_POLICY_RECIPE -->", "<!-- END NETCLAW_HTTP_POLICY_RECIPE -->"
if text.count(begin) != 1 or text.count(end) != 1:
    raise SystemExit("The canonical customer-policy recipe markers must each occur once.")
block = text.split(begin, 1)[1].split(end, 1)[0].strip()
if not block.startswith("~~~bash\n") or not block.endswith("\n~~~"):
    raise SystemExit("The canonical customer-policy recipe must remain one Bash fence.")
recipe = block[len("~~~bash\n"):-len("\n~~~")]
for name, value in zip(("API_URL", "MCP_URL", "INSTANCE", "CREDENTIAL_NAME"), sys.argv[3:], strict=True):
    recipe, count = re.subn(r"^" + name + r"='[^'\n]*'$", lambda _: name + "=" + shlex.quote(value), recipe, flags=re.M)
    if count != 1:
        raise SystemExit("The canonical recipe must contain exactly one editable " + name + " assignment.")
target.write_text(recipe + "\n")
PY
  printf '%s\n' http-host-smoke@example.test 'disposable HTTP host administrator passphrase' ALLOW \
    | CURL_HOME="$temporary/policy-curl" CURL_CA_BUNDLE="$NETRATEL_HTTPS_CERTIFICATE" \
      bash "$temporary/customer-policy-recipe.sh" \
        > "$temporary/customer-policy.json" 2> "$temporary/customer-policy-recipe.log"
}
declare -a customer_tenants=() customer_credentials=() customer_credential_ids=() customer_flow_ids=()
for customer in 1 2 3; do
  customer_name="customer${customer}-netclaw"
  stage="creating ${customer_name} tenant through the Web API gateway"
  tenant="$(account_post /api/v1/tenants/ "$(jq -n --arg name "$customer_name" '{name:$name}')")"
  tenant_id="$(jq -er '.tenantId | select(type == "number" and . > 0)' <<<"$tenant")"
  stage="creating ${customer_name} durable private flow"
  flow="$(account_post "/api/v1/tenants/${tenant_id}/flows" \
    "$(jq -n --arg name "${customer_name}-private-flow" '{name:$name,useTemplate:false}')")"
  flow_id="$(jq -er '.flowId // .id' <<<"$flow")"
  stage="requesting ${customer_name} HTTP MCP-purpose credential"
  customer_pairing="$(post /api/integration-pairing/v1/start \
    "$(jq -n --arg name "$customer_name" --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" '{name:$name,purpose:1,resource:$resource}')")"
  stage="approving ${customer_name} credential for its own tenant only"
  account_post /api/v2/account/integration-credentials/pairing/approve \
    "$(jq -n --arg code "$(jq -r '.userCode' <<<"$customer_pairing")" --arg expiry "$expiry" \
      --argjson tenant "$tenant_id" '{userCode:$code,expiresAtUtc:$expiry,grants:[{tenantId:$tenant,permission:"flow.read"}],instancePermissions:["mcp.discovery.read"]}')" >/dev/null
  stage="receiving ${customer_name} approved HTTP MCP-purpose credential"
  customer_delivery="$(poll_protocol /api/integration-pairing/v1/poll \
    "$(jq '{deviceCapability}' <<<"$customer_pairing")" issued "$(jq -er '.intervalSeconds' <<<"$customer_pairing")")"
  customer_credential_id="$(jq -er '.delivery.credentialId' <<<"$customer_delivery")"
  # Policy selects this credential's authenticated client ID. The administrator
  # retains no policy self-escalation; the three credentials share no authority.
  stage="running the documented administrator recipe for ${customer_name} flow-read policy"
  run_documented_customer_policy "$customer_name"
  jq -e '.version == 1' "$temporary/customer-policy.json" >/dev/null
  jq -n --arg name "$customer_name" --arg id "$customer_credential_id" --arg flow "$flow_id" \
    --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" --argjson tenant "$tenant_id" \
    --argjson policy "$(cat "$temporary/customer-policy.json")" \
    '{name:$name,credentialId:$id,purpose:"HttpMcp",resource:$resource,tenantId:$tenant,flowId:$flow,
      grants:[{tenantId:$tenant,permission:"flow.read"}],instancePermissions:["mcp.discovery.read"],policy:$policy}' \
    > "TestResults/http-host-setup/${customer_name}-identity.json"
  stage="acknowledging ${customer_name} credential delivery"
  post /api/integration-pairing/v1/acknowledge \
    "$(jq -n --arg capability "$(jq -r '.deviceCapability' <<<"$customer_pairing")" \
      --arg id "$customer_credential_id" '{deviceCapability:$capability,credentialId:$id}')" >/dev/null
  customer_tenants+=("$tenant_id")
  customer_credentials+=("$(jq -er '.delivery.secret' <<<"$customer_delivery")")
  customer_credential_ids+=("$customer_credential_id")
  customer_flow_ids+=("$flow_id")
  unset customer_pairing customer_delivery
done
[[ "${customer_credential_ids[0]}" != "${customer_credential_ids[1]}" && \
   "${customer_credential_ids[1]}" != "${customer_credential_ids[2]}" && \
   "${customer_credential_ids[0]}" != "${customer_credential_ids[2]}" ]]
verify_customer_connections() {
  local original_credential="$credential" customer target tenant_id request response
  for customer in 0 1 2; do
    credential="${customer_credentials[$customer]}"
    printf '%s' "$credential" > "$temporary/mcp-credential"
    verification_phase="${1}-customer$((customer + 1))"
    verify_mcp
    for target in 0 1 2; do
      tenant_id="${customer_tenants[$target]}"
      stage="verifying ${verification_phase} flow-read authorization for customer$((target + 1)) tenant"
      request="$(jq -n --argjson tenant "$tenant_id" '{jsonrpc:"2.0",id:4,method:"tools/call",
        params:{name:"netratel_flows",arguments:{operation:"list",request:{tenantId:$tenant}}}}')"
      response="$(mcp_call "$request" "TestResults/http-host-setup/${verification_phase}-tenant-$((target + 1)).json")"
      if (( customer == target )); then
        jq -e --arg flow "${customer_flow_ids[$customer]}" '.id == 4
          and (.result.isError // false) == false and .result.structuredContent.success == true
          and (.result.structuredContent.data | type == "array" and length == 1
            and (.[0].flowId // .[0].id) == $flow)' <<<"$response" >/dev/null
      else
        # A real permission rejection is required. Connectivity, protocol and
        # token failures are not evidence of a cross-customer authorization deny.
        jq -e '.id == 4 and (
          ((.error.message? // "") | contains("not authorized for this operation")) or
          (.result.isError == true and any(.result.content[]?;
            (.text // "") | contains("not authorized for this operation"))))' <<<"$response" >/dev/null
      fi
    done
  done
  credential="$original_credential"
  printf '%s' "$credential" > "$temporary/mcp-credential"
}
stage="verifying three protocol clients permit their own tenant read and reject all six cross-tenant reads"
verify_customer_connections initial

stage="saving a personal client with bare HTTP setup inside the serving container"
printf '%s\n' "$credential" | "${applied[@]}" exec -T \
  -e NETRATEL_HTTP_HOST_SMOKE_ORIGIN="$NETRATEL_PUBLIC_ORIGIN" mcp-http sh -ceu '
    umask 077
    credential_file="$(mktemp)"
    result_file="$(mktemp)"
    trap '\''rm -f "$credential_file" "$result_file"'\'' EXIT
    cat > "$credential_file"
    setup_status=0
    dotnet /app/NetRatel.Mcp.Http.dll setup --non-interactive \
      --instance "$NETRATEL_HTTP_HOST_SMOKE_ORIGIN" --credential-file "$credential_file" > "$result_file" || setup_status=$?
    if grep -F -q -f "$credential_file" "$result_file"; then
      echo "Personal setup emitted credential material." >&2; exit 1
    else
      test "$?" = 1
    fi
    if [ "$setup_status" -ne 0 ]; then cat "$result_file"; exit "$setup_status"; fi
    profile="$NETRATEL_CLIENT_DIRECTORY/profiles/httpmcpclient/default.json"
    test -f "$profile"
    test "$(stat -c %a "$profile")" = 600
    for secret in "$profile".*.secret; do
      test -f "$secret"
      test "$(stat -c %a "$secret")" = 600
    done
    cat "$result_file"
  ' > TestResults/http-host-setup/personal-saved.json
jq -e '.saved == true and .transport == "HttpMcpClient"
  and .path == "/var/lib/netratel/clients/profiles/httpmcpclient/default.json"
  and (.checks | length >= 5 and all(.[]; .status == "completed"))
  and (.launch | fromjson | .credentialFile | startswith("/var/lib/netratel/clients/profiles/httpmcpclient/"))' \
  TestResults/http-host-setup/personal-saved.json >/dev/null
personal_credential_file="$(jq -er '.launch | fromjson | .credentialFile' TestResults/http-host-setup/personal-saved.json)"

stage="recreating API/Web/MCP from the persisted single Compose document"
"${applied[@]}" up --detach --force-recreate --no-deps api web mcp-http
wait_ready
wait_mcp
check_generation
verification_phase=recreated
verify_mcp
stage="verifying customer grants remain isolated after complete API/Web/MCP recreation"
verify_customer_connections recreated
stage="resuming the saved server setup through the actual launcher verification command"
"${launcher[@]}" --verify -- --non-interactive --instance "$NETRATEL_PUBLIC_ORIGIN/mcp/" \
  --credential-file "$personal_credential_file" > "$temporary/verified-launcher.log" 2>&1
python3 - "$temporary/verified-launcher.log" TestResults/http-host-setup/verified.json <<'PY'
import json, pathlib, sys
source = pathlib.Path(sys.argv[1]).read_text(); decoder = json.JSONDecoder()
for offset, character in enumerate(source):
    if character != "{": continue
    try: value, _ = decoder.raw_decode(source[offset:])
    except json.JSONDecodeError: continue
    if isinstance(value, dict) and value.get("state") in {"end-to-end-verified", "verification-incomplete"}:
        pathlib.Path(sys.argv[2]).write_text(json.dumps(value, indent=2) + "\n")
        break
else:
    raise SystemExit("The launcher did not return a server verification receipt; inspect its private log.")
PY
jq -e --arg origin "$NETRATEL_PUBLIC_ORIGIN" --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" \
  '.state == "end-to-end-verified" and .instance == $origin and .target == $resource
  and (.checks | length >= 5 and all(.[]; .status == "completed"))' \
  TestResults/http-host-setup/verified.json >/dev/null
[[ "$("${applied[@]}" exec -T --user 10001 mcp-http sha256sum /setup-output/.http-host.setup.json | cut -d ' ' -f 1)" == "$saved_request_hash" ]]
[[ "$(sha256sum "$temporary/compose.yaml" | cut -d ' ' -f 1)" == "$applied_compose_hash" ]]
check_generation
stage="issuing a distinct bounded read-only verifier with explicit consent and zero tenant access"
bounded_pairing="$(post /api/integration-pairing/v1/start "$(jq -n --arg resource "$NETRATEL_MCP_PUBLIC_RESOURCE_URI" \
  '{name:"Fresh saved-generation read-only verification",purpose:1,resource:$resource,temporaryVerifier:true,requestedPermissions:["mcp.discovery.read"]}')")"
account_post /api/v2/account/integration-credentials/pairing/lookup "$(jq '{userCode}' <<< "$bounded_pairing")" \
  | jq -e '.temporaryVerifier == true and .requestedPermissions == ["mcp.discovery.read"]' >/dev/null
# Deliberately request an ordinary long expiry; the server must enforce its
# verifier bound independently of the caller-supplied requested expiration.
ordinary_expiry="$(date -u -d '+30 days' +'%Y-%m-%dT%H:%M:%SZ')"
account_post /api/v2/account/integration-credentials/pairing/approve \
  "$(jq -n --arg code "$(jq -r '.userCode' <<< "$bounded_pairing")" --arg expiry "$ordinary_expiry" \
    '{userCode:$code,expiresAtUtc:$expiry,grants:[],instancePermissions:["mcp.discovery.read"],verifierConsent:true}')" >/dev/null
bounded_delivery="$(poll_protocol /api/integration-pairing/v1/poll "$(jq '{deviceCapability}' <<< "$bounded_pairing")" issued "$(jq -er '.intervalSeconds' <<< "$bounded_pairing")")"
jq -e '.delivery.grants == [] and .delivery.instancePermissions == ["mcp.discovery.read"]' <<< "$bounded_delivery" >/dev/null
jq '.delivery | del(.secret)' <<< "$bounded_delivery" > TestResults/http-host-setup/bounded-verifier.json
python3 - TestResults/http-host-setup/bounded-verifier.json <<'PY_BOUND'
import datetime, json, pathlib, sys
receipt = json.loads(pathlib.Path(sys.argv[1]).read_text())
expiry = datetime.datetime.fromisoformat(receipt["expiresAtUtc"].replace("Z", "+00:00"))
now = datetime.datetime.now(datetime.timezone.utc)
assert now < expiry <= now + datetime.timedelta(minutes=15), "Verifier lifetime exceeded its server bound"
PY_BOUND
bounded_credential_file=/var/lib/netratel/clients/.bounded-host-verifier
jq -r '.delivery.secret' <<< "$bounded_delivery" | "${applied[@]}" exec -T --user 10001 mcp-http \
  sh -ceu 'umask 077; cat > /var/lib/netratel/clients/.bounded-host-verifier; chmod 600 /var/lib/netratel/clients/.bounded-host-verifier'
stage="fresh unchanged-source launcher verification with the bounded verifier"
"${launcher[@]}" --verify -- --non-interactive --instance "$NETRATEL_PUBLIC_ORIGIN" \
  --credential-file "$bounded_credential_file" > "$temporary/fresh-verified-launcher.log" 2>&1
python3 - "$temporary/fresh-verified-launcher.log" <<'PY_FRESH'
import json, pathlib, sys
text = pathlib.Path(sys.argv[1]).read_text(); decoder = json.JSONDecoder()
for offset, character in enumerate(text):
    if character != "{": continue
    try: receipt, _ = decoder.raw_decode(text[offset:])
    except json.JSONDecodeError: continue
    if isinstance(receipt, dict) and receipt.get("state") == "end-to-end-verified":
        checks = receipt.get("checks", [])
        if checks and all(check.get("status") == "completed" for check in checks) and any(check.get("name") == "permitted read-only tool" for check in checks): break
else: raise SystemExit("Fresh launcher verification did not complete a permitted API-backed read")
PY_FRESH
[[ "$("${applied[@]}" exec -T --user 10001 mcp-http sha256sum /setup-output/.http-host.setup.json | cut -d ' ' -f 1)" == "$saved_request_hash" ]]
stage="revoking the bounded verifier without affecting host or ordinary customer credentials"
bounded_id="$(jq -er '.delivery.credentialId' <<< "$bounded_delivery")"
post /api/integration-pairing/v1/cancel "$(jq '{deviceCapability}' <<< "$bounded_pairing")" >/dev/null
curl "${curl_args[@]}" --cookie "$temporary/cookies" "$NETRATEL_PUBLIC_ORIGIN/api/v2/account/integration-credentials/" \
  | jq -e --arg id "$bounded_id" 'any(.[]; .id == $id and .revokedAtUtc != null)' >/dev/null
"${applied[@]}" exec -T --user 10001 mcp-http rm -f "$bounded_credential_file"
unset bounded_pairing bounded_delivery bounded_id
stage="rejecting a concurrent saved-source edit before verification consent or completion"
cp "$temporary/compose.yaml" "$temporary/compose.unchanged-backup"
printf '\n# synthetic concurrent owner edit\n' >> "$temporary/compose.yaml"
changed_status=0
"${launcher[@]}" --verify -- --non-interactive --instance "$NETRATEL_PUBLIC_ORIGIN" \
  --credential-file "$personal_credential_file" > "$temporary/changed-source-launcher.log" 2>&1 || changed_status=$?
[[ "$changed_status" != 0 ]]
rg -q 'approved sources changed' "$temporary/changed-source-launcher.log"
if rg -q 'end-to-end-verified|Server verification completed' "$temporary/changed-source-launcher.log"; then
  echo 'Changed-source verification incorrectly claimed completion.' >&2; exit 1
fi
mv "$temporary/compose.unchanged-backup" "$temporary/compose.yaml"
check_source_ownership
[[ "$(sha256sum "$temporary/compose.yaml" | cut -d ' ' -f 1)" == "$applied_compose_hash" ]]
stage="reloading the personal default profile after serving-container recreation"
"${applied[@]}" exec -T mcp-http dotnet /app/NetRatel.Mcp.Http.dll doctor \
  --transport http --profile default > TestResults/http-host-setup/personal-reloaded.json
jq -e 'type == "array" and length >= 5 and all(.[]; .status == "completed")
  and (map(.name) | index("permitted read-only tool") != null)' \
  TestResults/http-host-setup/personal-reloaded.json >/dev/null
post /api/integration-pairing/v1/cancel "$(jq '{deviceCapability}' <<<"$pairing")" >/dev/null
unset credential delivery pairing host_request customer_credentials
jq -n --arg fingerprint "$fingerprint" --arg source "$(git rev-parse HEAD)" \
  '{sourceSha:$source,registrationFingerprint:$fingerprint,headlessGeneration:true,administratorProtocolApproval:true,
    komodoSingleCompose:true,privateHostOwnedSourcePreserved:true,missingStoragePreflight:true,existingReadOnlyIdentityAdopted:true,managedEnvironmentFilesRetained:true,splitWebApiOrigins:true,instance:"dev",
    initialTokenIssuanceRejectionDiagnosed:true,
    generatedApply:true,authenticatedCatalogAndApiRead:true,recreatedAndResumed:true,launcherVerificationCompleted:true,freshUnchangedSourceLauncherVerification:true,boundedVerifierExplicitConsent:true,boundedVerifierTenantGrants:0,boundedVerifierInstancePermissions:["mcp.discovery.read"],boundedVerifierRevocationConfirmed:true,changedSourceVerificationBlocked:true,personalProfileSavedAndReloaded:true,
    documentedCustomerPolicyRecipeExecuted:true,
    customerProtocolAcceptance:{connectionCount:3,ownTenantReads:6,crossTenantDenials:12,persistedAfterRecreation:true},
    netClawRuntimeAcceptance:"not executed; independent HTTP MCP protocol clients used",
    interactiveTerminalAcceptance:"covered separately by focused terminal fixtures",ownerDeploymentAcceptance:"pending"}' \
  > TestResults/http-host-setup/result.json
echo 'Disposable single-Compose host adoption, approval/apply/resume, durable client save and three-customer HTTP MCP isolation passed.'
