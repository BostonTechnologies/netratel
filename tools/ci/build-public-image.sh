#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --dockerfile <path> --image <tag> --version <semver> --revision <public-sha>" >&2
  exit 2
}

dockerfile=""
image=""
version=""
revision=""
while (( $# > 0 )); do
  case "$1" in
    --dockerfile) dockerfile="${2:-}"; shift 2 ;;
    --image) image="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --revision) revision="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$dockerfile" && -n "$image" && -n "$version" && -n "$revision" ]] || usage
[[ -f "$dockerfile" ]] || { echo "Dockerfile does not exist: $dockerfile" >&2; exit 1; }
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "Image revision must be a public 40-character SHA." >&2; exit 1; }

docker build \
  --file "$dockerfile" \
  --tag "$image" \
  --label "org.opencontainers.image.title=NetRatel" \
  --label "org.opencontainers.image.source=https://github.com/BostonTechnologies/netratel" \
  --label "org.opencontainers.image.version=$version" \
  --label "org.opencontainers.image.revision=$revision" \
  .
