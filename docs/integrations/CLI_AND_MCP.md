# CLI and MCP

The CLI, stdio MCP server, and HTTP MCP service are separate product
interfaces. They use the API's authorization boundaries and should receive
least-privilege credentials appropriate to their caller.

The stdio MCP server is intended for a local MCP host process. HTTP MCP is an
optional separately deployed service; it is not required for normal Web/API or
native Client operation. Configure its explicit authentication mode, canonical resource, allowed operations and network policy before exposing HTTP MCP. See the [HTTP MCP operator guide](../mcp-http/README.md) and [Web/YARP single-origin routing](../deployment/web-gateway.md).

HTTP MCP requires HTTPS OIDC discovery metadata by default. The
`NetRatel:Mcp:Http:RequireHttpsMetadata=false` setting is accepted only by a
Development host for disposable local integration tests; it must never be used
for a deployed instance.

The rc.1 tag and archives are historical artifacts. Obtain tooling only
from its matching prerelease, verify `SHA256SUMS` before extraction, and keep
the archive together with its SBOM and provenance record. A source checkout is
appropriate for development rather than a substitute for a promoted release
bundle.

## Explicit setup and profiles

Run `netratel setup` or `netratel auth login` to connect a client. Setup is explicit; normal commands and MCP startup never open a wizard. The first page asks only for **Instance web URL**, with the textbox focused. Paste the HTTPS Web root or its `/mcp` URL and press Enter. Setup normalizes either form once, infers the invoked surface and uses the persistent default profile; review the derived destination after discovery.

Bare `dotnet NetRatel.Mcp.dll setup` selects a personal stdio client; bare `dotnet NetRatel.Mcp.Http.dll setup` selects a personal HTTP MCP client. Deployment-host provisioning requires explicit `--mode host` or `--target http-host`. Running personal setup inside the supported HTTP MCP container saves to its separate durable client-data volume; it does not create or replace the host's outbound identity.

Alternative transports, isolated profiles and storage paths remain available through Advanced or explicit flags:

```sh
netratel setup --transport cli --profile operator
netratel setup --transport stdio --profile mcp-operator
netratel setup --transport http --profile http-operator
```

After discovery, open/copy the trusted browser approval link and use its short code in the signed-in account UX. Approve the actual tenants/permissions/expiry, return to the terminal, then review and save. A sole allowed instance/grant needs no extra choice. The link works from another device for SSH/container sessions, and rejected/expired requests have a bounded retry. The protocol is NetRatel integration-credential pairing; it does not issue an external OAuth/OIDC token. Existing-credential and managed external-provider profiles remain available without resetting their authentication mode. Required external-provider details are requested progressively rather than sending the user to create a JSON file.

CLI and stdio use API-purpose credentials. HTTP MCP client setup uses a separate HttpMcp-purpose credential and canonical `/mcp` resource; it cannot configure the host's outbound service identity. Deleting a local profile does not revoke a server credential. Revoke/rotate from `/account/integration-credentials`.

The Termina setup UI supports Enter/Next, visible Back/Cancel/Advanced, keyboard navigation and PageUp/PageDown scrolling for long reviews. It retains fields and focus after validation errors and marks environment-managed settings read-only. Native tooling resolves its configured location first, then a writable per-user configuration default. Supported container clients use the personal volume `netratel-http-client-data` at `/var/lib/netratel/clients`; the default HTTP profile is `profiles/httpmcpclient/default.json` beneath it. The summary shows the resolved location. Setup checks storage before approval/save, masks manual secret entry and emits no secrets in examples. Cancelling before save preserves the prior profile; replacing a profile requires deliberate confirmation. On a non-TTY, interactive setup exits with code `2` and help on stderr; cancellation exits `130`, other setup failures `3`. Normal stdio serving uses pipes without a TTY and writes JSON-RPC only to stdout.

The noninteractive existing-credential equivalent reads a protected file:

```sh
netratel setup --non-interactive --transport cli --profile operator \
  --instance https://netratel.example.invalid \
  --credential-file /run/secrets/netratel-api-credential
```

Use `--replace` only after reviewing replacement of the selected profile. Do not pass a raw credential as a command argument. `--config PATH` selects an explicit destination instead of the standard isolated profile path. Exported environment/container configuration needs an explicit apply/recreate step; setup cannot edit a parent shell or running container.

`netratel config validate` emits redacted structured effective/source information with exit `0` for valid local configuration or `2` for invalid configuration. `netratel doctor` performs bounded read-only checks and reports unavailable/unauthorized stages separately. For stdio, when the shipped executable is available in the current installation, adjacent stdio archive or HTTP tooling image, doctor uses the isolated effective configuration to perform a real MCP initialize, initialized notification, tool listing and offered `netratel_auth` status call. Missing executables or unexecuted protocol checks remain unavailable/pending; denied calls are reported as unauthorized. Incomplete verification exits `2`; saving the profile alone does not complete transport verification. `netratel profile list` lists local profiles; `netratel profile select NAME` prints an explicit config invocation or dedicated selector rather than silently switching every consumer. See [shared configuration](configuration.md) for keys, precedence, origin/purpose binding and atomic persistence.

The stdio distribution and HTTP tooling image provide explicit `setup`, `config validate`, `doctor` and profile commands through their supported executable entry points. The stdio archive ships the `NetRatel.Mcp` apphost and `NetRatel.Mcp.dll`; use the extracted apphost or `dotnet /absolute/path/NetRatel.Mcp.dll`. Setup emits that actual executable when found. A standalone CLI installation without the stdio archive reports that the stdio executable must be installed before its launch snippet can work. The HTTP tooling image uses its `stdio` dispatcher instead; `setup --target cli|stdio|http-client` selects a personal client, while `setup --mode host` or `--target http-host` selects deployment administration. See [HTTP MCP setup](../mcp-http/setup.md) for the URL-first container command, generated apply action and resumable live verification.

## Integration credentials

An authenticated user can open **Admin → Integration** at
`/account/integration-credentials` to create a least-privilege credential. The Admin group retains each destination's existing permissions; it does not require the Operator role for personal Integration access. Select each required tenant and permission
individually, a bounded expiry, and an explicit purpose. One credential can
carry several tenant and server-level grants. The generated secret
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

## Client operations, monitoring, and flows

The `services`, `monitoring`, `flows`, `ai`, and `clients-v2 metadata` /
`clients-v2 set-metadata` commands use the same closed operation catalog as
HTTP and stdio MCP. Each command requires `--tenant-id`. Client operations
also require the registered `--agent-id`; use the UUID returned by client
discovery, rather than a hostname. `--help` describes the operation, its
permission, and its accepted flags. Responses retain the API's JSON structure
so scripts can inspect status, revisions, cursors, and operation identifiers.

| Task | CLI commands | Required tenant permission |
| --- | --- | --- |
| Read client tags and friendly name | `clients-v2 metadata` | `telemetry.read` |
| Change tags or friendly name | `clients-v2 set-metadata` | `client.manage` |
| View or refresh service inventory | `services inventory`, `services refresh` | `telemetry.read` |
| Start, stop, restart, or inspect a service operation | `services start`, `stop`, `restart`, `operation` | `client.manage` |
| Read monitoring configuration, evidence, alerts, and clients | `monitoring configuration`, `summary`, `series`, `events`, `clients`, `client-series` | `monitoring.read` |
| Create or edit rules and static groups | `monitoring save-rule`, `save-group`, `delete-rule`, `delete-group` | `monitoring.manage` |
| Acknowledge or clear an occurrence | `monitoring acknowledge`, `clear`; aggregate offline alerts use `acknowledge-offline`, `clear-offline` | `monitoring.ack`, `monitoring.clear`, respectively |
| Create or revoke monitoring silence | `monitoring save-silence`, `delete-silence` | `monitoring.bypass` |
| Inspect drafts, versions, run history, templates, or connectors | `flows list`, `get`, `versions`, `version`, `runs`, `run`, `template`, `connectors` | `flow.read` |
| Validate or simulate a draft | `flows validate`, `dry-run` | `flow.read` |
| Create, save, clone, or enable a draft | `flows create`, `save-draft`, `clone`, `set-enabled` | `flow.edit` |
| Publish an immutable version | `flows publish` | `flow.publish` |

Mutating commands in these groups request an authority-checked review unless
`--confirm` is supplied. Read the returned review and repeat the same request
with `--confirm` when the change is approved. The API rechecks current grants
and target scope for execution. A review does not reserve a configuration
revision or grant permission that the credential lacks. Monitoring targets
that include every eligible tenant client additionally require
`monitoring.targets.all`.

Use `--body` or `--body-file` for the operation's typed **data object only**.
Tenant and target identifiers belong in flags. The two body options are
mutually exclusive; files must contain UTF-8 JSON and fit within the bounded
request limit. Use the current configuration revision as
`expectedConfigurationRevision`; monitoring group/rule writes carry their
next resource revision as described below. On a revision conflict, read again,
reconcile the intended change, and request another review.

### Service investigation and control

Read inventory first to obtain the exact service name and reported platform.
Supported control platforms are `Windows` and `LinuxSystemd`; use canonical
names such as `Spooler` or `sshd.service`. A caller-generated operation UUID
identifies one action. Reuse it when checking or retrying that same request;
create a new UUID for a different action.

```sh
netratel services inventory --tenant-id 7 --agent-id "$AGENT_ID"
netratel services restart --tenant-id 7 --agent-id "$AGENT_ID" \
  --service-name sshd.service --platform LinuxSystemd \
  --operation-id "$OPERATION_ID" --reason "Investigate service alert"

# After reviewing the returned request:
netratel services restart --tenant-id 7 --agent-id "$AGENT_ID" \
  --service-name sshd.service --platform LinuxSystemd \
  --operation-id "$OPERATION_ID" --reason "Investigate service alert" --confirm
netratel services operation --tenant-id 7 --agent-id "$AGENT_ID" \
  --operation-id "$OPERATION_ID"
```

An accepted or queued operation is still pending. Inspect `services operation`
for the outcome and fresh evidence before reporting that the service changed
state. For an uncertain delivery outcome, inspect the existing operation ID
before submitting more work. To update stale inventory, review and confirm
`services refresh`, then read `services inventory` again.

### Tags, groups, and flow drafts

```sh
netratel clients-v2 metadata --tenant-id 7 --agent-id "$AGENT_ID"
netratel clients-v2 set-metadata --tenant-id 7 --agent-id "$AGENT_ID" \
  --body '{"friendlyName":"Application server","tags":["linux","production"]}'
netratel monitoring configuration --tenant-id 7
netratel monitoring clients --tenant-id 7 --limit 50
netratel monitoring save-group --tenant-id 7 --group-id "$GROUP_ID" \
  --body-file group-change.json
netratel flows get --tenant-id 7 --flow-id "$FLOW_ID"
netratel flows connectors --tenant-id 7
netratel flows save-draft --tenant-id 7 --flow-id "$FLOW_ID" \
  --body-file flow-draft.json
netratel flows publish --tenant-id 7 --flow-id "$FLOW_ID" \
  --body '{"expectedRevision":9}'
```

For `monitoring save-group`, `group-change.json` contains `group`,
`expectedConfigurationRevision`, and `reason`. The `group` contains
`tenantId`, `groupId`, its next `revision`, `name`, and the selected
`agentIds`. Use `revision: 1` for a new group and the current group revision
plus one for an edit; `expectedConfigurationRevision` remains the current
tenant configuration revision. Keep the nested tenant and group identifiers
equal to the flags. Rules follow the same resource revision pattern. When
changing a rule's evaluation inputs (conditions, targets, hold/freshness
settings, or offline settings), also increment its `evaluationRevision` and set `resetPolicy` to
`SuspendOccurrenceAndRequireNewWindow`; otherwise retain its evaluation
revision. A new rule starts both revisions at 1.

Acknowledge and clear actions use the current occurrence ID and exact series
identity returned by monitoring evidence, with its configuration and operator
revision fences. `expectedStateRevision` is retained for compatibility;
ordinary telemetry updates alone do not invalidate the action. For a
per-client series, use `acknowledge` or `clear` with its nonempty `--agent-id`,
`--rule-id`, and exact `--resource-key`. When the returned series has the
all-zero agent UUID and `resourceKey: "clients:offline"`, use
`acknowledge-offline` or `clear-offline` instead:

```sh
netratel monitoring acknowledge-offline --tenant-id 7 --rule-id "$RULE_ID" \
  --body-file occurrence-action.json
netratel monitoring acknowledge-offline --tenant-id 7 --rule-id "$RULE_ID" \
  --body-file occurrence-action.json --confirm
```

The body contains `occurrenceId`, `reason`, and the observed
`expectedConfigurationRevision` / `expectedOperatorRevision`. The dedicated
offline operations accept no agent ID or resource override. The API verifies
the saved tenant/rule/offline occurrence before review and dispatch, then
uses the same occurrence/revision checks as the Web action. These are tenant
rule actions: a tenant-level policy for the exact operation is required;
client-only or tag-only grants do not authorize them, and exact-client denies
do not match a tenant aggregate action. MCP names these operations
`acknowledge_offline` and `clear_offline`.

Tenant monitoring and flow operations require an explicit tenant-wide MCP
policy for the selected operation, in addition to the current application
permission. A policy for one exact client does not grant tenant configuration,
history, or group/rule administration. `monitoring client-series`,
`acknowledge`, and `clear` also check the exact selected client's current
target policy. The optional `--search` filter belongs to `monitoring clients`
and accepts at most 256 characters; monitoring series and events use their
own bounded pages without a text search.

For `flows save-draft`, the data contains `expectedRevision`, `name`, and
`graph`. Obtain a graph from `flows get` or `flows template`; inspect
`flows connectors` before adding a node. `flows validate` accepts the graph
object itself. `flows dry-run` accepts the typed sample-event request and
simulates it without sending operational work. Publishing creates an
immutable version; a monitoring binding that executes it separately requires
the applicable `flow.execute` authority.

### Folder browsing and mutations

`files-v2 browse` returns nullable `createdUtc` and
`lastModifiedUtc` values from the client. Listings inspect immediate entries
only: they do not recursively scan folder contents or calculate folder sizes.
An older client or a filesystem without available metadata can return null;
do not infer timestamps from those missing values.

Use `files-v2 preview-create-directory` followed by
`confirm-create-directory` to create a folder. Use `preview-delete` followed
by `confirm-delete` to delete a file or an **empty** folder. These established
file commands require the returned plan token and idempotency key as well as
`--confirm`; inspect each command's help for its exact arguments. They retain
their existing policy-root and target checks. Folder deletion does not
recursively delete contents. Filesystem roots, approved roots, and final
symlink/junction targets are protected from directory mutations.

File operations use the following canonical tenant permissions, including
their corresponding preview and confirmation operations:

| Operation | Required application permissions |
| --- | --- |
| Browse, stat, read, and download | `file.read` |
| Create a folder, upload, or write | `file.write` |
| Delete a file or empty folder | `file.delete` |
| Collect a client file into an internal artifact | `file.read` |
| Copy | `file.read` and `file.write` |
| Move | `file.write` and `file.delete` |
| Clean up an artifact | `file.delete` |

Collection retains the `netratel.mcp.write` protocol scope, operation policy,
and existing plan/confirmation requirements. Copy and move retain their
existing source and destination root checks. The Web file browser uses the
same tenant grants to show its New folder and Delete empty folder controls
and checks client capabilities before offering them.

For CLI and stdio, an API-purpose integration credential uses its owner's
current file permission intersected with that credential's exact tenant grant.
The V2 file routes additionally require a matching MCP operation policy for
the persisted client and explicit permitted path roots. Preview and confirmation
remain bound to the same owner, credential, tenant, client, policy, and path.
HTTP MCP uses its existing authenticated relay and verified delegated account;
a raw M2M service token alone cannot supply that account authority.

### Netclaw connection and AI investigations

The `ai` group uses the same tenant Netclaw connection and investigation
records as `/ai/ops`. `ai connection`, `clients`, `list`, and `get` require
`telemetry.read`. Pairing, testing the connection, and enabling it require
`tenant.admin`. Create and archive operations require both `client.manage`
and `telemetry.read`; sending messages, responding to an approval, and
reconciling delivery additionally require `secret.use`. Alert-linked
investigations also require `monitoring.read` for creation, reading, and
subsequent actions.

Current MCP policy must permit the exact operation for every client in an
investigation's context. The API resolves saved context before reads and
mutations; it does not accept a replacement client list when opening an
existing investigation. `ai clients` omits denied clients, and `ai list`
omits an entire investigation if any of its context clients is denied.
These cached context checks do not require clients to be online.

```sh
netratel ai connection --tenant-id 7
netratel ai clients --tenant-id 7
netratel ai list --tenant-id 7 --skip 0
netratel ai create --tenant-id 7 --body-file investigation.json
netratel ai get --tenant-id 7 --investigation-id "$INVESTIGATION_ID"
netratel ai send --tenant-id 7 --investigation-id "$INVESTIGATION_ID" \
  --body-file message.json
netratel ai get --tenant-id 7 --investigation-id "$INVESTIGATION_ID" --after 12
```

An investigation create body contains `requestId`, `title`, and `agentIds`,
with an optional alert `occurrenceId`. Select systems from `ai clients` and
use a new request UUID for each investigation; reuse the same UUID when
checking or retrying the same create request. A send body contains
`messageId` and `text`; preserve the message UUID for a retry. Review the
create/send request, then repeat with `--confirm` to dispatch it.

Read the investigation snapshot to see the processing state, observed events,
and pending approvals. A transcript page contains at most 200 events. Advance
`--after` to the last **returned event's** `sequence`, and continue until it
reaches the snapshot's `lastSequence`; advancing straight to `lastSequence`
can skip events when a transcript spans several pages. `ai list` returns
`{items,hasMore,nextSkip}` after scanning at most 100 rows per request.
Pass its `nextSkip` as the next `--skip` while `hasMore` is true, even when
policy filtering returns no visible `items`. This preserves bounded paging
without skipping accessible history or treating a filtered page as the end.
For a pending approval, use `ai respond --investigation-id ... --call-id ...`
with `{"selectedKey":"..."}` from the options actually returned by that
approval. Select and confirm the intended option explicitly. For a
`DeliveryUnknown` state, review and confirm `ai reconcile` before attempting
another message. Archive requests carry the expected message ID and require
explicit acknowledgement when delivery may have occurred.

Administrators can use `ai pair --body-file pairing.json` with `address`,
`pairingCode`, and the current `expectedRevision`, then `ai test-connection`
and `ai set-enabled` with `{"expectedRevision":...,"enabled":true}`.
Keep a pairing-code file private, remove it after pairing, and never include
the pairing code or device token in an investigation prompt. All three
administrative operations follow the same review / `--confirm` boundary.

## stdio MCP configuration

The stdio MCP server loads an explicitly selected isolated profile/file or complete dedicated `NetRatel:Mcp:Outbound` environment configuration. `NETRATEL_MCP_CONFIG` selects the compatibility absolute configuration-file path; it never reads ambient CLI or AgentClient credential variables. The legacy mixed-case `NetRatel_MCP_CONFIG` name is a
compatibility-only alias. Keep the JSON configuration file private and provide
only least-privilege credentials for its target API.

The stdio server remains isolated from CLI and AgentClient environment
credentials. To use an integration credential, select its API-purpose protected reference in that explicit owner-readable file or dedicated MCP environment; omit every `oidc*` field:

```json
{
  "apiBaseUrl": "https://netratel.example.invalid",
  "authenticationMode": "IntegrationCredential",
  "integrationCredentialFile": "/run/secrets/netratel-api-credential"
}
```

An isolated stdio configuration that contains both `integrationCredential` and
any `oidc*` value is invalid. The MCP process sends JSON-RPC only on stdout;
diagnostics remain on stderr and never include the complete credential.

For a machine-to-machine client-credentials flow on endpoints that explicitly
admit a service caller, create a private file such as `/etc/netratel/mcp.json`
with deployment-specific values. This mode alone does not authorize the V2
file operations or the account-bound services, monitoring, flows, and AI
operations above; use the API-purpose credential configuration for those:

```json
{
  "apiBaseUrl": "https://netratel.example.invalid",
  "apiM2MTokenUrl": "https://netratel.example.invalid/connect/token",
  "apiM2MClientId": "replace-with-a-least-privilege-client-id",
  "apiM2MClientSecretFile": "/run/secrets/netratel-mcp-service",
  "apiM2MScope": "netratel.api"
}
```

Protect that file with owner-only access. From an extracted Linux x64 review
archive, launch the server with:

```sh
NETRATEL_MCP_CONFIG=/etc/netratel/mcp.json NETRATEL_MCP_INSTANCE=prod \
  dotnet /path/to/netratel-mcp-linux-x64/NetRatel.Mcp.dll
```

Set `NETRATEL_MCP_INSTANCE=prod` for the production operator surface, including
folder creation and deletion. The default `dev` selects the Development
compatibility surface; the process does not infer this choice from the API URL.

The stdio protocol is written only to standard output; operational diagnostics
are written to standard error. Configure the MCP host to supply the file path
and to restart the process when credentials or the target configuration change.

For every canonical setting and alias, use [shared tooling configuration](configuration.md) and [HTTP MCP configuration](../mcp-http/configuration.md). [Web/YARP routing](../deployment/web-gateway.md) explains the single public origin, API allowlist and optional MCP route.
