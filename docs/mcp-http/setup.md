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

### Choose tenant and server access yourself

**An authorized operator can select both tenant grants and server-level grants. An AI agent is optional.** These are separate scopes on the same client credential:

| Access | Where to configure it | What it permits |
| --- | --- | --- |
| MCP host connection to the API | Host setup and **Review host** above | The server's backend service identity; separate from client grants |
| Client tenant grants | **Create credential → Tenant access → Selected tenant**, or **Details → Configured access → Edit access → Access scope → the selected tenant** | A named application permission in that tenant, such as tenant 17 `flow.read` |
| Client server-level grants | **Create credential → Server access**, or the editing selector under **Access scope → Server-level access** | Instance-level operations, such as discovery and authentication-status checks with `mcp.discovery.read` |
| MCP operator policies | **Admin → MCP Policies**, or the reviewed proposal's exact `targets` | Admission for the named credential/principal, environment, operation and target |

Web sign-in mode and HTTP MCP ingress authentication are independent. To use a NetRatel `nrt_ic_…` credential, configure [Local credential MCP mode](local-credential-mode.md); a Hybrid Web login does not select that MCP mode automatically.

In **Integrations → API & MCP credentials → Create credential**, follow **Connection → Server access → Tenant access → Review & create**. On **Connection**, choose **HTTP MCP gateway**, enter the verified `/mcp` URL and exact UTC expiry, then choose **Next**. Select instance permissions on **Server access**, then use **Add tenant** and **Selected tenant** on **Tenant access** to assign permissions separately to each customer. Either access step can be empty; choose at least one permission overall. **Back** and **Next** retain the selections. The labelled info icon beside each scope heading opens a compact explanation by click, keyboard or touch; close it with **Close explanation** or Escape. Grants remain subject to current owner authority and operation policies, and do not create policies or approval. On **Review & create**, check the separate server and tenant summaries, exact expiry and endpoint, explicitly confirm, and create the credential. Copy the one-time secret into protected client storage before closing its acknowledgement. Ordinary client pairing under **Review client** retains the combined scope selector.

For a first flow read, select **Read flows** (`flow.read`) under exactly the intended tenant and **Discover MCP capabilities** (`mcp.discovery.read`) under **Server-level access**. A credential with only server grants still lacks the tenant grant for that read, even if server-level `flow.read` is selected. Creating the credential stores its grants; the matching operator policy remains a separate requirement. Customer credentials do not need every server permission or `mcp.policy.admin` for this workflow.

A credential created with **Create credential** belongs to the signed-in account that creates it. Local and OIDC logins are separate principals, and their credential lists are owner-scoped. Its owner must have the current durable NetRatel permissions for the requested access. Selecting a grant or approving a policy does not create missing owner role assignments. **Admin → Access** currently lists local users; assignments for an OIDC principal require the authenticated [access-administration API](../operations/rbac.md).

### Edit an existing credential's access in the browser

Open **Integrations → API & MCP credentials**, find the owned HTTP MCP credential, and choose **Details → Configured access → Edit access**. The entire grant change, review and application can be completed in the Web UI. NetRatel keeps the credential ID, secret, purpose, canonical MCP resource and exact UTC expiry; the connected client continues using its saved secret.

If loading, review or save fails, the editor shows the failed stage, HTTP status, a safe failure code and a correlation reference when available. Use **Retry loading access** to recover the complete saved selections and pending review. For HTTP 401, sign in again; for 429, wait before retrying; for a destination or resource mismatch, have the deployment administrator check the public Web/MCP configuration. Share only the safe diagnostic reference with support. Never copy the credential secret or raw request headers. A failed pending-review load keeps editing blocked so a retry cannot silently replace an existing request.

1. **Inspect the starting selections.** The editor loads the complete current tenant grants and server permissions, and refreshes the tenants your account can currently select. A newly created authorized tenant is available when you open the editor. Check the original expiry and grants before changing anything.
2. **Add or edit tenant access.** Use **Add tenant**, select the tenant by name and ID, then choose its permissions in **Access scope**. For example, retain Customer A's existing grants and add Customer B with **Read flows** (`flow.read`). Select an existing tenant to add or clear individual permissions, or remove that tenant's grants. Switching scopes retains the selections in every other scope.
3. **Edit server access independently.** Select **Server-level access** in **Access scope** and change only the intended instance permissions. Retain **Discover MCP capabilities** (`mcp.discovery.read`) when the client still needs discovery and authentication-status checks. Tenant grants do not replace server grants, or the reverse.
4. **Review the complete result.** Choose **Review changes**. This creates an immutable review request for the resulting grant set, with the existing credential identity and exact expiry. Check every tenant group and the server permissions, including the grants you intend to retain or remove. Review does not apply the change.
5. **Approve and apply.** If your account has the current owner and MCP policy-administration authority, explicitly confirm the reviewed change and choose **Apply changes**. The server repeats its authority and proposal checks before applying it. If another authorized operator must approve, use the pending request's code and review URL: that operator signs in, enters the code under **Connect CLI / MCP client**, chooses **Review client**, and approves the exact request. Keep the editor open, or reopen **Details → Configured access → Edit access** on the same credential. The editor checks the pending request and automatically applies an approved result; no MCP `resume` call is needed for this browser workflow.
6. **Check the saved result.** After application, the credential list, Details counts, configured tenant/server groups and current admission refresh from the saved result. Reopen Details and confirm that the credential ID and expiry are unchanged and that all retained grants are still present. Inspect the admission prerequisites before testing an intended read through the existing client connection.

**Changing or recovering a draft:** **Back to edit** cancels that exact pending review and keeps your selections for correction; **Review changes** then creates a new review. Closing the editor does not cancel a submitted pending request. Validation, authority and save errors leave the draft available with a remediation message. Correct the stated prerequisite, then retry or return to the draft. A missing tenant or durable permission must be resolved for the owning account; granting a checkbox cannot create account authority.

**Operation policies remain separate.** The grant editor does not create or broaden Allow policies from permission checkboxes. A saved `flow.read` grant can still show a policy prerequisite for `netratel_flows/list`. An authorized policy administrator can use **Admin → MCP Policies** to review that exact credential, environment, tenant and operation in the browser, then refresh current admission. Existing policies are retained; removing a credential grant makes that operation unavailable even if a policy still exists. The [MCP request/approve/resume alternative](#reviewed-client-access-and-exact-expiry) below can reconcile explicitly named operation targets alongside the requested grants.

For focused retesting, add Customer B while retaining Customer A and the server grants, apply, reload Details, and compare the saved counts and exact expiry. Confirm the existing client still authenticates with its stored secret. Test one intended read in each approved tenant, then remove only Customer B's grant and verify that Customer A still works and Customer B is denied. Test independent approval by closing and reopening a pending editor, and verify that an unsuccessful save keeps the selections available.

For initial creation, operators can also use the authenticated account API or the client setup wizard. The protected CLI `access prepare` / `access resume` commands later in this guide create a new auxiliary credential; use **Edit access** or the existing-credential MCP flow to retain a credential already installed in a client.

### Reviewed client access and exact expiry

A local HTTP MCP credential's configured grants and the current operation delegation are separate from tenant visibility and target policy. Start with `netratel_access` operation `credential` and the caller-owned `credentialId` to inspect safe grants and the exact UTC expiry. `request` proposes `reconcile` or `revoke` for an owned credential; include a durable UUID `requestId`, exact `expiresAtUtc`, explicit `grants`, `instancePermissions` and `targets`. Each target names `tenantId`, `tool` and `operation`; agent operations also require the independently resolved `agentId` and current `profileRevision`. Use `requests`, `receipt`, `resume` and `cancel` with the saved request ID to reconcile interrupted setup. A changed proposal needs a new request ID and review. Reconciliation does not silently start a new thirty-day period.

**Alternative through an MCP client: request → browser approval → resume.** This path supports explicitly named operation policies as well as grants. Unlike the browser grant editor above, its requesting MCP client performs the resume step:

1. **Inspect through the existing HTTP MCP connection.** Call `netratel_auth` with `{"operation":"status"}` to obtain the caller's non-secret credential ID, then `netratel_access` with operation `credential` and that `credentialId`. These calls require the server-level `mcp.discovery.read` grant. Retain the current complete tenant grants, server grants and exact UTC expiry.
2. **Prepare the exact change.** A person using an MCP client, or an AI agent acting for them, calls `netratel_access` with operation `request` and a `request` object containing `action: "reconcile"`, the owned `credentialId`, name, a new UUID `requestId`, exact `expiresAtUtc`, complete desired `grants` and `instancePermissions`, and exact operation `targets`. Preserve existing grants and expiry unless their change is explicitly intended. These arrays describe the resulting grant set, not just additions. For a tenant 17 flow read, the tenant grant is `{"tenantId":17,"permission":"flow.read"}`, the required server permission is `mcp.discovery.read`, and the target is `{"tenantId":17,"tool":"netratel_flows","operation":"list"}`. Keep the returned request ID, proposal hash, verification URL and user code. Submitting the proposal does not apply its grants or policies.
3. **Review in the browser.** An authorized operator opens the returned verification URL, enters the user code under **Connect CLI / MCP client**, and selects **Review client**. Inspect and approve the exact credential, grant sets, operation targets and expiry. An eligible owner may explicitly self-approve as described below. Browser approval leaves the request ready for resume.
4. **Resume the same request.** The requesting MCP client calls `netratel_access` with `{"operation":"resume","request":{"requestId":"SAVED_REQUEST_UUID"}}`. NetRatel rechecks authority and applies the approved grant set and policies for the named targets; the successful receipt reports `consumed`. After an interrupted response, inspect operation `receipt` with the same request ID before repeating the unchanged resume.
5. **Verify actual access.** Inspect the receipt and refreshed credential admission, then call the approved read, for example `netratel_flows` with `{"operation":"list","request":{"tenantId":17}}`. A successful empty list is valid. Only the reviewed operation targets receive matching policies; granting `flow.edit` does not itself admit a flow-edit operation.

A successful reconcile keeps the existing credential secret and canonical MCP URL. A client such as Netclaw that already stores that secret does not need a replacement connection for this grant change. Use the same process to propose tenant grants, server grants or both; their scope does not determine whether a human or an agent must initiate the proposal. Authority and explicit approval determine whether it can be applied.

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
# After an authorized operator approves in the browser, poll/resume the same saved request.
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

When valid configured tenant grants lack matching Production operator policies, refresh the [credential admission view](local-credential-mode.md#reviewed-admission). An authorized policy administrator can review the exact credential, environment, tenant and operation under **Admin → MCP Policies**, or use the supported MCP request/inspect/resume reconciliation above. Use **Edit access** for grant changes; it does not create those policies. Preserve the original exact UTC expiry and grants until an approved proposal changes them. Setup success does not establish native tenant dispatch, agent readiness or production readiness; those remain owner acceptance checks.
