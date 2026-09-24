# Supported platforms

The table below records the targets that the release workflow builds and
executes for the version in `Directory.Build.props`. It is not a production
certification statement.

| Component | Release target | Rehearsal evidence | Notes |
| --- | --- | --- | --- |
| Web, API, migrations, HTTP MCP | Linux x64 containers | Final image builds and image-only Compose smoke are required in CI. | No multi-architecture image is advertised for rc.1. |
| CLI | Linux x64 with .NET 10 | Archive help/version, local .NET tool installation, and scoped API read are checked. | The CLI archive and tool are framework-dependent. |
| stdio MCP | Linux x64 with .NET 10 | Packaged protocol initialization, tool listing, configuration failure, and scoped API read are checked. | Run it as a local MCP host process using an explicit configuration file. |
| Native Client | Linux x64 | The final package runs a native PTY self-test. | The package includes the Linux native PTY helper. |
| Native Client | Windows x64 | The matching Windows runner publishes the package and runs its executable version probe. | Install only from the approved artifact/manifest once a release exists. |
| Native Client | macOS arm64 | The matching macOS runner publishes the package and runs its executable version probe. | Install only from the approved artifact/manifest once a release exists. |

The documented stack requires Docker Compose plus bundled or external
PostgreSQL, and deployment-managed persistent Data
Protection and agent-signing material. Fresh local-account setup does not
require OIDC or SMTP; OIDC is required only for a deliberately configured
OIDC/hybrid deployment or the external-OIDC HTTP MCP mode. CLI and stdio MCP
archives require the .NET 10 runtime.

Windows/macOS CLI or stdio MCP archives, other native Client runtime IDs,
multi-architecture server images, distributed topologies, and production
certification are not advertised for rc.1. See [self-hosting](SELF_HOSTING.md),
[CLI and MCP](CLI_AND_MCP.md), and [release engineering](RELEASES.md) for the
tested deployment and release boundaries.
