#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
network="netratel-nginx-idle-${GITHUB_RUN_ID:-local}-${RANDOM}"
fixture="${network}-fixture"
proxy="${network}-proxy"
temporary_directory="$(mktemp -d)"
network_created=0
created_container_ids=()
cleanup() {
  local status=$?
  for container_id in "${created_container_ids[@]}"; do
    docker rm -f "$container_id" >/dev/null 2>&1 || true
  done
  if (( network_created )); then docker network rm "$network" >/dev/null 2>&1 || true; fi
  rm -rf "$temporary_directory"
  return "$status"
}
trap cleanup EXIT

openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj '/CN=netratel.example.test' -addext 'subjectAltName=DNS:netratel.example.test' \
  -keyout "$temporary_directory/tls.key" -out "$temporary_directory/tls.crt" >/dev/null 2>&1
printf 'events {}\nhttp { include /etc/nginx/conf.d/public.conf; }\n' > "$temporary_directory/nginx.conf"
docker network create "$network" >/dev/null
network_created=1
created_container_ids+=("$(docker run --detach --name "$fixture" --network "$network" \
  --network-alias api --network-alias web \
  --volume "$root/tools/ci/tests/nginx-gateway-idle.js:/fixture/probe.js:ro" \
  --volume "$root/tools/ci/tests/file-browser-transfer-lifecycle.js:/fixture/file-browser-transfer-lifecycle.js:ro" \
  --volume "$root/src/NetRatel/NetRatel.Web/wwwroot/download.js:/fixture/download.js:ro" \
  --volume "$temporary_directory/tls.crt:/fixture/tls.crt:ro" \
  node:22-alpine node /fixture/probe.js server)")
created_container_ids+=("$(docker run --detach --name "$proxy" --network "$network" \
  --network-alias netratel.example.test \
  --volume "$temporary_directory/nginx.conf:/etc/nginx/nginx.conf:ro" \
  --volume "$root/release/nginx.public-https.conf:/etc/nginx/conf.d/public.conf:ro" \
  --volume "$temporary_directory/tls.crt:/run/netratel-ingress/tls.crt:ro" \
  --volume "$temporary_directory/tls.key:/run/netratel-ingress/tls.key:ro" \
  nginx:1.27-alpine)")
healthy=0
for _ in $(seq 1 30); do
  if docker exec "$proxy" sh -c 'test -s /var/run/nginx.pid' >/dev/null 2>&1; then
    healthy=1
    break
  fi
  sleep 1
done
(( healthy )) || { echo 'Nginx fixture did not become ready.' >&2; exit 1; }
# Exercise the actual native browser transfer asset with the existing Node
# fixture before testing the public ingress profile.
docker exec "$fixture" node /fixture/file-browser-transfer-lifecycle.js /fixture/download.js
docker exec "$fixture" node /fixture/probe.js probe
