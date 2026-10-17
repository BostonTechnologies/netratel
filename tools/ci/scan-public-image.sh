#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --image <tag> --version <semver> --revision <public-sha>" >&2
  exit 2
}

image=""
version=""
revision=""
while (( $# > 0 )); do
  case "$1" in
    --image) image="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --revision) revision="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$image" && -n "$version" && -n "$revision" ]] || usage
for required_tool in docker jq tar grep find mktemp python3; do
  command -v "$required_tool" >/dev/null 2>&1 || {
    echo "Required image verifier tool '$required_tool' is unavailable." >&2; exit 1;
  }
done
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "Image revision must be a public 40-character SHA." >&2; exit 1; }

inspect="$(docker image inspect "$image")"
jq -e --arg version "$version" --arg revision "$revision" '
  .[0].Config.Labels["org.opencontainers.image.title"] == "NetRatel"
  and .[0].Config.Labels["org.opencontainers.image.source"] == "https://github.com/BostonTechnologies/netratel"
  and .[0].Config.Labels["org.opencontainers.image.version"] == $version
  and .[0].Config.Labels["org.opencontainers.image.revision"] == $revision
' <<<"$inspect" >/dev/null || {
  echo "Image is missing required public OCI metadata." >&2
  exit 1
}

docker run --rm --entrypoint /bin/sh "$image" -c '
  test -r /app/licenses/LICENSE
  test -r /app/licenses/NOTICE
  test -s /app/THIRD-PARTY-NOTICES.txt
  test -s /app/runtime.spdx.json
' || {
  echo "Image is missing its required LICENSE or NOTICE file." >&2
  exit 1
}

scan_dir="$(mktemp -d)"
cleanup() { find "$scan_dir" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT

docker image save "$image" | tar -xf - -C "$scan_dir"
# Resolve manifest references rather than searching for a legacy basename. The
# helper verifies every unique actual layer, including overwritten files, and
# rejects empty coverage. Exact reviewed upstream help-text matches retain their
# raw grep status in the emitted coverage record (see #281).
python3 "$(dirname "$0")/scan-saved-image.py" "$scan_dir"

echo "Verified public image metadata, license notices, and generic credential scan: $image"
