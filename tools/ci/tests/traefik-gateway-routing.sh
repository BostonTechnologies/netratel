#!/usr/bin/env bash
set -euo pipefail

native_only=0
if (( $# )); then
  [[ $# == 1 && "$1" == --native-only ]] || { echo 'Usage: traefik-gateway-routing.sh [--native-only]' >&2; exit 2; }
  [[ -n "${NETRATEL_GATEWAY_TEST_ASSEMBLY:-}" ]] || { echo '--native-only requires NETRATEL_GATEWAY_TEST_ASSEMBLY.' >&2; exit 2; }
  native_only=1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
network="netratel-traefik-routing-${GITHUB_RUN_ID:-local}-${RANDOM}"
fixture="${network}-fixture"
proxy="${network}-proxy"
streaming_proxy="${network}-streaming-proxy"
temporary_directory="$(mktemp -d)"
ca_certificate="$temporary_directory/ca.crt"
ca_key="$temporary_directory/ca.key"
certificate="$temporary_directory/tls.crt"
certificate_key="$temporary_directory/tls.key"
extensions="$temporary_directory/tls.ext"
network_created=0
created_container_ids=()
stage="initializing fixture"
proxy_runtime_arguments=()
if [[ -n "${NETRATEL_GATEWAY_TEST_ASSEMBLY:-}" ]]; then
  [[ -f "$NETRATEL_GATEWAY_TEST_ASSEMBLY" ]] || { echo 'The native gateway test assembly does not exist.' >&2; exit 2; }
  command -v dotnet >/dev/null 2>&1 || { echo 'dotnet is required for the native gateway probe.' >&2; exit 2; }
  proxy_runtime_arguments+=(--add-host host.docker.internal:host-gateway --publish 127.0.0.1::443)
fi

cleanup() {
  local status=$?
  if (( status != 0 )); then
    echo "Traefik fixture failed while $stage." >&2
    docker logs "$fixture" >&2 2>/dev/null || true
    docker logs "$proxy" >&2 2>/dev/null || true
    docker logs "$streaming_proxy" >&2 2>/dev/null || true
  fi
  for container_id in "${created_container_ids[@]}"; do
    docker rm -f "$container_id" >/dev/null 2>&1 || true
  done
  if (( network_created )); then
    docker network rm "$network" >/dev/null 2>&1 || true
  fi
  rm -rf "$temporary_directory"
  return "$status"
}
trap cleanup EXIT

command -v docker >/dev/null 2>&1 || { echo 'Docker is required for the Traefik routing regression.' >&2; exit 2; }
command -v openssl >/dev/null 2>&1 || { echo 'OpenSSL is required for the Traefik routing regression.' >&2; exit 2; }

openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$ca_key" >/dev/null 2>&1
openssl req -x509 -new -key "$ca_key" -sha256 -days 2 \
  -subj '/CN=NetRatel disposable Traefik fixture CA' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -out "$ca_certificate" >/dev/null 2>&1
openssl req -new -newkey rsa:2048 -nodes -keyout "$certificate_key" \
  -out "$temporary_directory/tls.csr" -subj '/CN=netratel.example.test' >/dev/null 2>&1
cat > "$extensions" <<'EOF'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:netratel.example.test,IP:127.0.0.1
EOF
openssl x509 -req -in "$temporary_directory/tls.csr" -CA "$ca_certificate" -CAkey "$ca_key" \
  -CAcreateserial -days 2 -sha256 -extfile "$extensions" \
  -out "$certificate" >/dev/null 2>&1
chmod 644 "$ca_certificate" "$certificate"

docker network create "$network" >/dev/null
network_created=1
created_container_ids+=("$(docker run --detach --name "$fixture" --network "$network" \
  --network-alias gateway-fixture \
  --volume "$root/tools/ci/tests/traefik-upstream.js:/app/traefik-upstream.js:ro" \
  --volume "$ca_certificate:/run/traefik/ca.crt:ro" \
  node:22-alpine node /app/traefik-upstream.js server)")
start_proxy() {
  local name="$1" alias="$2"
  shift 2
  created_container_ids+=("$(docker run --detach --name "$name" --network "$network" \
    --network-alias "$alias" \
    "${proxy_runtime_arguments[@]}" \
    --env "NO_PROXY=${NO_PROXY:+$NO_PROXY,}gateway-fixture,host.docker.internal,netratel.example.test,gateway-streaming,127.0.0.1,localhost" \
    --volume "$root/tools/ci/tests/traefik-gateway-dynamic.yaml:/etc/traefik/dynamic.yaml:ro" \
    --volume "$certificate:/run/traefik/tls.crt:ro" \
    --volume "$certificate_key:/run/traefik/tls.key:ro" \
    traefik:v3.7.13 \
      --entryPoints.websecure.address=:443 \
      --entryPoints.ping.address=:8080 \
      --ping=true \
      --ping.entryPoint=ping \
      --providers.file.filename=/etc/traefik/dynamic.yaml \
      --providers.file.watch=false \
      --log.level=INFO "$@")")
}
# The only transport setting that differs between these disposable proxies is
# the absolute request-body read deadline. This is static entrypoint configuration.
start_proxy "$proxy" netratel.example.test
start_proxy "$streaming_proxy" gateway-streaming \
  --entryPoints.websecure.transport.respondingTimeouts.readTimeout=0s
docker inspect "$proxy" --format 'Traefik fixture image={{.Config.Image}} id={{.Image}}'

stage="waiting for fixture startup"
healthy=0
for _ in $(seq 1 30); do
  if docker exec "$proxy" traefik healthcheck --ping >/dev/null 2>&1 \
    && docker exec "$streaming_proxy" traefik healthcheck --ping >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 1
done
if (( ! healthy )); then
  docker logs "$proxy" >&2 || true
  docker logs "$streaming_proxy" >&2 || true
  echo 'Traefik did not become ready within 30 seconds.' >&2
  exit 1
fi

stage="validating exact gRPC route, trailing-dot boundary, and REST fallback"
docker exec "$fixture" node /app/traefik-upstream.js probe
docker exec "$fixture" node /app/traefik-upstream.js probe gateway-streaming
if (( ! native_only )); then
  stage="validating one sustained duplex stream per profile across several 60-second windows"
  docker exec "$fixture" node /app/traefik-upstream.js sustained
  docker logs "$fixture"
  echo 'Traefik gateway routing and sustained synthetic duplex regressions passed.'
else
  echo 'Native-only rerun: repeating route checks and authenticated sustained profiles; synthetic sustained profiles omitted.'
fi

if [[ -n "${NETRATEL_GATEWAY_TEST_ASSEMBLY:-}" ]]; then
  stage="validating authenticated native gateway across the same proxy fixture"
  export NETRATEL_GATEWAY_TEST_BACKEND_PORT=26223
  export NETRATEL_GATEWAY_PROXY_ENDPOINT="https://$(docker port "$streaming_proxy" 443/tcp)"
  export NETRATEL_GATEWAY_PROXY_DEFAULT_ENDPOINT="https://$(docker port "$proxy" 443/tcp)"
  export NETRATEL_GATEWAY_PROXY_CA_PATH="$ca_certificate"
  results_directory="${NETRATEL_GATEWAY_TEST_RESULTS_DIRECTORY:-$root/TestResults/gateway-transport}"
  mkdir -p "$results_directory"
  result_path="$results_directory/native-gateway.trx"
  rm -f "$result_path"
  dotnet "$NETRATEL_GATEWAY_TEST_ASSEMBLY" \
    -showLiveOutput \
    -method NetRatel.Tests.API.AgentGatewayServiceTests.Connect_NativeDuplexRemainsAdmittedAcrossProxyReadDeadline \
    -result-trx "$result_path"
  # A missing/misnamed selected test must not silently turn the hosted gate green.
  python3 - "$result_path" <<'PY'
import sys
import xml.etree.ElementTree as ET

document = ET.parse(sys.argv[1])
counters = next(element for element in document.iter() if element.tag.endswith("Counters"))
assert all(int(counters.attrib.get(key, "0")) == expected for key, expected in
           (("total", 1), ("executed", 1), ("passed", 1), ("failed", 0), ("notExecuted", 0))), counters.attrib
results = [element for element in document.iter() if element.tag.endswith("UnitTestResult")]
assert len(results) == 1 and results[0].attrib.get("outcome") == "Passed"
assert "Connect_NativeDuplexRemainsAdmittedAcrossProxyReadDeadline" in results[0].attrib.get("testName", "")
print("Authenticated native gateway hosted receipt: executed=1 passed=1 skipped=0.")
PY
fi
