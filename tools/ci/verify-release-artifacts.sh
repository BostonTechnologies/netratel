#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --artifacts <directory> --version <semver>" >&2
  exit 2
}

artifacts=""
version=""
while (( $# > 0 )); do
  case "$1" in
    --artifacts) artifacts="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done

[[ -n "$artifacts" && -n "$version" ]] || usage
[[ -d "$artifacts" ]] || { echo "Artifact directory does not exist: $artifacts" >&2; exit 1; }

cli="netratel-cli-${version}-linux-x64.tar.gz"
cli_tool="NetRatel.Cli.${version}.nupkg"
mcp="netratel-mcp-stdio-${version}-linux-x64.tar.gz"
bundle="netratel-compose-${version}.tar.gz"
sbom="netratel-${version}.spdx.json"

scan_archive_contents() {
  local archive="$1"
  local extract_dir
  extract_dir="$(mktemp -d)"
  if ! tar -xzf "$artifacts/$archive" -C "$extract_dir"; then
    find "$extract_dir" -depth -delete 2>/dev/null || true
    return 1
  fi
  if grep -rI -q -E -- '-----BEGIN ([A-Z ]*PRIVATE KEY|CERTIFICATE)|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}' "$extract_dir"; then
    find "$extract_dir" -depth -delete 2>/dev/null || true
    echo "Release archive contains potential key material." >&2
    return 1
  fi
  find "$extract_dir" -depth -delete 2>/dev/null || true
}

for archive in "$cli" "$mcp" "$bundle"; do
  [[ -s "$artifacts/$archive" ]] || { echo "Missing release archive: $archive" >&2; exit 1; }
  tar -tzf "$artifacts/$archive" | while IFS= read -r path; do
    case "$path" in
      /*|../*|*/../*) echo "Unsafe archive entry in $archive: $path" >&2; exit 1 ;;
    esac
  done
  scan_archive_contents "$archive"
done

[[ -s "$artifacts/cli/$cli_tool" ]] || { echo "Missing CLI tool package: $cli_tool" >&2; exit 1; }
unzip -l "$artifacts/cli/$cli_tool" | grep -Eq 'tools/net10\.0/any/(netratel|netratel\.dll)$' ||
  { echo "CLI tool package does not contain the NetRatel tool entry point." >&2; exit 1; }

tar -tzf "$artifacts/$cli" | grep -Eq '^netratel-cli-linux-x64/(netratel|netratel\.dll)$' ||
  { echo "CLI archive does not contain the NetRatel executable." >&2; exit 1; }
tar -tzf "$artifacts/$mcp" | grep -Eq '^netratel-mcp-linux-x64/(NetRatel\.Mcp|NetRatel\.Mcp\.dll)$' ||
  { echo "stdio MCP archive does not contain the MCP executable." >&2; exit 1; }
tar -tzf "$artifacts/$bundle" | grep -qx './compose.images.yaml' ||
  { echo "Release bundle is missing compose.images.yaml." >&2; exit 1; }
tar -tzf "$artifacts/$bundle" | grep -qx './compose.mcp-http.yaml' ||
  { echo "Release bundle is missing compose.mcp-http.yaml." >&2; exit 1; }
tar -tzf "$artifacts/$bundle" | grep -qx './.env.images.example' ||
  { echo "Release bundle is missing .env.images.example." >&2; exit 1; }
tar -tzf "$artifacts/$bundle" | grep -qx './release-manifest.json' ||
  { echo "Release bundle is missing release-manifest.json." >&2; exit 1; }

release_extract_dir="$(mktemp -d)"
cleanup_release_extract() {
  find "$release_extract_dir" -depth -delete 2>/dev/null || true
}
trap cleanup_release_extract EXIT
tar -xzf "$artifacts/$cli" -C "$release_extract_dir"
tar -xzf "$artifacts/$mcp" -C "$release_extract_dir"

cli_executable="$release_extract_dir/netratel-cli-linux-x64/netratel"
mcp_assembly="$release_extract_dir/netratel-mcp-linux-x64/NetRatel.Mcp.dll"
[[ -x "$cli_executable" ]] || { echo "CLI archive executable is not executable." >&2; exit 1; }
[[ -f "$mcp_assembly" ]] || { echo "stdio MCP archive assembly is missing." >&2; exit 1; }
"$cli_executable" --help >/dev/null
cli_version="$("$cli_executable" --version)"
[[ "$cli_version" == "$version" ]] || {
  echo "CLI archive version '$cli_version' does not match release version '$version'." >&2
  exit 1
}

cli_tool_extract_dir="$release_extract_dir/cli-tool"
dotnet tool install NetRatel.Cli --tool-path "$cli_tool_extract_dir" --add-source "$artifacts/cli" --version "$version" --ignore-failed-sources >/dev/null
cli_tool_version="$("$cli_tool_extract_dir/netratel" --version)"
[[ "$cli_tool_version" == "$version" ]] || {
  echo "CLI tool package version '$cli_tool_version' does not match release version '$version'." >&2
  exit 1
}

mcp_initialize_response="$({
  printf '%s\\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"release-artifact-verifier","version":"1"}}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"get"}}}'
  sleep 1
} | timeout 10s dotnet "$mcp_assembly" 2>/dev/null)"
jq -se '
  length == 3
  and (map(.id) | sort == [1, 2, 3])
  and (map(select(.id == 1))[0] | .jsonrpc == "2.0"
    and .result.serverInfo.name == "NetRatel.Mcp"
    and (.result.capabilities.tools | type == "object"))
  and (map(select(.id == 2))[0].result.tools | type == "array" and length > 0)
  and map(select(.id == 3))[0].result.structuredContent.success == true
' <<<"$mcp_initialize_response" >/dev/null || {
  echo "stdio MCP archive did not initialize, list tools, and complete its capabilities read." >&2
  exit 1
}

invalid_mcp_config="$release_extract_dir/invalid-mcp-config.json"
printf '%s\n' '{"apiBaseUrl":"not-an-absolute-uri"}' > "$invalid_mcp_config"
invalid_mcp_output="$(NETRATEL_MCP_CONFIG="$invalid_mcp_config" timeout 10s dotnet "$mcp_assembly" </dev/null 2>&1 || true)"
grep -Fq 'NetRatel MCP API base URL must be absolute.' <<<"$invalid_mcp_output" || {
  echo "stdio MCP archive did not report an actionable invalid API URL diagnostic." >&2
  exit 1
}

[[ -s "$artifacts/$sbom" ]] || { echo "Missing SPDX SBOM: $sbom" >&2; exit 1; }
grep -q '"spdxVersion"' "$artifacts/$sbom" ||
  { echo "Release SBOM is not SPDX JSON." >&2; exit 1; }

[[ -s "$artifacts/SHA256SUMS" ]] || { echo "Missing SHA256SUMS." >&2; exit 1; }
grep -Fq " cli/$cli_tool" "$artifacts/SHA256SUMS" || {
  echo "CLI tool package is missing from SHA256SUMS." >&2
  exit 1
}
(
  cd "$artifacts"
  sha256sum --check SHA256SUMS
)

echo "Verified review artifacts for NetRatel $version."
