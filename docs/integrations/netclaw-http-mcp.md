# Give Netclaw access to NetRatel over HTTP MCP

First [connect the HTTP MCP server to the NetRatel platform](../mcp-http/setup.md). Complete that server's **end-to-end verified** check before configuring Netclaw. You configure hosting once for the deployment, then create a separate client credential for each customer connection.

```text
Netclaw customer connections → NetRatel HTTP MCP → NetRatel API
  customer-specific credentials   server service identity
```

This guide configures the first arrow. NetRatel's `setup --mode host` configures the second. Bare `dotnet NetRatel.Mcp.Http.dll setup` saves a NetRatel HTTP-client profile for the machine running it; it does not install a connection in Netclaw. Neither mode means home versus company or single tenant versus multiple tenants.

The Netclaw steps follow its documented [installation](https://netclaw.dev/getting-started/installation/), [HTTP transport](https://netclaw.dev/configuration/mcp-servers/), [MCP CLI](https://netclaw.dev/cli/mcp-tools/) and [encrypted secret store](https://netclaw.dev/cli/secrets/). Run **Netclaw commands** on the Netclaw machine as the operating-system user that runs its daemon, or inside that daemon's container with its persistent home mounted. A separate shell user has a separate configuration and key ring. The NetRatel policy block has its own administrator-machine instructions.

A **NetRatel credential owner** prepares each customer connection, and an independent **NetRatel policy administrator** reviews its access through the supported reviewed workflow in step 2. The **Netclaw administrator** securely installs the resulting customer credential in steps 3–4. The requesting identity cannot approve its own reviewed request. The optional advanced policy-administration procedure requires its own established administrator authority.

## 1. Create one credential per customer

**NetRatel administrator:** for a deployment using **Local credential** authentication, open **Admin → Integration → Integration credentials** in NetRatel. Choose **Create integration**, connection type **HTTP MCP gateway**, and the canonical server URL shown by completed server setup, usually `https://netratel.example.invalid/mcp`.

Create these independently:

| Integration and Netclaw connection | MCP URL | NetRatel grants |
| --- | --- | --- |
| `customer1-netclaw` | The same canonical `/mcp` URL | Customer 1 only |
| `customer2-netclaw` | The same canonical `/mcp` URL | Customer 2 only |
| `customer3-netclaw` | The same canonical `/mcp` URL | Customer 3 only |

For the first connection check below, select **Read flows** (`flow.read`) for exactly that customer tenant and **Discover MCP capabilities** (`mcp.discovery.read`) under server-level access. Set a bounded expiry. Leave other permissions unselected; additional operations need their own reviewed grants and server policy. This first read uses no agent target and changes no flows. Review the tenant ID as well as its name.

Keep each returned credential in a protected password manager until it is installed below. It is shown once. An **API / CLI / stdio MCP** credential has a different purpose and cannot replace an **HTTP MCP** credential. The host's backend service secret and delegation key never belong in Netclaw.

Connection names label the entries. NetRatel enforces isolation through each credential's grants on every operation. A default tenant or a prompt naming a customer does not restrict another credential that has broader grants.

## 2. Approve the customer's read operation in NetRatel

**NetRatel policy administrator:** complete this step before handing the connection to the Netclaw administrator. Credential grants do not create an Allow policy. Approve only the exact customer credential, tenant, environment and read operation; keep the default deny. Customer credentials do not need **Manage MCP policy**.

Use the [reviewed client access workflow](../mcp-http/setup.md#reviewed-client-access-and-exact-expiry) to propose `netratel_access/request` with action `reconcile` for the caller-owned customer credential. Preserve its previously approved exact UTC expiry, request only that customer's `flow.read` grant and server `mcp.discovery.read`, and select the exact tenant-bound `netratel_flows/list` target. An independent authorized operator reviews the identity, resource, environment, grants, scopes/roles, target and expiry before approval. Resume the same request ID, inspect the safe receipt, and complete the actual permitted read in step 4. Missing owner authority remains a prerequisite; requesting access does not create it. New credentials use the protected preparation and delivery commands in that guide. No administrative API token is required for this workflow.

**Optional advanced policy administration:** established policy administrators can also use the [administrative API](../contracts/API_REFERENCE.md) directly. **Admin → Access** manages [RBAC](../operations/rbac.md), not these policy records. This maintenance path is separate from MCP client bootstrap and its independent review; it is not a fallback for a requesting client that lacks authority. The block below calls the **actual API HTTPS host**, not the Web sign-in URL or gateway. Use an account with instance-administrator authority or `mcp.policy.admin`.

**Advanced administration — one copy/paste block, once per customer:** run in Bash on the administrator's machine with `curl` and `jq`. Use the same administrator account that created the credentials in step 1. **The API must allow local account sign-in (Local or Hybrid)**; MCP Local credential mode does not determine API sign-in mode. Edit the four non-secret values using server review and the customer name. The block prompts for hidden sign-in/2FA, shows the one-read scope and requires `ALLOW`. It finds the credential ID/tenant itself and removes private login/cookie files on exit.

<!-- BEGIN NETCLAW_HTTP_POLICY_RECIPE -->
~~~bash
(
set -euo pipefail
set +x
umask 077
API_URL='https://netratel-api.example.invalid'
MCP_URL='https://netratel.example.invalid/mcp'
INSTANCE='prod'
CREDENTIAL_NAME='customer1-netclaw'
policy_work=$(mktemp -d)
trap 'rm -rf -- "$policy_work"' EXIT
api_origin=$API_URL
api_origin=${api_origin%/}
[[ "$api_origin" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]] || {
  echo 'Use the API HTTPS origin only, without credentials, a path or query.' >&2; exit 1;
}
mcp_resource=$MCP_URL
[[ "$mcp_resource" == https://* && "$mcp_resource" != *[[:space:]]* &&
    "$mcp_resource" != *'@'* && "$mcp_resource" != *'?'* && "$mcp_resource" != *'#'* ]] || {
  echo 'Copy the exact canonical HTTPS MCP URL from server review.' >&2; exit 1;
}
IFS= read -r -p 'Administrator email: ' admin_email
IFS= read -r -s -p 'Administrator password: ' admin_password
printf '\n' >&2
printf '%s\n' "$admin_password" |
  jq -Rn --arg email "$admin_email" '{email:$email,password:input,rememberMe:false}' > "$policy_work/login.json"
unset admin_password
api=(curl --fail --silent --show-error --proto '=https'
  --connect-timeout 10 --max-time 30
  --cookie "$policy_work/cookies" --cookie-jar "$policy_work/cookies"
  --header 'Content-Type: application/json' --header 'X-NetRatel-Account-Request: 1')
login_status=$("${api[@]}" --data-binary @"$policy_work/login.json" \
  --output "$policy_work/login-response.json" --write-out '%{http_code}' \
  "$api_origin/api/v2/local-auth/login")
rm -f -- "$policy_work/login.json"
if [[ "$login_status" == 202 ]]; then
  jq -e '.requiresTwoFactor == true' "$policy_work/login-response.json" > /dev/null
  IFS= read -r -s -p 'Authenticator or recovery code: ' second_factor
  printf '\n' >&2
  printf '%s\n' "$second_factor" | jq -Rn '{code:input}' > "$policy_work/two-factor.json"
  unset second_factor
  login_status=$("${api[@]}" --data-binary @"$policy_work/two-factor.json" \
    --output "$policy_work/login-response.json" --write-out '%{http_code}' \
    "$api_origin/api/v2/local-auth/login/two-factor")
  rm -f -- "$policy_work/two-factor.json"
fi
[[ "$login_status" == 204 ]] || { echo 'Administrator sign-in did not complete.' >&2; exit 1; }
policy_instance=$INSTANCE
case "$policy_instance" in dev) policy_environment=1 ;; prod) policy_environment=2 ;;
  *) echo 'Use the instance from successful server verification.' >&2; exit 1 ;; esac
customer_name=$CREDENTIAL_NAME
"${api[@]}" --output "$policy_work/credentials.json" "$api_origin/api/v2/account/integration-credentials/"
jq -ce --arg name "$customer_name" --arg resource "$mcp_resource" '
  [.[] | select(.name == $name and .resource == $resource and .purpose == 1 and .revokedAtUtc == null)
    | select(any(.grants[]; .permission == "flow.read"))
    | select(any(.instancePermissions[]?; . == "mcp.discovery.read"))
    | select(all(.instancePermissions[]?; . == "mcp.discovery.read"))
    | select(([.grants[].tenantId] | unique | length) == 1)]
  | if length == 1 then .[0] else error("Need one matching unrevoked HTTP MCP credential with one customer, flow.read and discovery-only server access") end
' "$policy_work/credentials.json" > "$policy_work/customer.json"
credential_id=$(jq -r '.id' "$policy_work/customer.json")
tenant_id=$(jq -r '.grants[0].tenantId' "$policy_work/customer.json")
expiry=$(jq -r '.expiresAtUtc' "$policy_work/customer.json")
printf 'At %s allow %s only netratel_flows/list for tenant %s in %s until %s.\n' \
  "$mcp_resource" "$customer_name" "$tenant_id" "$policy_instance" "$expiry" >&2
IFS= read -r -p 'Type ALLOW to create this policy: ' approval
[[ "$approval" == ALLOW ]] || { echo 'No policy created.' >&2; exit 1; }
jq -n --arg name "$customer_name-flow-read" --arg id "$credential_id" --arg expiry "$expiry" \
  --argjson tenant "$tenant_id" --argjson environment "$policy_environment" \
  '{policy:{name:$name,environment:$environment,effect:2,priority:10,
    principalSelector:{kind:2,value:$id},targetSelector:{kind:2,tenantId:$tenant},
    targetClassification:null,operationFamily:128,operation:"netratel_flows/list",
    constraints:{},expiresAtUtc:$expiry,reviewByUtc:$expiry,auditReference:"Reviewed customer Netclaw flow read"}}' \
  > "$policy_work/policy.json"
"${api[@]}" --data-binary @"$policy_work/policy.json" \
  --output "$policy_work/created.json" "$api_origin/api/v2/mcp-operator/policies"
jq -e '.version == 1' "$policy_work/created.json" > /dev/null
jq '{policyId,version,environment}' "$policy_work/created.json"
)
~~~
<!-- END NETCLAW_HTTP_POLICY_RECIPE -->

Use `dev` only when the verified MCP instance and API runtime are Development; `prod` selects Production. Neither setting describes customer count. The fixed recipe permits only `netratel_flows/list` in the AutomationRead family, for the exact credential client ID and tenant, with expiry bounded by the credential. It does not allow editing/executing flows or access to another customer. If a response is interrupted, inspect the saved policy through the administrative API before creating another. An expired/revoked credential or matching Deny policy still prevents access.

This local-account recipe does not sign into an OIDC-only installation. Its administrator must use the established authenticated administrative API client; legacy OIDC policy administration also requires `netratel.mcp.admin`. Do not change the deployment's authentication mode to run this example.

## 3. Register the HTTP connections in Netclaw

### Isolate customer SignalR sessions

Use **a separate Netclaw daemon and endpoint for each customer** when connecting tenant-isolated NetRatel AI Ops sessions. The current native SignalR implementation assigns every incoming turn to the **Personal** audience; its MCP authorization selects a profile by audience, not by NetRatel tenant, paired device name or session ID. Two customer registrations available to Personal on one daemon are therefore available to both customers' SignalR sessions. Changing a connection name, prompt or selected client does not restrict that access. These behaviors are explicit in upstream [SignalR session admission](https://github.com/netclaw-dev/netclaw/blob/aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d/src/Netclaw.Daemon/Gateway/SessionRegistry.cs) and [MCP audience authorization](https://github.com/netclaw-dev/netclaw/blob/aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d/src/Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs).

Give each daemon its own persistent home, protected credentials, session store and network endpoint. Install only that customer's NetRatel MCP credential in it, and pair the matching NetRatel tenant to that endpoint. Separate containers or operating-system accounts must also prevent tools from reading the other customer's home or credentials; a shared writable volume, unrestricted host shell or Docker socket defeats that boundary. `NETCLAW_HOME` selects separate state, but changing that variable alone does not restrict filesystem access. Retain existing provider credentials through the deployment's protected provisioning mechanism; do not clone a complete home containing other customer credentials or sessions. See upstream [home selection](https://github.com/netclaw-dev/netclaw/blob/aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d/src/Netclaw.Configuration/NetclawPaths.cs), [Docker deployment](https://netclaw.dev/deployment/docker/) and [exposure modes](https://netclaw.dev/deployment/exposure-modes/).

**Inside customer 1's isolated daemon context:** replace the example URL with the endpoint from completed server setup. NetRatel uses Netclaw's `http` transport, which is Streamable HTTP. Inspect `netclaw mcp list` first; keep an existing correct entry instead of re-adding it and replacing its connection or secret settings.

```sh
netclaw mcp add customer1-netclaw https://netratel.example.invalid/mcp --transport http
```

Repeat in customer 2's and customer 3's separate daemon contexts, substituting their matching connection names. Do not register all three credentials in the Personal audience of a daemon serving tenant-isolated AI Ops.

**Inside the same customer's daemon context, Bash terminal:** store its bearer through Netclaw's encrypted secret store. The prompt hides your paste and the command entered into shell history contains only a variable reference. Keep shell tracing disabled. Run this block separately for each customer, changing both `customer1` occurrences to the matching customer before pasting its credential.

```bash
set +x
IFS= read -r -s -p 'Paste customer1 HTTP MCP credential: ' customer_credential
printf '\n'
netclaw secrets set McpServers.customer1-netclaw.Headers.Authorization "Bearer $customer_credential"
unset customer_credential
```

Netclaw stores secret values encrypted in `<Netclaw home>/config/secrets.json`; preserve that home's `keys/` with it. Endpoint and transport metadata live in `<Netclaw home>/config/netclaw.json`; the default home is `~/.netclaw`, unless `NETCLAW_HOME` selects another. Use Netclaw's protected secret-management equivalent for your deployment manager; do not put the bearer in a URL, Compose editor, plaintext configuration, chat, or committed example. A NetRatel profile saved in the MCP container is a separate consumer and supplies none of this configuration automatically.

## 4. Grant Netclaw tools, then verify customer boundaries

**Inside each customer's daemon context:** for an unmanaged CLI daemon, apply connection changes with the supported lifecycle commands, then inspect its tools:

```sh
netclaw daemon stop
netclaw daemon start
netclaw mcp list
netclaw mcp permissions
```

For a supervised container or service, use its existing supervisor's restart operation instead of starting a second daemon. Check installed-version help: current upstream [CLI dispatch](https://github.com/netclaw-dev/netclaw/blob/aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d/src/Netclaw.Cli/Program.cs) supports separate `daemon stop` and `daemon start` commands, even where older instructions mention a restart verb.

The daemon must run for discovery and the permissions screen. **Personal defaults to allowing all MCP servers and tools**; new Team/Public entries start without tool grants. Inspect the actual configuration rather than assuming Personal is restricted. For the first read in this guide, merge the following into the customer's existing `config/netclaw.json`, preserving unrelated settings and any stricter approval policy. Substitute the matching customer name in that daemon only:

```json
{
  "Tools": {
    "AudienceProfiles": {
      "Personal": {
        "McpServersMode": "Allowlist",
        "AllowedMcpServers": ["customer1-netclaw"],
        "McpServerToolGrants": {
          "customer1-netclaw": ["netratel_flows"]
        }
      }
    }
  }
}
```

In **mcp permissions**, verify that Personal exposes only the intended server and tools. Grant further tools only for intended operations and retain approval for actions requiring an operator decision. Netclaw's audiences and approval policies are separate from NetRatel's tenant grants and operation policy; all must permit an actual call. See [upstream MCP permissions](https://netclaw.dev/guides/mcp-tool-permissions/) and [registration defaults](https://github.com/netclaw-dev/netclaw/blob/aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d/src/Netclaw.Cli/Mcp/McpCommand.cs).

Save, restart the daemon again to load permission changes, and inspect:

```sh
netclaw daemon stop
netclaw daemon start
netclaw mcp list
netclaw mcp get customer1-netclaw
netclaw mcp tools customer1-netclaw --audience personal
netclaw status
```

`mcp get` redacts credentials. **Connected (N tools)** establishes transport and catalog discovery. For each customer, call `netratel_flows` with `{"operation":"list","request":{"tenantId":7}}`, replacing `7` with that customer's approved tenant ID from the administrator's policy summary. A successful result can be an empty flow list. Then call the same operation with another customer's tenant ID using that connection: it must be denied without returning that customer's data. Also confirm that the customer's SignalR session has no other customer's MCP registration available; denial with one credential does not prove isolation if the assistant can select a second credential. Repeat for all three isolated daemons, and after recreating the deployment. Record successful reads and expected denials separately. A prompt, connection name, catalog or generic HTTP 200 is insufficient evidence of tenant isolation.

Revoke one test credential in NetRatel and confirm that its connection can no longer execute; leave the other two active. Replace that credential securely if the connection should remain available. Removing local Netclaw configuration does not revoke its NetRatel credential.

## Add agent/system reads after the first connection works

For `netratel_clients/get` or `telemetry`, the administrator must additionally approve tenant `telemetry.read` and a matching ClientAdministration policy for that exact operation/target, with the server-owned agent target profile/classification. This applies in Production and in Development with the operator surface enabled. The Development compatibility surface has a different supported catalog; do not switch surfaces or add an environment-wide Development grant to bypass admission.

Optional `netratel_access/whoami` and `netratel_search` discovery currently require separately approved `client.manage` authority; client search also applies current target visibility/search policy. They are not part of the initial flow-read recipe. An administrator can instead supply known tenant/AgentId values for approved direct reads. Credentials, account permissions, policy and target classification must all permit the requested operation.

## External OIDC deployments

Retain the server's configured OIDC mode. Local `nrt_ic_` credentials apply only to Local credential mode. For OIDC, register each HTTP entry using the provider's actual client ID/scopes where required, then run `netclaw mcp auth <connection-name>` and complete the provider's browser flow. Follow the provider's redirect registration and Netclaw's [OAuth instructions](https://netclaw.dev/configuration/mcp-servers/#oauth-flow). NetRatel browser integration approval is a different protocol and does not issue an OIDC token. Complete the same permitted/denied tenant reads after OIDC authentication.

## NetRatel AI Ops is a separate connection

Pairing NetRatel's AI Ops workspace to the Netclaw daemon enables chat and investigation transport in the other direction. Its device token does not grant NetRatel tool access. Use [Netclaw AI operations](netclaw-ai-operations.md) for that optional pairing.

Repository tests and disposable deployment checks do not establish that a particular Netclaw daemon or owner deployment has passed these steps. Complete the checks above against the installed versions and retain their actual results.
