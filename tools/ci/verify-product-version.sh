#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
manifest="$root/release/release-manifest.json"

version="$(sed -nE 's/^[[:space:]]*"version":[[:space:]]*"([^"]+)".*/\1/p' "$manifest")"
if [[ -z "$version" ]]; then
  echo "Release manifest does not declare a product version." >&2
  exit 1
fi

expected_prefix="${version%%-*}"
expected_suffix=""
if [[ "$version" == *-* ]]; then
  expected_suffix="${version#*-}"
fi

mapfile -t projects < <(find "$root/src/NetRatel" -name '*.csproj' -type f -print | sort)
if (( ${#projects[@]} == 0 )); then
  echo "No NetRatel projects were found." >&2
  exit 1
fi

for project in "${projects[@]}"; do
  relative="${project#"$root"/}"
  if rg -n '<(Version|VersionPrefix|VersionSuffix|PackageVersion|AssemblyVersion|FileVersion|InformationalVersion)>' "$project" >/dev/null; then
    echo "$relative declares product version metadata locally; use Directory.Build.props instead." >&2
    exit 1
  fi

  properties="$(dotnet msbuild "$project" -nologo -getProperty:Version,VersionPrefix,VersionSuffix,AssemblyVersion,FileVersion)"
  evaluated="$(jq -r '.Properties.Version' <<<"$properties")"
  prefix="$(jq -r '.Properties.VersionPrefix' <<<"$properties")"
  suffix="$(jq -r '.Properties.VersionSuffix' <<<"$properties")"
  assembly="$(jq -r '.Properties.AssemblyVersion' <<<"$properties")"
  file="$(jq -r '.Properties.FileVersion' <<<"$properties")"

  [[ "$evaluated" == "$version" ]] || { echo "$relative evaluated Version '$evaluated', expected '$version'." >&2; exit 1; }
  [[ "$prefix" == "$expected_prefix" ]] || { echo "$relative evaluated VersionPrefix '$prefix', expected '$expected_prefix'." >&2; exit 1; }
  [[ "$suffix" == "$expected_suffix" ]] || { echo "$relative evaluated VersionSuffix '$suffix', expected '$expected_suffix'." >&2; exit 1; }
  [[ "$assembly" == "$expected_prefix.0" ]] || { echo "$relative evaluated AssemblyVersion '$assembly', expected '$expected_prefix.0'." >&2; exit 1; }
  [[ "$file" == "$expected_prefix.0" ]] || { echo "$relative evaluated FileVersion '$file', expected '$expected_prefix.0'." >&2; exit 1; }
done

echo "Verified ${#projects[@]} projects at product version $version."
