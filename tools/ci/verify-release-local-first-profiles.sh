#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --bundle <netratel-compose.tar.gz>" >&2
  exit 2
}

bundle=""
while (( $# > 0 )); do
  case "$1" in
    --bundle) bundle="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -s "$bundle" ]] || { echo "Release Compose bundle is missing: $bundle" >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { echo "Docker Compose is required to validate release profiles." >&2; exit 1; }

temporary_dir="$(mktemp -d)"
cleanup() {
  find "$temporary_dir" -depth -delete 2>/dev/null || true
}
trap cleanup EXIT

tar -xzf "$bundle" -C "$temporary_dir"
env_file="$temporary_dir/.env.images.example"
[[ -f "$env_file" ]] || { echo "Release bundle is missing its image environment example." >&2; exit 1; }

compose() {
  env -u OIDC_AUTHORITY -u OIDC_CLIENT_ID -u OIDC_API_SCOPE -u OIDC_API_AUDIENCE \
    -u OIDC_TOKEN_ENDPOINT -u OIDC_ADMIN_GROUP_ID -u OIDC_CLIENT_SECRET \
    NETRATEL_API_IMAGE=netratel-api:bundle-validation \
    NETRATEL_WEB_IMAGE=netratel-web:bundle-validation \
    NETRATEL_MIGRATIONS_IMAGE=netratel-migrations:bundle-validation \
    NETRATEL_AGENT_AUTH_PRIVATE_KEY=/dev/null \
    docker compose --project-directory "$temporary_dir" --env-file "$env_file" "$@"
}

# These validations run only against the extracted archive. They prevent a
# source-only Compose recipe or mandatory OIDC interpolation from reappearing.
compose -f "$temporary_dir/compose.images.yaml" config --quiet
NETRATEL_EXTERNAL_DATABASE_CONNECTION_STRING='Host=external-db;Database=netratel;Username=netratel;Password=validation-only' \
  compose -f "$temporary_dir/compose.images.yaml" -f "$temporary_dir/compose.external-postgres.yaml" config --quiet

rendered="$(compose -f "$temporary_dir/compose.images.yaml" config --format json)"
jq -e '
  .services.api.restart == "unless-stopped"
  and ([.services.api.volumes[]?.source] | index("web-keys"))
  and ([.services.web.volumes[]?.source] | index("web-keys"))
  and .services.api.environment.Authentication__Oidc__Authority == ""
  and .services.web.environment.Authentication__Oidc__Authority == ""
' <<<"$rendered" >/dev/null || {
  echo "Release PostgreSQL profile does not retain local-first restart, shared-key, and OIDC-optional semantics." >&2
  exit 1
}

echo "Validated local-first Compose profiles from extracted release bundle."
