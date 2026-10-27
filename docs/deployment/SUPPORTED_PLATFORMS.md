# Supported platforms

The release workflows build the targets below from the version in
`Directory.Build.props`. Download the matching archives and publication record
from a completed [GitHub release](https://github.com/BostonTechnologies/netratel/releases).

| Component | Release target | Release verification | Runtime requirements |
| --- | --- | --- | --- |
| Web, API, migrations, HTTP MCP and Client images | Linux x64 containers | Image builds, source/version labels, layer scans, immutable digests and anonymous pulls. | Docker; the server bundle uses Docker Compose. |
| CLI | Linux x64 | Archive help/version, local .NET tool installation and scoped API read. | .NET 10; the archive and tool are framework-dependent. |
| stdio MCP and HTTP bridge | Linux x64 | Packaged protocol initialization, tool listing, configuration failure and scoped API read. | .NET 10; run as a local MCP host process with an explicit profile. |
| stdio MCP and HTTP bridge | Windows x64 | Packaged protocol and protected Windows launcher checks, including encrypted profile migration. | .NET 10; the protected launcher requires PowerShell 7.4 or later. |
| Native Client | Linux x64 | Matching-runner build and archive/manifest/sidecar integrity checks. | Self-contained; includes the Linux native PTY helper. |
| Native Client | Windows x64 | Matching-runner build and archive/manifest/sidecar integrity checks. | Self-contained; use the approved Windows installer or archive. |
| Native Client | macOS arm64 | Matching-runner build and archive/manifest/sidecar integrity checks. | Self-contained; use the approved macOS installer or archive. |

The documented stack requires Docker Compose plus bundled or external
PostgreSQL, and deployment-managed persistent Data
Protection and agent-signing material. Fresh local-account setup does not
require OIDC or SMTP; OIDC is required only for a deliberately configured
OIDC/hybrid deployment or the external-OIDC HTTP MCP mode. CLI and stdio MCP
archives require the .NET 10 runtime.

Separate Windows/macOS CLI archives, macOS stdio MCP archives, additional native
Client runtime IDs and multi-architecture images are outside this distribution
matrix. Native installation, updater execution, browser and deployment/upgrade
checks are available through the optional integration workflow. See [self-hosting](SELF_HOSTING.md),
[CLI and MCP](../integrations/CLI_AND_MCP.md), and [release engineering](RELEASES.md) for the
deployment and validation procedures.
