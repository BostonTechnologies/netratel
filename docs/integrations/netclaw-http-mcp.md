# Give Netclaw access to NetRatel over HTTP MCP

First [connect the HTTP MCP server to the NetRatel platform](../mcp-http/setup.md). Complete that server's **end-to-end verified** check before configuring Netclaw. You configure hosting once for the deployment, then create a separate client credential for each customer connection.

```text
Netclaw customer connections → NetRatel HTTP MCP → NetRatel API
  customer-specific credentials   server service identity
```

This guide configures the first arrow. NetRatel's `setup --mode host` configures the second. Bare `dotnet NetRatel.Mcp.Http.dll setup` saves a NetRatel HTTP-client profile for the machine running it; it does not install a connection in Netclaw. Neither mode means home versus company or single tenant versus multiple tenants.

The steps below follow Netclaw's documented [HTTP transport](https://netclaw.dev/configuration/mcp-servers/), [MCP CLI](https://netclaw.dev/cli/mcp-tools/) and [encrypted secret store](https://netclaw.dev/cli/secrets/). Run the commands on the **Netclaw machine**, as the operating-system user that runs its daemon, or inside that daemon's container with its persistent home mounted. A separate shell user has a separate configuration and key ring.

## 1. Create one credential per customer

For a NetRatel deployment using **Local credential** authentication, open **Admin → Integration → Integration credentials** in NetRatel. Choose **Create integration**, connection type **HTTP MCP gateway**, and the canonical server URL shown by completed server setup, usually `https://netratel.example.invalid/mcp`.

Create these independently:

| Integration and Netclaw connection | MCP URL | NetRatel grants |
| --- | --- | --- |
| `customer1-netclaw` | The same canonical `/mcp` URL | Customer 1 only |
| `customer2-netclaw` | The same canonical `/mcp` URL | Customer 2 only |
| `customer3-netclaw` | The same canonical `/mcp` URL | Customer 3 only |

Set a bounded expiry and select only the required tenant permissions and client targets. For an initial read test, grant `telemetry.read` for that customer; grant further permissions only for intended operations. Leave server-level permissions unselected unless that connection needs them. Review the tenant IDs as well as their names.

Keep each returned credential in a protected password manager until it is installed below. It is shown once. An **API / CLI / stdio MCP** credential has a different purpose and cannot replace an **HTTP MCP** credential. The host's backend service secret and delegation key never belong in Netclaw.

Connection names label the entries. NetRatel enforces isolation through each credential's grants on every operation. A default tenant or a prompt naming a customer does not restrict another credential that has broader grants.

## 2. Register the HTTP connections in Netclaw

**Netclaw machine:** replace the example URL with the endpoint from completed server setup. NetRatel uses Netclaw's `http` transport, which is Streamable HTTP.

```sh
netclaw mcp add customer1-netclaw https://netratel.example.invalid/mcp --transport http
netclaw mcp add customer2-netclaw https://netratel.example.invalid/mcp --transport http
netclaw mcp add customer3-netclaw https://netratel.example.invalid/mcp --transport http
```

**Netclaw machine, Bash terminal:** store each bearer through Netclaw's encrypted secret store. The prompt hides your paste and the command entered into shell history contains only a variable reference. Keep shell tracing disabled. Run this block separately for each customer, changing both `customer1` occurrences to the matching customer before pasting its credential.

```bash
set +x
IFS= read -r -s -p 'Paste customer1 HTTP MCP credential: ' customer_credential
printf '\n'
netclaw secrets set McpServers.customer1-netclaw.Headers.Authorization "Bearer $customer_credential"
unset customer_credential
```

Netclaw stores secret values encrypted in `~/.netclaw/config/secrets.json`; preserve `~/.netclaw/keys/` with it. Endpoint and transport metadata live in `~/.netclaw/config/netclaw.json`. Use Netclaw's protected secret-management equivalent for your deployment manager; do not put the bearer in a URL, Compose editor, plaintext configuration, chat, or committed example. A NetRatel profile saved in the MCP container is a separate consumer and supplies none of this configuration automatically.

## 3. Grant Netclaw tools, then verify customer boundaries

**Netclaw machine:**

```sh
netclaw daemon restart
netclaw mcp list
netclaw mcp permissions
```

The daemon must run for discovery and the permissions screen. New Netclaw entries start with empty tool grants. In **mcp permissions**, enable each intended server and grant only the tools needed by the intended audience; retain approval for operations requiring an operator decision. Netclaw's **Personal**, **Team** and **Public** audiences control who may use a tool. They are separate from NetRatel tenant grants. A shared audience that can select all three credentials can access all three customers through those entries; give customer users/sessions only their permitted server entries through Netclaw's supported audience/deployment isolation.

Save, restart the daemon again to load permission changes, and inspect:

```sh
netclaw daemon restart
netclaw mcp list
netclaw mcp get customer1-netclaw
netclaw status
```

`mcp get` redacts credentials. **Connected (N tools)** establishes transport and catalog discovery. For each customer, inspect `netratel_access` `whoami`, then use the discovered `netratel_search` tenant/client schemas to resolve an allowed tenant ID and AgentId. Run one permitted client read, such as `netratel_clients` `get` with those exact IDs on the operator surface. Then attempt that read for another customer's tenant/client using the same connection: it must be denied without returning that customer's data. Repeat for all three connections, and after recreating the deployment. Record successful reads and expected denials separately. A prompt, connection name, catalog or generic HTTP 200 is insufficient evidence of tenant isolation.

Revoke one test credential in NetRatel and confirm that its connection can no longer execute; leave the other two active. Replace that credential securely if the connection should remain available. Removing local Netclaw configuration does not revoke its NetRatel credential.

## External OIDC deployments

Retain the server's configured OIDC mode. Local `nrt_ic_` credentials apply only to Local credential mode. For OIDC, register each HTTP entry using the provider's actual client ID/scopes where required, then run `netclaw mcp auth <connection-name>` and complete the provider's browser flow. Follow the provider's redirect registration and Netclaw's [OAuth instructions](https://netclaw.dev/configuration/mcp-servers/#oauth-flow). NetRatel browser integration approval is a different protocol and does not issue an OIDC token. Complete the same permitted/denied tenant reads after OIDC authentication.

## NetRatel AI Ops is a separate connection

Pairing NetRatel's AI Ops workspace to the Netclaw daemon enables chat and investigation transport in the other direction. Its device token does not grant NetRatel tool access. Use [Netclaw AI operations](netclaw-ai-operations.md) for that optional pairing.

Repository tests and disposable deployment checks do not establish that a particular Netclaw daemon or owner deployment has passed these steps. Complete the checks above against the installed versions and retain their actual results.
