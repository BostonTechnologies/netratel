#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --dockerfile <path> --image <tag> --version <semver> --revision <public-sha> [--target <stage>] [--build-arg <name=value>]" >&2
  exit 2
}

dockerfile=""
image=""
version=""
revision=""
build_args=()
target=""
while (( $# > 0 )); do
  case "$1" in
    --dockerfile) dockerfile="${2:-}"; shift 2 ;;
    --image) image="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --revision) revision="${2:-}"; shift 2 ;;
    --build-arg) build_args+=(--build-arg "${2:-}"); shift 2 ;;
    --target) target="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$dockerfile" && -n "$image" && -n "$version" && -n "$revision" ]] || usage
[[ -f "$dockerfile" ]] || { echo "Dockerfile does not exist: $dockerfile" >&2; exit 1; }
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "Image revision must be a public 40-character SHA." >&2; exit 1; }
for (( index=1; index < ${#build_args[@]}; index+=2 )); do
  [[ "${build_args[index]}" != PublicSourceRevision && "${build_args[index]}" != PublicSourceRevision=* ]] || {
    echo "PublicSourceRevision is controlled by --revision." >&2
    exit 1
  }
done
target_args=()
if [[ -n "$target" ]]; then target_args=(--target "$target"); fi

docker build \
  "${target_args[@]}" \
  --build-arg "PublicSourceRevision=$revision" \
  --file "$dockerfile" \
  --tag "$image" \
  --label "org.opencontainers.image.title=NetRatel" \
  --label "org.opencontainers.image.source=https://github.com/BostonTechnologies/netratel" \
  --label "org.opencontainers.image.version=$version" \
  --label "org.opencontainers.image.revision=$revision" \
  "${build_args[@]}" \
  .
