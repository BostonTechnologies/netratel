# NetRatel documentation

Install the verified image bundle using [release installation](../release/INSTALL.md), then follow [self-hosting](deployment/SELF_HOSTING.md) and [first-run setup](deployment/FIRST_RUN_SETUP.md). For a single public HTTPS origin, use the [Web/YARP gateway guide](deployment/web-gateway.md). Optional HTTP MCP has its own [configuration and setup](mcp-http/README.md).

| Category | Guides |
| --- | --- |
| [Deployment](deployment/README.md) | Installation, configuration, Web routing, branding, platform support and release engineering |
| [CLI and integrations](integrations/README.md) | CLI/stdio MCP, isolated profiles, RatelDesk, notifications and Netclaw |
| [HTTP MCP](mcp-http/README.md) | Hosting, authentication, browser client approval, configuration and troubleshooting |
| [Native Client](client/README.md) | Installation, inventory, native gateway transport, renewal and recovery |
| [Operations](operations/README.md) | Permissions, monitoring, flows, services and troubleshooting |
| [Contracts](contracts/README.md) | API reference and versioned integration protocols |
| [Architecture decisions](adr/README.md) | Durable design decisions |
| [Implementation](implementation/README.md) | Architecture, development and dated investigations |
| [Evidence](evidence/README.md) | Historical execution and measurement receipts |
| [Release history](release-notes/README.md) | Notes for specific historical releases |
| [Assets](assets/README.md) | Brand assets and distribution references |

Current instructions and historical evidence have separate homes. [The move map](implementation/documentation-move-map.md) identifies canonical replacements for retired docs-root paths.

Only this index and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) remain directly under `docs/`. The notice is the sole exception because build/publish packaging distributes it. New guides belong inside the closest category; repository policy is in [AGENTS.md](../AGENTS.md).
