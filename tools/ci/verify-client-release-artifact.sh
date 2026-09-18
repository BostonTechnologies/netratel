#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --artifacts <directory> --version <semver> --runtime <rid> --extension <zip|tar.gz>" >&2
  exit 2
}

artifacts=""
version=""
runtime=""
extension=""
while (( $# > 0 )); do
  case "$1" in
    --artifacts) artifacts="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --runtime) runtime="${2:-}"; shift 2 ;;
    --extension) extension="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$artifacts" && -n "$version" && -n "$runtime" && ( "$extension" == zip || "$extension" == tar.gz ) ]] || usage
archive="netratel-client-${version}-${runtime}.${extension}"
sbom="netratel-client-${version}-${runtime}.spdx.json"
manifest="netratel-client-${runtime}/netratel-client-manifest.json"

[[ -s "$artifacts/$archive" ]] || { echo "Missing Client archive: $archive" >&2; exit 1; }
[[ -s "$artifacts/$sbom" ]] || { echo "Missing Client SBOM: $sbom" >&2; exit 1; }
jq -e '
  (.spdxVersion | type == "string")
  and ((.packages | type == "array") and length > 1)
  and ([.files[]? | .checksums[]? | select(.algorithm == "SHA256") | .checksumValue]
       | length > 0 and all(test("^0+$") | not))
' "$artifacts/$sbom" >/dev/null || {
  echo "Client SBOM lacks dependency coverage or contains placeholder SHA-256 values." >&2
  exit 1
}

if [[ "$extension" == zip ]]; then
  entries="$(unzip -Z1 "$artifacts/$archive")"
else
  entries="$(tar -tzf "$artifacts/$archive")"
fi

if grep -Eq '(^/|(^|/)\.\.(/|$))' <<<"$entries"; then
  echo "Client archive contains an unsafe entry." >&2
  exit 1
fi
grep -qx "$manifest" <<<"$entries" || { echo "Client archive is missing its manifest." >&2; exit 1; }
for required_entry in \
  "netratel-client-${runtime}/LICENSE" \
  "netratel-client-${runtime}/NOTICE" \
  "netratel-client-${runtime}/appsettings.json" \
  "netratel-client-${runtime}/powershell.config.json" \
  "netratel-client-${runtime}/terminal_pty_helper.py" \
  "netratel-client-${runtime}/updater/netratel-update.ps1" \
  "netratel-client-${runtime}/updater/netratel-update.sh"; do
  grep -qx "$required_entry" <<<"$entries" || {
    echo "Client archive is missing required support file: $required_entry" >&2
    exit 1
  }
done
if [[ "$runtime" == win-* ]]; then
  grep -Eq "^netratel-client-${runtime}/NetRatel\.Client\.exe$" <<<"$entries" || {
    echo "Windows Client archive is missing its executable." >&2; exit 1;
  }
else
  grep -Eq "^netratel-client-${runtime}/NetRatel\.Client$" <<<"$entries" || {
    echo "Client archive is missing its executable." >&2; exit 1;
  }
fi

if [[ "$extension" == zip ]]; then
  manifest_json="$(unzip -p "$artifacts/$archive" "$manifest")"
else
  manifest_json="$(tar -xOzf "$artifacts/$archive" "$manifest")"
fi
jq -e --arg version "$version" --arg runtime "$runtime" \
  '.product == "NetRatel.Client" and .version == $version and .runtimeId == $runtime' \
  <<<"$manifest_json" >/dev/null || { echo "Client manifest does not match the release metadata." >&2; exit 1; }

extract_dir="$(mktemp -d)"
cleanup() {
  find "$extract_dir" -depth -delete 2>/dev/null || true
}
trap cleanup EXIT

if [[ "$extension" == zip ]]; then
  unzip -qq "$artifacts/$archive" -d "$extract_dir"
else
  tar -xzf "$artifacts/$archive" -C "$extract_dir"
fi
if grep -rI -q -E -- '-----BEGIN ([A-Z ]*PRIVATE KEY|CERTIFICATE)|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}' "$extract_dir"; then
  echo "Client archive contains potential key material." >&2
  exit 1
fi

if [[ "$runtime" == linux-x64 ]]; then
  client="$extract_dir/netratel-client-${runtime}/NetRatel.Client"
  native_library="$extract_dir/netratel-client-${runtime}/libnetratel_terminal_pty.so"
  [[ -x "$client" ]] || { echo "Linux Client executable is not executable." >&2; exit 1; }
  [[ -f "$native_library" ]] || { echo "Linux Client archive is missing its native PTY library." >&2; exit 1; }
  "$client" --terminal-pty-self-test-native
elif [[ "$runtime" == win-x64 ]]; then
  "$extract_dir/netratel-client-${runtime}/NetRatel.Client.exe" --version
else
  "$extract_dir/netratel-client-${runtime}/NetRatel.Client" --version
fi

(
  cd "$artifacts"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum --check SHA256SUMS
  elif command -v shasum >/dev/null 2>&1; then
    while read -r expected file; do
      [[ -n "$expected" && -n "$file" ]] || continue
      actual="$(shasum -a 256 "$file" | awk '{print $1}')"
      [[ "$actual" == "$expected" ]] || {
        echo "Checksum mismatch: $file" >&2
        exit 1
      }
      echo "$file: OK"
    done < SHA256SUMS
  else
    echo "Neither sha256sum nor shasum is available for checksum verification." >&2
    exit 1
  fi
)

echo "Verified Client review artifact for $runtime."
