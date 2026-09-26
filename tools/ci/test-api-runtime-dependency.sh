#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  echo "Usage: $0 --image IMAGE" >&2
}

image=''
while (($# > 0)); do
  case "$1" in
    --image)
      (($# >= 2)) || { usage; exit 2; }
      image="$2"
      shift 2
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

[[ -n "$image" ]] || { usage; exit 2; }
command -v cc >/dev/null 2>&1 || { echo "A C compiler is required for the native library probe." >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { echo "Docker is required for the native library probe." >&2; exit 1; }

temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
probe="$temporary_directory/load-shared-library"

cc -O2 -Wall -Wextra -Werror \
  tools/ci/load-shared-library.c \
  -ldl \
  -o "$probe"

docker run --rm \
  --user 1654:1654 \
  --volume "$probe:/tmp/netratel-load-shared-library:ro" \
  --entrypoint /tmp/netratel-load-shared-library \
  "$image" \
  libgssapi_krb5.so.2
