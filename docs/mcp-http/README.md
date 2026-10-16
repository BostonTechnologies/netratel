# HTTP MCP operator guide

HTTP MCP lets Netclaw and other MCP clients use the NetRatel API. First connect the MCP server to the platform once, then give each client a scoped credential for that endpoint. Server/client modes describe the two sides of the connection, not home/company use or tenant count. The standard deployment exposes `/mcp` at the trusted Web HTTPS origin while its listener remains private. Ordinary Web/API and native Client operation does not require MCP.

- [Set up the server, then connect clients](setup.md): start here. Explains the modes, prerequisites, externally managed Compose and supplied release-bundle paths, apply/resume and actual verification.
- [Give Netclaw customer connections access](../integrations/netclaw-http-mcp.md): supported HTTP transport, protected credential storage and customer isolation checks after server setup.
- [Host setup and recovery reference](hosting-reference.md): deployment ownership, alternate launch contexts, storage, headless generation and rotation.
- [Configuration reference](configuration.md): every host, outbound, delegation and Compose setting, defaults, secret-file mappings and precedence.
- [Authentication modes](authentication.md): local integration credentials, browser client approval and explicit external OIDC.
- [Local credential mode](local-credential-mode.md): the API-bound credential/delegation flow.
- [Troubleshooting and verification](troubleshooting.md): configuration, TLS, routing, authentication, initialize and one permitted read-only call.
- [Web/YARP routing](../deployment/web-gateway.md): generated defaults and manual JSON/environment overrides.
- [CLI and stdio MCP](../integrations/CLI_AND_MCP.md): separate tooling entry points and isolated identities.
- [Release installation](../../release/INSTALL.md): verified image bundle and first-run Web/API setup.

[The production activation manifest](production-activation-manifest.yml) is an existing owner acceptance record template for OIDC deployments. It is not runtime configuration or evidence that production checks passed. Local mode has no OAuth metadata or OIDC authority; use the applicable mode's procedures.
