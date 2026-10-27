# Set up the HTTP MCP server, then connect clients

There are two separate connections:

```text
Netclaw / other HTTP MCP client → HTTP MCP server → NetRatel API
          client credential         host service identity
```

**Host setup connects the MCP server to NetRatel once per deployment. Client setup gives each consumer its own access.** Host/client does not mean home/company use or single/multiple tenants. Ordinary Web/API and the native NetRatel Client agent do not require HTTP MCP.

The `netratel://health` resource describes the MCP host only. After reconnecting the saved client, invoke `netratel_health` with `operation: "get"` to check the protected API liveness/readiness routes through the actual configured HTTPS destination. Preserve each component outcome and correlation ID. A healthy MCP host or admitted health probe does not establish delegated authentication or tenant access; complete `netratel_auth/status` and an approved tenant read separately. See [verification](troubleshooting.md#bounded-verification).

## Which setup command do I run?

These are commands for an already-open terminal, with the matching tools installed:

| What you are setting up | Command | Terminal |
| --- | --- | --- |
| **MCP server → API** | `dotnet /app/NetRatel.Mcp.Http.dll setup --mode host` | Inside a **prepared HTTP MCP container**; see the storage/deployment prerequisite below. |
| **Personal HTTP MCP client → server** | `dotnet /app/NetRatel.Mcp.Http.dll setup` | Inside the HTTP MCP container, with persistent client storage. |
| **CLI → API** | `netratel setup` | The client machine where the CLI is installed. |
| **Stdio MCP → API** | `dotnet NetRatel.Mcp.dll setup` | The extracted stdio tooling directory on the client machine. |

The direct **host** command needs persistent host-setup storage and prepared deployment information. It cannot discover Docker-host files or recreate sibling services from a container terminal. **For an existing deployment, use the complete host procedure below:** it obtains those details, starts the same host wizard and checks the applied containers afterward. Client setup alone cannot supply the server's missing prerequisites.

When starting a new tooling container through the image dispatcher, `setup --target http-host` selects host setup and `setup --target http-client` selects client setup. Those are image arguments, not standalone shell commands.

## Connect the server once

Use matching verified API, Web and HTTP MCP images. Have your public HTTPS Web URL, an instance-administrator login, and access to the saved deployment through its existing manager. The **Linux Docker host** needs Docker Compose with `config --no-env-resolution` (upstream v2.35.0 or later), Bash, Python 3 and GNU coreutils. The launcher checks the selected plugin before setup; [verified isolated Compose remediation](hosting-reference.md#compose-capability-prerequisite) leaves the host plugin unchanged. Keep the existing database, ingress, volumes and protected settings.

1. **Open a terminal on the Docker host**, not inside the MCP container. List the HTTP MCP containers:

   ```sh
   docker ps --filter label=com.docker.compose.service=mcp-http --format 'table {{.Names}}\t{{.Image}}'
   ```

   In a writable directory, copy the setup tool from the installed image. Replace `YOUR_MCP_CONTAINER` with its **name** from that list:

   ```sh
   mkdir -p netratel-mcp-tools
   cd netratel-mcp-tools
   mcp_container=YOUR_MCP_CONTAINER
   docker cp "$mcp_container:/app/setup-http-mcp.sh" ./setup-http-mcp.sh
   bash ./setup-http-mcp.sh --container "$mcp_container"
   ```

   You do not need another extracted Compose bundle. The tool reads the selected container's actual saved deployment files. If ownership is unknown, select the existing deployment manager or explicitly release-owned Compose when asked. A missing source must be restored before approval; see [source recovery](hosting-reference.md#missing-saved-source-or-first-time-mcp-enablement).

2. **Enter the NetRatel Web URL**, such as `https://netratel.example.invalid` or its `/mcp` URL. Check the displayed Web/MCP/API addresses. Existing API and authentication settings are reused. For a deliberately separate API address, choose **Advanced → Separate API HTTPS origin**.

3. **Approve the server.** Review the effective configuration and redacted changes, open the displayed approval link and sign in as the instance administrator. Check the code and choose **Review host**. This approves the persistent server connection; it grants no personal or tenant client access.

4. **Save, then deploy.** **Saved — apply pending** means deployment is still required. For an existing manager, choose **Export and exit**, privately open the complete Compose document at the printed host path, replace the **entire saved document in that same manager**, retain its interpolation/secret values, then **Save and Deploy**. For release-owned Compose, run the complete reviewed apply command from the deployment directory.

5. **Verify after deployment.** On the Docker host, in the same tools directory, run:

   ```sh
   bash ./setup-http-mcp.sh --container "$mcp_container" --verify
   ```

   In a new terminal, set `mcp_container` to its current name again first. In local credential mode, separately approve the **Temporary verification client**: only `mcp.discovery.read`, no tenant grants; it is short-lived and revoked afterward. For OIDC, follow the existing provider/account prompts. If interrupted before Save/apply, rerun the ordinary command from step 1 first.

6. **Finish at End-to-end verified.** Configuration, authentication, MCP initialization/catalog and the permitted API-backed status read must pass. **Deployment applied — verify pending**, a saved file, a token or HTTP 200 is not completion. Keep the checkpoint and persistent storage. Follow [recovery](hosting-reference.md) if cleanup remains pending or changed sources require renewed review.

If `docker cp` reports that the tool is absent, the running image predates this bootstrap method. Upgrade to the matching verified release first, or use the [existing bundle acquisition commands](hosting-reference.md#use-a-verified-compose-download). If there is no MCP container yet, use [first-time enablement](hosting-reference.md#missing-saved-source-or-first-time-mcp-enablement).

## Connect each HTTP MCP client afterward

**MCP container terminal:** start the client wizard with this command, without `--mode host`:

```sh
dotnet /app/NetRatel.Mcp.Http.dll setup
```

From a **Docker-host terminal**, the equivalent is:

```sh
docker exec -it "$mcp_container" dotnet /app/NetRatel.Mcp.Http.dll setup
```

Enter the Web URL. In local credential mode, open the browser approval link, choose **Review client**, and select only the required tenant/server permissions. For OIDC, follow the configured provider/account prompts. Return to the terminal, review, Save and complete its read-only verification. Keep the client-data volume: this profile belongs to that container, not another computer.

If that container lacks persistent client storage, use the copied host tool instead:

```sh
bash ./setup-http-mcp.sh --container "$mcp_container" --target http-client
```

For **Netclaw or another third-party client**, create a separate HTTP MCP credential under **Integrations → API & MCP credentials → Create credential** or approve that client's code under **Connect CLI / MCP client → Review client**. Install the one-time secret in that client's own protected storage and use the canonical `/mcp` URL. A container profile is not automatically installed into Netclaw. Follow [Netclaw customer connections](../integrations/netclaw-http-mcp.md) for its transport, policy and per-customer checks.

See [CLI/stdio commands](../integrations/CLI_AND_MCP.md), [host/storage recovery](hosting-reference.md), [configuration](configuration.md) and [troubleshooting](troubleshooting.md) for other contexts.

For a consumer that supports only stdio launch commands, obtain the versioned `NetRatel.Mcp` archive and configure `http-bridge --profile NAME` using [the supported client transport and migration guide](../integrations/http-mcp-bridge.md). Run its acquisition and client setup commands on the consumer machine; a container profile does not migrate that machine's installed adapter. Native HTTP connections remain available through the consumer's maintained transport.

### Reviewed client access and exact expiry

A local HTTP MCP credential's configured grants and the current operation delegation are separate from tenant visibility and target policy. Start with `netratel_access` operation `credential` and the caller-owned `credentialId` to inspect safe grants and the exact UTC expiry. `request` proposes `reconcile` or `revoke` for an owned credential; include a durable UUID `requestId`, exact `expiresAtUtc`, explicit `grants`, `instancePermissions` and `targets`. Each target names `tenantId`, `tool` and `operation`; agent operations also require the independently resolved `agentId` and current `profileRevision`. Use `requests`, `receipt`, `resume` and `cancel` with the saved request ID to reconcile interrupted setup. A changed proposal needs a new request ID and review. Reconciliation does not silently start a new thirty-day period.

Credential and request UUIDs accept standard representations, including hyphenated and compact values, with case-independent identity. New proposals normalize those IDs before storage and hashing. A replay in another UUID representation keeps the same request and exact original reviewed binding; it does not grant permission to change the proposal. Existing reviewed requests retain their original hash and approval binding.

An authorized signed-in operator enters the returned user code at the credential-management review URL, checks the identity, resource, instance/environment, exact grants, scopes/roles, targets, operation set, revisions and UTC instant, and explicitly approves. The credential owner may approve its own request when that account has current MCP policy administration and every selected durable permission. The browser labels owner self-approval explicitly; the receipt records `approvalMode=owner_self_approval`, and the lifecycle audit records the same principal as owner and actor. Approval and resume recheck current credential delegation and MCP policy authority. Missing owner roles or target profiles remain explicit prerequisites. No administrative API token or broad fallback grant is needed.

An owner with current `mcp.policy.admin` instance permission and MCP administrator delegation may instead call `netratel_access/owner_apply` on its own pending request. Supply the saved `requestId`, the exact 64-character `proposalHash` from the `request` receipt, and `confirm: true`. This applies an owned credential reconcile or revoke immediately, with `approvalMode=owner_direct` retained in the request record and the owner as lifecycle actor. The same exact call is idempotent. Creation still starts through the protected client and delivers its secret only through that client's device capability; `owner_apply` approves the prepared request, then protected polling completes issuance. Current owner authority, resource, environment, credential revision, target profile, policy revision, expiry and exact proposal hash are checked when the decision is made and again before delivery. A changed proposal needs a new request ID. This action does not grant an administrator role or permission to a credential that lacks it.

Lookup reports whether that signed-in operator is currently eligible to approve and whether the decision will be owner self-approval; this is advisory and approval/resume repeat the authority checks. Review failures include a safe code, stage, remediation and correlation reference. `review_approver_authority_missing` and `review_owner_authority_missing` distinguish the approver's and credential owner's durable permissions. Code expiry, proposed credential expiry, changed proposals, target revisions and policy changes have separate reasons. A transient `review_service_unavailable` preserves the pending request: keep its code and request ID, then inspect its receipt after recovery before retrying. Share the correlation reference when diagnosing a failure; do not share the private pending-state file or decrypted credential.

For a new auxiliary credential, the protected client prepares pairing outside model-visible tool results. On the consumer machine, [obtain and verify the stdio tooling archive and install its .NET 10 runtime](../integrations/http-mcp-bridge.md#obtain-and-configure-the-bridge). Run the commands below as the same operating-system user that owns the protected HTTP profiles, replacing `/path/to/netratel-mcp-linux-x64` with the actual extracted archive directory. Save a secret-free proposal JSON file, for example:

```json
{
  "requestId": "b1111111-1111-4111-8111-111111111111",
  "name": "Disposable flow reader",
  "action": "create",
  "expiresAtUtc": "2030-04-01T10:11:12Z",
  "grants": [{"tenantId":17,"permission":"flow.read"},{"tenantId":29,"permission":"flow.read"}],
  "instancePermissions": ["mcp.discovery.read"],
  "targets": [{"tenantId":17,"tool":"netratel_flows","operation":"list"},{"tenantId":29,"tool":"netratel_flows","operation":"list"}]
}
```

Choose a real future expiry within one year and replace synthetic tenant IDs with the separately approved exact tenants. The requesting profile must already have durable owner authority for the requested application grants.

```sh
# Client terminal. Existing HTTP profile stores the requesting credential securely.
dotnet /path/to/netratel-mcp-linux-x64/NetRatel.Mcp.dll access prepare --instance https://netratel.example.test \
  --request-file ./reviewed-proposal.json --requester-config ./existing-http-profile.json \
  --state-file ./private/access-request.json --config ./auxiliary-http-profile.json --profile auxiliary
# After independent browser approval, poll/resume the same saved request.
dotnet /path/to/netratel-mcp-linux-x64/NetRatel.Mcp.dll access resume --state-file ./private/access-request.json \
  --config ./auxiliary-http-profile.json --profile auxiliary
```

Prepare prints only request ID, non-bearer user code, review URL and expiry. Its protected pending file retains the device capability; keep it private and out of model context, exports and logs. A lost response is reconciled by rerunning prepare with the same request and paths. Resume saves the one-time credential through the existing protected profile store, then acknowledges delivery. It rejects a delivery whose grants or exact expiry differ from the retained proposal. Do not print or paste that profile's secret. Native `request` can inspect an already prepared create proposal, and `receipt` reports its safe outcome. Credential inspection never retrieves an existing secret.

#### Prepare an auxiliary credential from a retained Windows DPAPI primary

On the Windows consumer machine, first obtain the matching [Windows archive and prepare its complete DPAPI layout](../integrations/http-mcp-bridge.md#retain-an-existing-windows-dpapi-credential). Continue in that PowerShell 7 terminal with its `$preparation` receipt and extracted `netratel-mcp-win-x64` directory. Reuse the validated private state and temporary directories; their names alone do not prove their ancestry is protected:

```powershell
$privateDirectory = $preparation.layout.pendingStateDirectory
$temporaryDirectory = $preparation.layout.temporaryDirectory
```

Save the reviewed, secret-free proposal JSON in that directory. From the extracted `netratel-mcp-win-x64` directory, run:

```powershell
.\http-mcp-windows.ps1 `
    -Profile $preparation.profile -TemporaryDirectory $temporaryDirectory `
    -AccessPrepare -RequestFile (Join-Path $privateDirectory 'proposal.json') `
    -StateFile (Join-Path $privateDirectory 'pending.json') `
    -DestinationConfig (Join-Path $privateDirectory 'auxiliary-http-profile.json') `
    -DestinationProfile auxiliary
# After an authorized operator approves, resume the same saved request.
.\NetRatel.Mcp.exe access resume `
    --state-file (Join-Path $privateDirectory 'pending.json') `
    --config (Join-Path $privateDirectory 'auxiliary-http-profile.json') --profile auxiliary
```

The wrapper binds the requester to the prepared primary's canonical resource and supplies its credential only to the requester's authentication path in that spawned setup process. The primary credential is never loaded into the auxiliary destination profile. Preparation is idempotent only for the unchanged request and saved paths; inspect its receipt after an uncertain outcome. Resume uses the protected saved device capability and stores the newly approved auxiliary credential through normal shared setup. The primary's profile, encrypted source and active consumer configuration are preserved. Select an auxiliary profile only after its exact grants, expiry and permitted reads have been independently checked; creating it does not switch the primary consumer.

#### Verify effective admission

The receipt separates configured credential grants, the current attenuated delegation, current tenant visibility, exact policy decisions and remaining prerequisites. For agent targets, the receipt leaves `policyAllowed` null with `conditional_runtime_unverified` and keeps `authorizationAdmitted` false until current runtime prerequisites are established. Read `netratel_access target/effective` for that exact persisted target and policy, then inspect operation readiness. Tenant and control-plane authorization can be evaluated without agent runtime facts. Dispatch rechecks readiness, capability and the normal preview, confirmation and idempotency requirements. Complete setup by testing a permitted read in each approved tenant.

Reviewed credential expiry uses exact UTC instants at microsecond resolution: at most six fractional digits, such as `2030-04-01T10:11:12.123456Z`. Unsupported precision is rejected before date parsing; accepted input is never rounded. The 30-day preset adds exactly 720 hours to the displayed representable UTC reference.


## Current credential admission during client setup

HTTP MCP setup and doctor keep their successful authentication/status check separate from business admission. For a local credential, status provides its non-secret identifier and setup uses the existing `netratel_access/credential` inspection to attach current configured grants, durable owner permissions, exact expiry, binding and named operation/policy facts. The diagnostic exposes `admissionStatus` and the safe `admission` separately from the authentication check status. `blocked` means a known prerequisite is missing; `not_checked` means no authoritative local view or named operation sample was available. Older servers, external OAuth and discovery-only credentials can still complete connection setup while their business admission remains explicitly unassessed. No business operation runs as a setup diagnostic.

When valid configured tenant grants lack matching Production operator policies, refresh the [credential admission view](local-credential-mode.md#reviewed-admission) and use the supported request/inspect/resume reconciliation above. Preserve the original exact UTC expiry and grants until an approved proposal changes them. Setup success does not establish native tenant dispatch, agent readiness or production readiness; those remain owner acceptance checks.
