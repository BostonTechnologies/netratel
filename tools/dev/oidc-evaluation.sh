#!/usr/bin/env bash
# Starts a disposable, browser-login evaluation stack outside the source checkout.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
command="${1:-}"
requested_workspace="${2:-$(dirname "$root")/netratel-oidc-evaluation}"

usage() { echo "Usage: $0 start [workspace] | stop [workspace]" >&2; exit 2; }
die() { echo "OIDC evaluation: $*" >&2; exit 1; }
[[ "$command" == start || "$command" == stop ]] || usage

if [[ "$command" == start ]]; then mkdir -p "$requested_workspace"; fi
workspace="$(cd "$requested_workspace" 2>/dev/null && pwd)" || die "workspace does not exist: $requested_workspace"
state_file="$workspace/state.env"
workspace_hash() {
  if command -v sha256sum >/dev/null 2>&1; then
    printf '%s' "$workspace" | sha256sum | awk '{print $1}'
  else
    printf '%s' "$workspace" | shasum -a 256 | awk '{print $1}'
  fi
}

write_state_line() {
  local key="$1" value="$2"
  [[ "$value" != *$'\n'* && "$value" != *$'\r'* ]] || die "state values cannot contain newlines"
  value="${value//\\/\\\\}"; value="${value//\"/\\\"}"; value="${value//\$/\\\$}"
  printf '%s="%s"\n' "$key" "$value"
}
write_state() {
  local temporary="$state_file.tmp.$$"
  umask 077
  {
    write_state_line NETRATEL_EVALUATION_PROJECT "$NETRATEL_EVALUATION_PROJECT"
    write_state_line NETRATEL_EVALUATION_ROOT "$root"
    write_state_line NETRATEL_EVALUATION_WORKSPACE "$workspace"
    write_state_line NETRATEL_EVALUATION_DOCKER_CONTEXT "$NETRATEL_EVALUATION_DOCKER_CONTEXT"
    write_state_line NETRATEL_EVALUATION_DOCKER_HOST "$NETRATEL_EVALUATION_DOCKER_HOST"
    write_state_line NETRATEL_AGENT_AUTH_PRIVATE_KEY "$NETRATEL_AGENT_AUTH_PRIVATE_KEY"
    write_state_line NETRATEL_SMOKE_TLS_CERT_PATH "$NETRATEL_SMOKE_TLS_CERT_PATH"
    write_state_line NETRATEL_SMOKE_TLS_CERTIFICATE_PATH "$NETRATEL_SMOKE_TLS_CERTIFICATE_PATH"
    write_state_line NETRATEL_SMOKE_TLS_KEY_PATH "$NETRATEL_SMOKE_TLS_KEY_PATH"
    write_state_line NETRATEL_SMOKE_TLS_CERT_PASSWORD "$NETRATEL_SMOKE_TLS_CERT_PASSWORD"
    write_state_line NETRATEL_GATEWAY_PROXY_CONFIG_PATH "$NETRATEL_GATEWAY_PROXY_CONFIG_PATH"
    write_state_line NETRATEL_WEB_PROXY_CONFIG_PATH "$NETRATEL_WEB_PROXY_CONFIG_PATH"
    write_state_line NETRATEL_WEB_BIND_ADDRESS "$NETRATEL_WEB_BIND_ADDRESS"
    write_state_line NETRATEL_WEB_PORT "$NETRATEL_WEB_PORT"
    write_state_line NETRATEL_OIDC_TEST_BIND_ADDRESS "$NETRATEL_OIDC_TEST_BIND_ADDRESS"
    write_state_line NETRATEL_OIDC_TEST_PORT "$NETRATEL_OIDC_TEST_PORT"
    write_state_line NETRATEL_API_TEST_PORT "$NETRATEL_API_TEST_PORT"
    write_state_line NETRATEL_SMOKE_WEB_PROXY_SUBNET "$NETRATEL_SMOKE_WEB_PROXY_SUBNET"
    write_state_line NETRATEL_SMOKE_WEB_PROXY_ADDRESS "$NETRATEL_SMOKE_WEB_PROXY_ADDRESS"
    write_state_line POSTGRES_PASSWORD "$POSTGRES_PASSWORD"
    write_state_line OIDC_AUTHORITY "$OIDC_AUTHORITY"; write_state_line OIDC_CLIENT_ID "$OIDC_CLIENT_ID"
    write_state_line OIDC_API_SCOPE "$OIDC_API_SCOPE"; write_state_line OIDC_API_AUDIENCE "$OIDC_API_AUDIENCE"
    write_state_line OIDC_TOKEN_ENDPOINT "$OIDC_TOKEN_ENDPOINT"; write_state_line OIDC_ADMIN_GROUP_ID "$OIDC_ADMIN_GROUP_ID"
    write_state_line OIDC_CLIENT_SECRET "$OIDC_CLIENT_SECRET"
  } > "$temporary"
  chmod 600 "$temporary"; mv -f "$temporary" "$state_file"
}
load_state() {
  [[ -f "$state_file" ]] || die "no evaluation state found at $workspace"
  # This private, launcher-owned state is exported for the same Compose inputs in a fresh stop shell.
  set -a; source "$state_file"; set +a # shellcheck disable=SC1090
  [[ "$NETRATEL_EVALUATION_ROOT" == "$root" && "$NETRATEL_EVALUATION_WORKSPACE" == "$workspace" ]] || die "state belongs to a different checkout or workspace"
  [[ "$NETRATEL_EVALUATION_PROJECT" =~ ^netratel-oidc-evaluation-[a-f0-9]{12}$ ]] || die "invalid project identity"
  local required=(NETRATEL_AGENT_AUTH_PRIVATE_KEY NETRATEL_SMOKE_TLS_CERT_PATH NETRATEL_SMOKE_TLS_CERTIFICATE_PATH NETRATEL_SMOKE_TLS_KEY_PATH NETRATEL_GATEWAY_PROXY_CONFIG_PATH NETRATEL_WEB_PROXY_CONFIG_PATH NETRATEL_WEB_PORT NETRATEL_OIDC_TEST_PORT NETRATEL_API_TEST_PORT NETRATEL_SMOKE_WEB_PROXY_SUBNET NETRATEL_SMOKE_WEB_PROXY_ADDRESS POSTGRES_PASSWORD OIDC_AUTHORITY OIDC_CLIENT_ID OIDC_API_SCOPE OIDC_API_AUDIENCE OIDC_TOKEN_ENDPOINT OIDC_ADMIN_GROUP_ID OIDC_CLIENT_SECRET)
  local value; for value in "${required[@]}"; do [[ -n "${!value:-}" ]] || die "state is missing $value"; done
}
compose() {
  local -a environment=(env -i "PATH=$PATH" "HOME=${HOME:-/tmp}" "DOCKER_CONTEXT=$NETRATEL_EVALUATION_DOCKER_CONTEXT")
  [[ -n "$NETRATEL_EVALUATION_DOCKER_HOST" ]] && environment+=("DOCKER_HOST=$NETRATEL_EVALUATION_DOCKER_HOST")
  "${environment[@]}" docker compose --env-file "$state_file" --project-name "$NETRATEL_EVALUATION_PROJECT" -f "$root/compose.yaml" -f "$root/tests/compose/oidc-smoke.compose.yaml" "$@"
}

if [[ "$command" == stop ]]; then
  load_state; compose config --quiet; compose down --volumes --remove-orphans
  echo "Stopped disposable OIDC evaluation project $NETRATEL_EVALUATION_PROJECT. Keys remain in $workspace until you remove it."
  exit 0
fi

chmod 700 "$workspace"
if [[ -f "$state_file" ]]; then
  load_state
else
  hash="$(workspace_hash)"; base_port=$((20000 + 16#${hash:0:4} % 20000))
  NETRATEL_EVALUATION_PROJECT="netratel-oidc-evaluation-${hash:0:12}"
  NETRATEL_EVALUATION_ROOT="$root"; NETRATEL_EVALUATION_WORKSPACE="$workspace"
  NETRATEL_EVALUATION_DOCKER_CONTEXT="${DOCKER_CONTEXT:-default}"; NETRATEL_EVALUATION_DOCKER_HOST="${DOCKER_HOST:-}"
  NETRATEL_AGENT_AUTH_PRIVATE_KEY="$workspace/agent-es256-private.pem"; NETRATEL_SMOKE_TLS_CERT_PATH="$workspace/tls.pfx"
  NETRATEL_SMOKE_TLS_CERTIFICATE_PATH="$workspace/tls.crt"; NETRATEL_SMOKE_TLS_KEY_PATH="$workspace/tls.key"
  NETRATEL_SMOKE_TLS_CERT_PASSWORD="netratel-compose-only-password"
  NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"; NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
  NETRATEL_WEB_BIND_ADDRESS="127.0.0.1"; NETRATEL_WEB_PORT="${NETRATEL_WEB_PORT:-$base_port}"
  # Keep host-gateway issuer access for containers; this is evaluation-only, not an IdP deployment surface.
  NETRATEL_OIDC_TEST_BIND_ADDRESS="${NETRATEL_OIDC_TEST_BIND_ADDRESS:-0.0.0.0}"; NETRATEL_OIDC_TEST_PORT="${NETRATEL_OIDC_TEST_PORT:-$((base_port + 1))}"
  NETRATEL_API_TEST_PORT="${NETRATEL_API_TEST_PORT:-$((base_port + 2))}"
  NETRATEL_SMOKE_WEB_PROXY_SUBNET="172.$((16 + 16#${hash:4:2} % 16)).$((16#${hash:6:2})).0/24"; NETRATEL_SMOKE_WEB_PROXY_ADDRESS="172.$((16 + 16#${hash:4:2} % 16)).$((16#${hash:6:2})).10"
  POSTGRES_PASSWORD="netratel-local-evaluation-postgres"; OIDC_AUTHORITY="https://unused.example.invalid"; OIDC_CLIENT_ID="unused-local-evaluation-client"; OIDC_API_SCOPE="unused-local-evaluation-scope"; OIDC_API_AUDIENCE="unused-local-evaluation-audience"; OIDC_TOKEN_ENDPOINT="https://unused.example.invalid/token"; OIDC_ADMIN_GROUP_ID="unused-local-evaluation-group"; OIDC_CLIENT_SECRET="unused-local-evaluation-secret"
  openssl ecparam -name prime256v1 -genkey -noout -out "$NETRATEL_AGENT_AUTH_PRIVATE_KEY"
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=netratel-local-evaluation' -keyout "$NETRATEL_SMOKE_TLS_KEY_PATH" -out "$NETRATEL_SMOKE_TLS_CERTIFICATE_PATH" >/dev/null 2>&1
  openssl pkcs12 -export -out "$NETRATEL_SMOKE_TLS_CERT_PATH" -inkey "$NETRATEL_SMOKE_TLS_KEY_PATH" -in "$NETRATEL_SMOKE_TLS_CERTIFICATE_PATH" -passout "pass:$NETRATEL_SMOKE_TLS_CERT_PASSWORD" >/dev/null 2>&1
  chmod 644 "$NETRATEL_AGENT_AUTH_PRIVATE_KEY" "$NETRATEL_SMOKE_TLS_KEY_PATH" "$NETRATEL_SMOKE_TLS_CERTIFICATE_PATH" "$NETRATEL_SMOKE_TLS_CERT_PATH"
  write_state
fi
compose config --quiet; compose up --build --detach
cat <<EOF
Evaluation stack started as $NETRATEL_EVALUATION_PROJECT.

Open http://127.0.0.1:${NETRATEL_WEB_PORT}/auth/oidc and sign in as netratel-test-operator.
The test OIDC listener is intentionally reachable from containers through the host gateway and may bind beyond loopback; use it only on a trusted evaluation host.
When finished, run:
  $0 stop $workspace
EOF
