#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
network="netratel-traefik-routing-${GITHUB_RUN_ID:-local}-${RANDOM}"
fixture="${network}-fixture"
proxy="${network}-proxy"
temporary_directory="$(mktemp -d)"
ca_certificate="$temporary_directory/ca.crt"
ca_key="$temporary_directory/ca.key"
certificate="$temporary_directory/tls.crt"
certificate_key="$temporary_directory/tls.key"
extensions="$temporary_directory/tls.ext"
network_created=0
created_container_ids=()

cleanup() {
  local status=$?
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
subjectAltName=DNS:netratel.example.test
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
created_container_ids+=("$(docker run --detach --name "$proxy" --network "$network" \
  --network-alias netratel.example.test \
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
    --log.level=INFO)")

stage="waiting for fixture startup"
healthy=0
for _ in $(seq 1 30); do
  if docker exec "$proxy" traefik healthcheck --ping >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 1
done
if (( ! healthy )); then
  docker logs "$proxy" >&2 || true
  echo 'Traefik did not become ready within 30 seconds.' >&2
  exit 1
fi

stage="validating exact gRPC route, trailing-dot boundary, and REST fallback"
docker exec "$fixture" node /app/traefik-upstream.js probe
echo 'Traefik gateway route regression passed with synthetic fixture responders.'
