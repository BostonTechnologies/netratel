#!/usr/bin/env bash

set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

solution_projects=(
  NetRatel.API
  NetRatel.AgentClient
  NetRatel.AgentGateway.Contracts
  NetRatel.Akka
  NetRatel.AppHost
  NetRatel.Application
  NetRatel.Cli
  NetRatel.Client
  NetRatel.Infrastructure
  NetRatel.Mcp.Core
  NetRatel.Mcp.Http
  NetRatel.Mcp
  NetRatel.ServiceDefaults
  NetRatel.Shared
  NetRatel.Tests
  NetRatel.Web
  NetRatel.API.IntegrationTests
  NetRatel.Web.ComponentTests
  NetRatel.Web.PlaywrightTests
)

failed=false
fail() {
  printf 'layout validation: %s\n' "$*" >&2
  failed=true
}

for project in "${solution_projects[@]}"; do
  [[ -d "src/NetRatel/$project" ]] || fail "missing solution project directory src/NetRatel/$project"
  [[ ! -e "$project" ]] || fail "solution project directory reintroduced at repository root: $project"
done

for dockerfile in \
  docker/api/Dockerfile \
  docker/web/Dockerfile \
  docker/mcp-http/Dockerfile \
  docker/migrations/Dockerfile \
  docker/client/Dockerfile.public; do
  [[ -f "$dockerfile" ]] || fail "missing Dockerfile $dockerfile"
done

for collateral in \
  docker/api/docker-entrypoint.sh \
  docker/web/docker-entrypoint.sh \
  docker/README.md \
  .dockerignore; do
  [[ -f "$collateral" ]] || fail "missing Docker collateral $collateral"
done

[[ ! -e docker/client/Dockerfile ]] || fail 'private Client Dockerfile must not be present in the public export'

if find src/NetRatel -type f \( -name Dockerfile -o -name 'Dockerfile.*' -o -name '*.Dockerfile' \) -print -quit | rg -q '.'; then
  fail 'project-local Dockerfiles must not exist below src/NetRatel/'
fi

active_paths=(.github docker docs scripts tools NetRatel.sln .gitignore)
stale_pattern='NetRatel\.(API|Web|Mcp\.Http|Client)/Dockerfile|docker/client/Dockerfile($|[^.])|docker/(traefik|deployment-control-plane|remote-support-turn-generic)|NetRatel\.Client/tools/'
if rg -n \
  --glob '!docs/repository-layout-ci-migration.md' \
  --glob '!tools/ci/validate-layout.sh' \
  --glob '!tools/ci/check-public-disclosure.sh' \
  "$stale_pattern" "${active_paths[@]}"; then
  fail 'active build or deployment collateral still refers to a pre-migration path'
fi

while IFS= read -r project; do
  [[ -z "$project" ]] && continue
  [[ -f "$project" ]] || fail "solution references missing project: $project"
done < <(dotnet sln NetRatel.sln list | tail -n +3)

if [[ "$failed" == true ]]; then
  exit 1
fi

printf 'layout validation passed\n'
