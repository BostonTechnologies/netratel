#!/usr/bin/env bash
# Starts a disposable, browser-login evaluation stack outside the source checkout.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
command="${1:-}"
workspace="${2:-$(dirname "$root")/netratel-oidc-evaluation}"
state_file="$workspace/state.env"

usage() {
  echo "Usage: $0 start [workspace] | stop [workspace]" >&2
  exit 2
}

[[ "$command" == start || "$command" == stop ]] || usage

if [[ "$command" == stop ]]; then
  [[ -f "$state_file" ]] || { echo "No evaluation state found at $workspace." >&2; exit 1; }
  # shellcheck disable=SC1090
  source "$state_file"
  docker compose --project-name "$NETRATEL_EVALUATION_PROJECT" \
    -f "$root/compose.yaml" -f "$root/tests/compose/oidc-smoke.compose.yaml" \
    down --volumes --remove-orphans
  echo "Stopped the disposable OIDC evaluation stack. Keys remain in $workspace until you remove it."
  exit 0
fi

mkdir -p "$workspace"
chmod 700 "$workspace"

if [[ -f "$state_file" ]]; then
  # shellcheck disable=SC1090
  source "$state_file"
else
  project="netratel-oidc-evaluation"
  agent_key="$workspace/agent-es256-private.pem"
  tls_key="$workspace/tls.key"
  tls_certificate="$workspace/tls.crt"
  tls_bundle="$workspace/tls.pfx"
  openssl ecparam -name prime256v1 -genkey -noout -out "$agent_key"
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=netratel-local-evaluation' \
    -keyout "$tls_key" -out "$tls_certificate" >/dev/null 2>&1
  openssl pkcs12 -export -out "$tls_bundle" -inkey "$tls_key" -in "$tls_certificate" \
    -passout pass:netratel-compose-only-password >/dev/null 2>&1
  # The disposable containers run as an unprivileged UID and need read-only mounts.
  chmod 644 "$agent_key" "$tls_key" "$tls_certificate" "$tls_bundle"
  {
    printf 'NETRATEL_EVALUATION_PROJECT=%q\n' "$project"
    printf 'NETRATEL_AGENT_AUTH_PRIVATE_KEY=%q\n' "$agent_key"
    printf 'NETRATEL_SMOKE_TLS_CERT_PATH=%q\n' "$tls_bundle"
    printf 'NETRATEL_SMOKE_TLS_CERTIFICATE_PATH=%q\n' "$tls_certificate"
    printf 'NETRATEL_SMOKE_TLS_KEY_PATH=%q\n' "$tls_key"
  } > "$state_file"
fi

# shellcheck disable=SC1090
source "$state_file"
export NETRATEL_EVALUATION_PROJECT NETRATEL_AGENT_AUTH_PRIVATE_KEY
export NETRATEL_SMOKE_TLS_CERT_PATH NETRATEL_SMOKE_TLS_CERTIFICATE_PATH NETRATEL_SMOKE_TLS_KEY_PATH
export NETRATEL_SMOKE_TLS_CERT_PASSWORD=netratel-compose-only-password
export NETRATEL_GATEWAY_PROXY_CONFIG_PATH="$root/tests/compose/gateway-proxy.nginx.conf"
export NETRATEL_WEB_PROXY_CONFIG_PATH="$root/tests/compose/web-proxy.nginx.conf"
export NETRATEL_WEB_BIND_ADDRESS=127.0.0.1 NETRATEL_WEB_PORT="${NETRATEL_WEB_PORT:-18080}"
export NETRATEL_OIDC_TEST_PORT="${NETRATEL_OIDC_TEST_PORT:-18081}"
export NETRATEL_API_TEST_PORT="${NETRATEL_API_TEST_PORT:-19222}"
export POSTGRES_PASSWORD=netratel-local-evaluation-postgres
export OIDC_AUTHORITY=https://unused.example.invalid
export OIDC_CLIENT_ID=unused-local-evaluation-client
export OIDC_API_SCOPE=unused-local-evaluation-scope
export OIDC_API_AUDIENCE=unused-local-evaluation-audience
export OIDC_TOKEN_ENDPOINT=https://unused.example.invalid/token
export OIDC_ADMIN_GROUP_ID=unused-local-evaluation-group
export OIDC_CLIENT_SECRET=unused-local-evaluation-secret

docker compose --project-name "$NETRATEL_EVALUATION_PROJECT" \
  -f "$root/compose.yaml" -f "$root/tests/compose/oidc-smoke.compose.yaml" config --quiet
docker compose --project-name "$NETRATEL_EVALUATION_PROJECT" \
  -f "$root/compose.yaml" -f "$root/tests/compose/oidc-smoke.compose.yaml" up --build --detach

cat <<EOF
Evaluation stack started.

Open http://127.0.0.1:${NETRATEL_WEB_PORT}/auth/oidc and sign in as netratel-test-operator.
On Linux where the browser does not resolve host.docker.internal, add the temporary
mapping '127.0.0.1 host.docker.internal' before opening the URL. Log out from the
application when finished, then run:
  $0 stop $workspace
EOF
