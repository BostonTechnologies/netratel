# NetRatel

[Latest release](https://github.com/BostonTechnologies/netratel/releases/latest) ·
[All releases](https://github.com/BostonTechnologies/netratel/releases) ·
[Installation guide](release/INSTALL.md)

<img src="docs/assets/netratel/netratel-readme-hero.webp" width="960" alt="NetRatel — secure, connected automation" />

NetRatel is a self-hosted application for managing computers and automating
work across them. Connect machines, view their status, run scripts and jobs,
open remote terminals, browse files, and inspect logs from one web interface.

Built with .NET and Akka.NET for real-time coordination, NetRatel includes an
API, command-line tools, a native Client/agent, and optional Model Context
Protocol (MCP) integrations. AI services are optional and are not required for
normal operation.

Published releases include native Client archives, CLI and MCP tooling, and a
Compose bundle with verified image digests. Choose a completed release and use
its matching downloads, checksums and installation instructions.

## Components

- **Web** provides the browser interface and authenticated operator sessions.
- **API** owns durable PostgreSQL state, authorization, and the agent gateway.
- **CLI**, **stdio MCP**, and **HTTP MCP** provide separately authorized
  automation interfaces.
- **Client** is the native agent installed on managed machines.
- **RatelDesk connector** provides tenant-scoped protected configuration and
  preview for monitoring flows; see [Monitoring flows](docs/operations/FLOWS.md).

## Self-hosting

[Choose login method: Local, SSO, or both](docs/deployment/CONFIGURATION.md#choose-login-method).

PostgreSQL is the only supported application and identity database.
Persist Data Protection and agent-signing material outside the container
filesystem. A fresh local installation can use its built-in local accounts with
no external identity provider; configure OIDC only for an optional OIDC or
hybrid deployment. Supply all secrets at deployment time; the tracked
configuration contains only reserved example values.

Read [self-hosting](docs/deployment/SELF_HOSTING.md) before deployment. Obtain the public
release bundle from a completed [GitHub release](https://github.com/BostonTechnologies/netratel/releases)
and verify its checksums. The bundle supplies immutable image digests after
publication has completed. See [supported platforms](docs/deployment/SUPPORTED_PLATFORMS.md)
for the current distribution targets and runtime requirements.

Useful references: [architecture](docs/implementation/ARCHITECTURE.md),
[configuration](docs/deployment/CONFIGURATION.md), [CLI and MCP](docs/integrations/CLI_AND_MCP.md),
[Native Client](docs/client/CLIENT.md), [Services inventory](docs/operations/SERVICES.md), [Monitoring](docs/operations/MONITORING.md), [Monitoring flows](docs/operations/FLOWS.md),
[development and testing](docs/implementation/DEVELOPMENT.md),
[troubleshooting](docs/operations/TROUBLESHOOTING.md), and
[release engineering](docs/deployment/RELEASES.md). The generated [API reference](docs/contracts/API_REFERENCE.md)
is available at `/api/docs/` on a running deployment.

## Development

The repository pins its SDK in [global.json](global.json). Restore, build, and
test with:

```sh
dotnet restore NetRatel.sln
dotnet build NetRatel.sln --configuration Release --no-restore
bash tools/ci/run-fast-regressions.sh TestResults/fast
```

`tools/ci/verify-product-version.sh` verifies that all first-party projects
evaluate to the release-manifest version.

Required CI runs fast unit, component and focused API/PostgreSQL regressions
after one restore/build. Full product-pair checks, physical incidents, native
client execution, browser acceptance and deployment/upgrade checks are opt-in
through the manual integration workflow. See [release validation](docs/deployment/RELEASES.md)
for selecting one functional suite and interpreting pending owner acceptance.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md),
and [SECURITY.md](SECURITY.md). NetRatel is licensed under Apache-2.0; see
[LICENSE](LICENSE) and [NOTICE](NOTICE).

Browse the [documentation index](docs/README.md) for categorized operator, integration, HTTP MCP and historical guides.
