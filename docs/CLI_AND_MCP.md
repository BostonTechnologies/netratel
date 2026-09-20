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

The rc.1 tag and archives are historical artifacts. Obtain rc.2 tooling only
from its matching prerelease, verify `SHA256SUMS` before extraction, and keep
the archive together with its SBOM and provenance record. A source checkout is
appropriate for development rather than a substitute for a promoted release
bundle.

## Integration credentials

An authenticated operator can create a least-privilege credential at
`/account/integration-credentials`. Select one tenant and one permission for
each grant, a bounded expiry, and an explicit purpose. The generated secret
has the `nrt_ic_` prefix, is displayed once, and must be stored in an
owner-only secret mechanism; NetRatel retains only a one-way verifier.

`API / CLI / stdio MCP` credentials are accepted by the business API boundary.
`HTTP MCP` credentials are deliberately not accepted there: P08 pairs them to
the canonical HTTP MCP gateway and delegation resource. Neither purpose is an
agent enrollment code, native Client token, browser session, or a substitute
for an external OIDC credential.

For a current API-purpose credential, use a secret-injection mechanism rather
than a command-line argument:

```sh
export NETRATEL_INTEGRATION_TOKEN="read-from-your-secret-store"
curl --fail-with-body \
  -H "Authorization: Bearer ${NETRATEL_INTEGRATION_TOKEN}" \
  https://netratel.example.invalid/api/v2/agents/42/00000000-0000-0000-0000-000000000000/telemetry
unset NETRATEL_INTEGRATION_TOKEN
```

The credential cannot exceed the owner’s current tenant permission, is
rechecked against current membership/role state at use, and stops working on
expiry, revocation, or local-account disablement. Revoke it from the same
account page; rotation is create a replacement, update its consumer, then
revoke the old credential.

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

For local API-purpose integration credentials, set exactly one explicit mode:
`NETRATEL_CLI_API_BASE_URL` plus `NETRATEL_CLI_INTEGRATION_TOKEN` (the
previously documented `NETRATEL_INTEGRATION_TOKEN` is accepted as a fallback).
Do not set any `NETRATEL_CLI_OIDC_*` value at the same time; NetRatel rejects
an ambiguous configuration before making a network request. The CLI never
falls back to OIDC after a local credential is rejected. `netratel auth
whoami` shows the chosen mode, target, and a non-secret credential prefix;
`netratel auth token` deliberately refuses to print an integration credential.
Avoid `config set` for local credentials because command-line arguments can be
recorded by a shell. Use environment injection or an owner-readable config
file instead.

The release rehearsal currently packages and validates Linux x64 for the CLI
and stdio MCP. Other CLI/MCP runtime archives are not advertised until their
matching runners build and smoke-test them.

## stdio MCP configuration

The stdio MCP server requires an explicit absolute configuration-file path in
`NETRATEL_MCP_CONFIG`; it never reads ambient CLI or AgentClient credential
variables. The legacy mixed-case `NetRatel_MCP_CONFIG` name is a
compatibility-only alias. Keep the JSON configuration file private and provide
only least-privilege credentials for its target API.

The stdio server remains isolated from CLI and AgentClient environment
credentials. To use an integration credential, place it only in that explicit
owner-readable file; omit every `oidc*` field:

```json
{
  "apiBaseUrl": "https://netratel.example.invalid",
  "integrationCredential": "read-from-your-secret-store"
}
```

An isolated stdio configuration that contains both `integrationCredential` and
any `oidc*` value is invalid. The MCP process sends JSON-RPC only on stdout;
diagnostics remain on stderr and never include the complete credential.

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
