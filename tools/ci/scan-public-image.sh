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

scan_dir="$(mktemp -d)"
cleanup() { find "$scan_dir" -depth -delete 2>/dev/null || true; }
trap cleanup EXIT

docker image save "$image" | tar -xf - -C "$scan_dir"
layer_dir="$scan_dir/layers"
mkdir -p "$layer_dir"
layer_index=0
while IFS= read -r -d '' layer; do
  layer_index=$((layer_index + 1))
  extraction_dir="$layer_dir/$layer_index"
  mkdir "$extraction_dir"
  tar -xf "$layer" -C "$extraction_dir"
done < <(find "$scan_dir" -type f -name layer.tar -print0)

# Scan extracted text files rather than serialized image archives. The latter
# contain arbitrary binary data and can create false positives for key markers.
if grep -rI -q -E -- \
  '-----BEGIN [A-Z ]*PRIVATE KEY|<key[[:space:]>]|<encryptedKey[[:space:]>]|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}' \
  "$layer_dir"; then
  echo "Public image contains potential credential material." >&2
  exit 1
fi

echo "Verified public image metadata and generic credential scan: $image"
