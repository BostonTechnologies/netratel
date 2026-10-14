# HTTP MCP operator guide

HTTP MCP is an optional bearer-authenticated service. The standard deployment exposes it at the same trusted public HTTPS origin as Web, under `/mcp`, while its listener remains private. Ordinary Web/API and native Client operation does not require MCP.

- [Deploy and set up](setup.md): coordinated Web/API/MCP configuration, explicit administrator setup, persistent mounts and migration.
- [Configuration reference](configuration.md): every host, outbound, delegation and Compose setting, defaults, secret-file mappings and precedence.
- [Authentication modes](authentication.md): local integration credentials, browser client approval and explicit external OIDC.
- [Local credential mode](local-credential-mode.md): the API-bound credential/delegation flow.
- [Troubleshooting and verification](troubleshooting.md): configuration, TLS, routing, authentication, initialize and one permitted read-only call.
- [Web/YARP routing](../deployment/web-gateway.md): generated defaults and manual JSON/environment overrides.
- [CLI and stdio MCP](../integrations/CLI_AND_MCP.md): separate tooling entry points and isolated identities.
- [Release installation](../../release/INSTALL.md): verified image bundle and first-run Web/API setup.

[The production activation manifest](production-activation-manifest.yml) is an existing owner acceptance record template for OIDC deployments. It is not runtime configuration or evidence that production checks passed. Local mode has no OAuth metadata or OIDC authority; use the applicable mode's procedures.
