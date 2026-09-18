#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
manifest="$root/release/release-manifest.json"

for required_tool in python3 dotnet jq find; do
  command -v "$required_tool" >/dev/null 2>&1 || {
    echo "Required release-version tool '$required_tool' is unavailable; cannot verify product version." >&2
    exit 1
  }
done

version="$(jq -er '.version | strings | select(length > 0)' "$manifest")" || {
  echo "Release manifest does not declare a usable product version." >&2
  exit 1
}

expected_prefix="${version%%-*}"
expected_suffix=""
if [[ "$version" == *-* ]]; then
  expected_suffix="${version#*-}"
fi

python3 "$root/tools/ci/verify-product-version.py" --root "$root"

mapfile -t projects < <(find "$root/src/NetRatel" -name '*.csproj' -type f -print | sort) || {
  echo "Unable to enumerate NetRatel projects." >&2
  exit 1
}
if (( ${#projects[@]} == 0 )); then
  echo "No NetRatel projects were found." >&2
  exit 1
fi

for project in "${projects[@]}"; do
  relative="${project#"$root"/}"
  properties="$(dotnet msbuild "$project" -nologo -getProperty:Version,VersionPrefix,VersionSuffix,PackageVersion,InformationalVersion,AssemblyVersion,FileVersion)" || {
    echo "Unable to evaluate version metadata for $relative." >&2
    exit 1
  }
  evaluated="$(jq -r '.Properties.Version' <<<"$properties")"
  prefix="$(jq -r '.Properties.VersionPrefix' <<<"$properties")"
  suffix="$(jq -r '.Properties.VersionSuffix' <<<"$properties")"
  package="$(jq -r '.Properties.PackageVersion' <<<"$properties")"
  informational="$(jq -r '.Properties.InformationalVersion' <<<"$properties")"
  assembly="$(jq -r '.Properties.AssemblyVersion' <<<"$properties")"
  file="$(jq -r '.Properties.FileVersion' <<<"$properties")"

  [[ "$evaluated" == "$version" ]] || { echo "$relative evaluated Version '$evaluated', expected '$version'." >&2; exit 1; }
  [[ "$prefix" == "$expected_prefix" ]] || { echo "$relative evaluated VersionPrefix '$prefix', expected '$expected_prefix'." >&2; exit 1; }
  [[ "$suffix" == "$expected_suffix" ]] || { echo "$relative evaluated VersionSuffix '$suffix', expected '$expected_suffix'." >&2; exit 1; }
  [[ "$package" == "$version" ]] || { echo "$relative evaluated PackageVersion '$package', expected '$version'." >&2; exit 1; }
  [[ "$informational" == "$version" ]] || { echo "$relative evaluated InformationalVersion '$informational', expected '$version'." >&2; exit 1; }
  [[ "$assembly" == "$expected_prefix.0" ]] || { echo "$relative evaluated AssemblyVersion '$assembly', expected '$expected_prefix.0'." >&2; exit 1; }
  [[ "$file" == "$expected_prefix.0" ]] || { echo "$relative evaluated FileVersion '$file', expected '$expected_prefix.0'." >&2; exit 1; }
done

echo "Verified ${#projects[@]} projects at product version $version."
