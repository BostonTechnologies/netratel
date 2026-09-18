# CLI and MCP

The CLI, stdio MCP server, and HTTP MCP service are separate product
interfaces. They use the API's authorization boundaries and should receive
least-privilege credentials appropriate to their caller.

The stdio MCP server is intended for a local MCP host process. HTTP MCP is an
optional separately deployed service; it is not required for normal Web/API or
native Client operation. Do not expose either interface publicly until its OIDC
resource, allowed operations, and network policy have been configured.

HTTP MCP requires HTTPS OIDC discovery metadata by default. The
`NetRatel:Mcp:Http:RequireHttpsMetadata=false` setting is accepted only by a
Development host for disposable local integration tests; it must never be used
for a deployed instance.

Release archives are not published for `0.1.0-rc.2` yet. The rc.1 tag and
archives are historical artifacts; build from reviewed
source for evaluation and verify the archive checksum and client manifest once
a future owner-approved release exists.

## CLI configuration

The Linux x64 CLI review archive and the local `NetRatel.Cli` .NET tool package
are framework-dependent and require the .NET 10 runtime. Configure either
with a JSON file or the following environment
variables: `NETRATEL_CLI_API_BASE_URL`, `NETRATEL_CLI_OIDC_TOKEN_URL`,
`NETRATEL_CLI_OIDC_CLIENT_ID`, `NETRATEL_CLI_OIDC_USERNAME`,
`NETRATEL_CLI_OIDC_APP_PASSWORD`, and optionally
`NETRATEL_CLI_OIDC_SCOPE`. Command-line options take precedence over those
values. The legacy `BT_*` variable names are compatibility aliases only; new
automation must use the `NETRATEL_CLI_*` names.

The release rehearsal currently packages and validates Linux x64 for the CLI
and stdio MCP. Other CLI/MCP runtime archives are not advertised until their
matching runners build and smoke-test them.

## stdio MCP configuration

The stdio MCP server requires an explicit absolute configuration-file path in
`NETRATEL_MCP_CONFIG`; it never reads ambient CLI or AgentClient credential
variables. The legacy mixed-case `NetRatel_MCP_CONFIG` name is a
compatibility-only alias. Keep the JSON configuration file private and provide
only least-privilege credentials for its target API.

For a machine-to-machine client-credentials flow, create a private file such
as `/etc/netratel/mcp.json` with deployment-specific values:

```json
{
  "apiBaseUrl": "https://netratel.example.invalid",
  "apiM2MTokenUrl": "https://netratel.example.invalid/connect/token",
  "apiM2MClientId": "replace-with-a-least-privilege-client-id",
  "apiM2MClientSecret": "supply-through-your-secret-mechanism",
  "apiM2MScope": "netratel.api"
}
```

Protect that file with owner-only access. From an extracted Linux x64 review
archive, launch the server with:

```sh
NETRATEL_MCP_CONFIG=/etc/netratel/mcp.json \
  dotnet /path/to/netratel-mcp-linux-x64/NetRatel.Mcp.dll
```

The stdio protocol is written only to standard output; operational diagnostics
are written to standard error. Configure the MCP host to supply the file path
and to restart the process when credentials or the target configuration change.
