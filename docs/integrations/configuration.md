# Shared tooling configuration

CLI, stdio MCP and HTTP MCP outbound execution share the configuration resolver and persistence rules. Each surface keeps its own identity namespace and explicitly selected profile/file. CLI credentials are never silently imported into MCP; an HttpMcp ingress bearer is never the HTTP host's outbound API identity.

## Precedence and explicit selection

Effective values use this order, from lowest to highest:

1. Built-in defaults.
2. An explicitly selected configuration/profile JSON file.
3. The current surface's dedicated environment variables.
4. Explicit supported non-secret command options.

Canonical typed keys win over documented compatibility aliases **within the same provider/precedence level**. A dedicated environment alias overrides a canonical value from a lower-precedence selected JSON file. An absent command option does not override a configured value. Empty environment values explicitly clear an override; authentication must still resolve to one coherent mode. Configuration files reject duplicate or unknown properties instead of guessing. Outbound configuration has scalar fields, so it has no implicit list merging.

An explicitly named missing or invalid file fails. Authentication failure never falls back to a different mode, profile, instance or application's environment. Review the selected target and source diagnostics before making a network request. Changing an approved origin/target or bound OIDC/M2M token endpoint requires an explicit validated rebind or new approval; do not send an old credential to a new server by editing its URL. CLI and HTTP MCP outbound clients disable automatic redirects. A redirect is an actionable target/issuer configuration result; diagnose it and review the new HTTPS endpoint explicitly instead of replaying credentials to another origin.

`NETRATEL_MCP_CONFIG` selects an isolated MCP JSON file; legacy `NetRatel_MCP_CONFIG` is compatibility-only. Environment-only MCP configuration uses the dedicated `NetRatel:Mcp:Outbound` section and `NETRATEL_MCP_OUTBOUND_*` aliases. Neither stdio nor HTTP MCP imports `NETRATEL_CLI_*`, `NETRATEL_INTEGRATION_TOKEN`, ambient AgentClient credentials or the default CLI profile.

## Environment namespaces

For CLI, canonical properties are under `NetRatel:Cli`; for stdio/HTTP MCP outbound, they are under `NetRatel:Mcp:Outbound`. In environment variables, replace section separators with `__`, for example `NetRatel__Mcp__Outbound__ApiBaseUrl`.

| Property | Use |
| --- | --- |
| `ApiBaseUrl` | Explicit API target |
| `AuthenticationMode` | `IntegrationCredential`, `Oidc`, or `ApiM2MClientSecret`; credentials must match the chosen mode. HTTP host outbound accepts only its OIDC application or M2M service identity |
| `IntegrationCredential` / `IntegrationCredentialFile` | API-purpose credential inline or by protected file reference |
| `OidcTokenUrl`, `OidcClientId`, `OidcUsername` | Existing explicit OIDC password/client settings |
| `OidcAppPassword` / `OidcAppPasswordFile`, `OidcScope` | OIDC secret source and optional scope |
| `ApiM2MTokenUrl`, `ApiM2MClientId` | Existing machine-to-machine token endpoint and service client ID |
| `ApiM2MClientSecret` / `ApiM2MClientSecretFile`, `ApiM2MScope` | M2M secret source and scope |
| `ProfileName`, `Instance`, `DefaultTenantId` | Named profile, explicitly selected surface and supported tenant default |
| `CredentialPurpose`, `ApprovedOrigin`, `ApprovedTarget`, `ApprovedTokenEndpoint` | Credential's reviewed purpose, target and OIDC/M2M issuer-endpoint binding |

Dedicated aliases use the property's upper snake case with prefix `NETRATEL_MCP_OUTBOUND_`, or the established `NETRATEL_CLI_` prefix for CLI. Integration credential aliases use `INTEGRATION_TOKEN` / `INTEGRATION_TOKEN_FILE`. CLI retains its existing `NETRATEL_INTEGRATION_TOKEN` fallback and `BT_*` aliases for compatibility; those aliases are CLI-only. New deployment examples should use canonical typed keys or documented dedicated names.

See the [HTTP MCP reference](../mcp-http/configuration.md) for all ingress, outbound, selected-target and delegation mappings. Native Client precedence is a different installed-service contract, documented in [deployment configuration](../deployment/CONFIGURATION.md#native-client-configuration-precedence).

Named profiles live under `~/.config/netratel/profiles/{surface}/{safe-name}.json`, with separate `cli`, `stdiomcp`, `httpmcpoutbound` and `httpmcpclient` directories. Select `ProfileName` in the current namespace or its dedicated `PROFILE_NAME` alias. HTTP MCP clients have the separate `NetRatel__Mcp__Client__` / `NETRATEL_MCP_CLIENT_` namespace.

Supported HTTP MCP containers set `NETRATEL_CLIENT_DIRECTORY=/var/lib/netratel/clients` on a writable persistent personal-data volume. Client profile defaults use `{client-directory}/profiles/{surface}/{safe-name}.json`; this root takes precedence over the legacy `NETRATEL_SETUP_DIRECTORY` fallback. Explicit `--config`, selected profile/file and dedicated client environment settings keep their precedence. Personal defaults never select the host's outbound configuration. A missing durable container mount produces the concrete `compose.setup.yaml ... mcp-http setup --target http-client` remediation before approval/save, rather than failing later under `/app/.config`. See [personal setup and container recovery](../mcp-http/setup.md#persistent-client-storage-and-an-existing-container).

## Files and secret references

Existing isolated JSON files remain supported. For new files, prefer protected references over embedding credential material:

```json
{
  "apiBaseUrl": "https://netratel.example.invalid",
  "authenticationMode": "IntegrationCredential",
  "integrationCredentialFile": "/run/secrets/netratel-api-credential"
}
```

Select exactly one source for a secret at the effective precedence level: inline value or file/reference. A higher-level secret-file reference replaces a lower-level inline secret, and a higher-level inline secret replaces a lower-level reference; two alternatives at the same level are rejected. `.NET` does not infer `_FILE` behavior; only the explicitly implemented aliases/properties have that meaning. Configuration files are bounded to 64 KiB and secret files to 16 KiB; only final CR/LF is trimmed. Missing, empty, oversized, insecure or symlinked secret files fail with a redacted actionable error. Files must be readable by the serving identity, not public or writable by unrelated users. Container secret storage requires no desktop keyring.

Saving setup uses restrictive permissions and atomic writes to an explicitly selected writable path. Cancellation retains the previous profile. Mounted read-only configuration and injected environment fields are externally managed: setup can export reviewed files but cannot edit a parent shell, replace immutable Docker secrets, or alter a running container's environment. Back up profile metadata and secret storage separately from server Data Protection/native signing/delegation keys.

## Validate and apply

Local validation checks structure, target/purpose binding, one auth mode and secret references without creating credentials or executing business operations. Redacted diagnostics show target/auth mode, source and selected paths; they never print credentials or the whole environment. Connectivity and authenticated read-only checks are separate and can be unavailable because of DNS, TLS, grants or policy.

After changing container environment, apply the reviewed configuration and recreate the affected containers. A simple restart retains their old environment. Tooling configuration is read at process startup; restart the CLI/MCP process after changing its selected profile or secret reference. Web's supported YARP file reload is described in [Web gateway routing](../deployment/web-gateway.md); it does not reload other services' options.

See [CLI and stdio MCP](CLI_AND_MCP.md) for explicit setup and ordinary execution, and [HTTP MCP setup](../mcp-http/setup.md) for the separate administrator host workflow.
