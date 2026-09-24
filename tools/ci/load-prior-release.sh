#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${NETRATEL_UPGRADE_PRIOR_VERSION:-}" ]]; then
  prior_release="$(python3 tools/ci/select-prior-release.py)"
  export NETRATEL_UPGRADE_PRIOR_VERSION="$(jq -r '.tag' <<<"$prior_release")"
  export NETRATEL_UPGRADE_PRIOR_API_IMAGE="$(jq -r '.images.api' <<<"$prior_release")"
  export NETRATEL_UPGRADE_PRIOR_MIGRATIONS_IMAGE="$(jq -r '.images.migrations' <<<"$prior_release")"
  export NETRATEL_UPGRADE_PRIOR_WEB_IMAGE="$(jq -r '.images.web' <<<"$prior_release")"
  export NETRATEL_UPGRADE_PRIOR_CLIENT_IMAGE="$(jq -r '.images.client' <<<"$prior_release")"
fi
