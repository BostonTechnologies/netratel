#!/usr/bin/env bash
# Shared by the release job and the explicitly selected Windows branch rehearsal.
set -euo pipefail

[[ "${RUNNER_OS:-}" == Windows ]] || { echo 'Windows MCP packaging requires a hosted Windows runner.' >&2; exit 2; }
[[ "${VERSION:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]] || { echo 'A release version is required.' >&2; exit 2; }
[[ "${PublicSourceRevision:-}" =~ ^[0-9a-f]{40}$ ]] || { echo 'A verified source revision is required.' >&2; exit 2; }
[[ "$(git rev-parse HEAD)" == "$PublicSourceRevision" ]] || { echo 'The package source differs from its recorded revision.' >&2; exit 2; }
[[ "$(python3 tools/ci/product-version.py)" == "$VERSION" ]] || { echo 'The package version differs from the source.' >&2; exit 2; }

distribution=artifacts/netratel-mcp-win-x64
[[ ! -e "$distribution" && ! -e "artifacts/netratel-mcp-stdio-$VERSION-win-x64.zip" ]] || { echo 'Use a fresh package destination.' >&2; exit 2; }
dotnet restore src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj --runtime win-x64
dotnet publish src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj --configuration Release --no-restore --runtime win-x64 --self-contained false --output "$distribution"
cp LICENSE NOTICE "$distribution/"
cp tools/client/http-mcp-windows.ps1 tools/client/HttpMcpProcessRelay.cs "$distribution/"
cp docs/integrations/CLI_AND_MCP.md docs/integrations/http-mcp-bridge.md "$distribution/"
python3 tools/ci/prepare-bundle-documentation.py --bundle "$distribution" --commit "$PublicSourceRevision" \
  --document CLI_AND_MCP.md=docs/integrations/CLI_AND_MCP.md \
  --document http-mcp-bridge.md=docs/integrations/http-mcp-bridge.md
python3 tools/ci/generate-runtime-sbom.py \
  --project-assets src/NetRatel/NetRatel.Mcp/obj/project.assets.json \
  --distribution "$distribution" --runtime win-x64 --name NetRatel-MCP-win-x64 \
  --version "$VERSION" --output "artifacts/netratel-mcp-stdio-$VERSION-win-x64.spdx.json"
pwsh -NoLogo -NoProfile -NonInteractive -Command 'Compress-Archive -Path "artifacts/netratel-mcp-win-x64" -DestinationPath "artifacts/netratel-mcp-stdio-$env:VERSION-win-x64.zip"'
(
  cd artifacts
  sha256sum "netratel-mcp-stdio-$VERSION-win-x64.zip" "netratel-mcp-stdio-$VERSION-win-x64.spdx.json" > SHA256SUMS
)
python3 tools/ci/verify-windows-mcp-archive.py --artifacts artifacts --version "$VERSION"
