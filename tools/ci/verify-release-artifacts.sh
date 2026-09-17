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

temporary_dir="$(mktemp -d)"
cleanup_temporary_dir() {
  find "$temporary_dir" -depth -delete 2>/dev/null || true
}
trap cleanup_temporary_dir EXIT

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
  listing="$temporary_dir/$archive.entries"
  tar -tzf "$artifacts/$archive" > "$listing"
  while IFS= read -r path; do
    case "$path" in
      /*|../*|*/../*) echo "Unsafe archive entry in $archive: $path" >&2; exit 1 ;;
    esac
  done < "$listing"
  scan_archive_contents "$archive"
done

archive_has_entry() {
  local archive="$1"
  local pattern="$2"
  local message="$3"
  grep -Eq -- "$pattern" "$temporary_dir/$archive.entries" || {
    echo "$message" >&2
    exit 1
  }
}

[[ -s "$artifacts/cli/$cli_tool" ]] || { echo "Missing CLI tool package: $cli_tool" >&2; exit 1; }
unzip -l "$artifacts/cli/$cli_tool" | grep -Eq 'tools/net10\.0/any/(netratel|netratel\.dll)$' ||
  { echo "CLI tool package does not contain the NetRatel tool entry point." >&2; exit 1; }

archive_has_entry "$cli" '^netratel-cli-linux-x64/(netratel|netratel\.dll)$' "CLI archive does not contain the NetRatel executable."
archive_has_entry "$mcp" '^netratel-mcp-linux-x64/(NetRatel\.Mcp|NetRatel\.Mcp\.dll)$' "stdio MCP archive does not contain the MCP executable."
archive_has_entry "$bundle" '^\./compose\.images\.yaml$' "Release bundle is missing compose.images.yaml."
archive_has_entry "$bundle" '^\./compose\.mcp-http\.yaml$' "Release bundle is missing compose.mcp-http.yaml."
archive_has_entry "$bundle" '^\./\.env\.images\.example$' "Release bundle is missing .env.images.example."
archive_has_entry "$bundle" '^\./release-manifest\.json$' "Release bundle is missing release-manifest.json."

release_extract_dir="$temporary_dir/release"
mkdir "$release_extract_dir"
tar -xzf "$artifacts/$cli" -C "$release_extract_dir"
tar -xzf "$artifacts/$mcp" -C "$release_extract_dir"

cli_executable="$release_extract_dir/netratel-cli-linux-x64/netratel"
mcp_assembly="$release_extract_dir/netratel-mcp-linux-x64/NetRatel.Mcp.dll"
[[ -x "$cli_executable" ]] || { echo "CLI archive executable is not executable." >&2; exit 1; }
[[ -f "$mcp_assembly" ]] || { echo "stdio MCP archive assembly is missing." >&2; exit 1; }
"$cli_executable" --help >/dev/null
cli_version="$("$cli_executable" --version)"
[[ "$cli_version" == "$version" || "$cli_version" == "$version"+* ]] || {
  echo "CLI archive version '$cli_version' does not match release version '$version'." >&2
  exit 1
}

cli_tool_extract_dir="$release_extract_dir/cli-tool"
dotnet tool install NetRatel.Cli --tool-path "$cli_tool_extract_dir" --add-source "$artifacts/cli" --version "$version" --ignore-failed-sources >/dev/null
cli_tool_version="$("$cli_tool_extract_dir/netratel" --version)"
[[ "$cli_tool_version" == "$version" || "$cli_tool_version" == "$version"+* ]] || {
  echo "CLI tool package version '$cli_tool_version' does not match release version '$version'." >&2
  exit 1
}

mcp_config="$release_extract_dir/mcp-config.json"
printf '%s\n' '{"apiBaseUrl":"https://netratel.example.invalid","oidcTokenUrl":"https://issuer.example.invalid/connect/token","oidcClientId":"release-artifact-verifier","oidcUsername":"release-artifact-verifier","oidcAppPassword":"synthetic-release-artifact-password","oidcScope":"netratel.api"}' > "$mcp_config"

coproc MCP_STDIO {
  NETRATEL_MCP_CONFIG="$mcp_config" NETRATEL_MCP_INSTANCE=dev timeout 10s dotnet "$mcp_assembly" 2>/dev/null
}
mcp_stdout_fd="${MCP_STDIO[0]}"
mcp_stdin_fd="${MCP_STDIO[1]}"

mcp_call() {
  local request="$1"
  local response
  printf '%s\n' "$request" >&"$mcp_stdin_fd"
  IFS= read -r -t 10 response <&"$mcp_stdout_fd" || return 1
  printf '%s\n' "$response"
}

mcp_initialize_response="$(mcp_call '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"release-artifact-verifier","version":"1"}}}')"
printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}' >&"$mcp_stdin_fd"
mcp_tools_response="$(mcp_call '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')"
mcp_capabilities_response="$(mcp_call '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"netratel_capabilities","arguments":{"operation":"get"}}}')"
kill "$MCP_STDIO_PID" 2>/dev/null || true
wait "$MCP_STDIO_PID" 2>/dev/null || true

if ! printf '%s\n%s\n%s\n' "$mcp_initialize_response" "$mcp_tools_response" "$mcp_capabilities_response" | jq -se '
  length == 3
  and (map(.id) | sort == [1, 2, 3])
  and (map(select(.id == 1))[0] | .jsonrpc == "2.0"
    and .result.serverInfo.name == "NetRatel.Mcp"
    and (.result.capabilities.tools | type == "object"))
  and (map(select(.id == 2))[0].result.tools | type == "array" and length > 0)
  and map(select(.id == 3))[0].result.structuredContent.success == true
' >/dev/null; then
  echo "stdio MCP archive did not initialize, list tools, and complete its capabilities read." >&2
  exit 1
fi

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
