#!/usr/bin/env bash
set -euo pipefail

runtime="${1:-win-x64}"
case "$runtime" in
  win-x64|linux-x64|osx-arm64) ;;
  *) echo "Unsupported runtime: $runtime" >&2; exit 1 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"
outdir="$root/artifacts/client/$runtime"

mkdir -p "$outdir"
echo "Publishing NetRatel.Client for $runtime to $outdir"
dotnet publish "$project" -c Release -r "$runtime" /p:PublishSingleFile=true /p:SelfContained=true /p:PublishTrimmed=false -o "$outdir"
