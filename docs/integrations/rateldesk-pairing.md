# Connect NetRatel and RatelDesk

Open **Account → Integration credentials → System connections** at
`/account/integration-credentials` in either product. API & MCP credentials
remain in their separate tab.

1. On either product, select **Generate a Pairing Code**. Copy the displayed
   code to the other product. It expires after five minutes, is single use,
   and a newly generated code immediately replaces the previous code.
2. On the other product, select **Create Connection**. Enter the peer's
   **Address** and **Pairing code**, then select **Pair & connect**. Supported
   addresses include DNS names and IP addresses with HTTP/HTTPS and a port.
   The exchange runs on the server and stays on this page.
3. After **Systems paired**, select the real **NetRatel tenant** and
   **RatelDesk organization**. Choose that organization's customer when
   enabling incident creation. Enter a connection name.
4. Select the independent capabilities you need: **Create incidents**
   (NetRatel → RatelDesk), **Run automation** (RatelDesk → NetRatel), or both.
   Automation is never enabled implicitly.
5. Select **Save** once. It validates the current permissions and selected
   resources, provisions the connector or automation binding, and activates
   the connection. **Connected** describes a saved and authorized mapping.

Pairing alone permits setup and authorized directory choices. It does not
permit incident creation or automation. Only existing authorized tenants,
organizations and customers are selectable; pairing creates none of them.
There is no consent page, second sign-in, approval checkbox, enable switch or
mandatory test. If Save fails, correct the actionable inline error and retry
the same form. Refresh reopens the existing unconfigured pairing.

## Manage a connection

The list shows the connection name, peer address, mapped tenants, enabled
directions, status and last test result/time. **View** opens its configuration.
**Test connection** checks authenticated access, the current mapping and
selected capabilities without creating an incident or task. Testing is
optional after Save.

**Delete connection** removes the selected mapping and revokes its local
authority immediately, including when the peer is unavailable. Other mappings
between the same installations remain usable. Deleting the last mapping
also removes the pair credentials. An unused pairing can be deleted. Any
peer cleanup runs automatically; an offline peer has not acknowledged it.
Existing incidents, receipts, Flow runs and task history remain intact.

### Approve a saved execution account for a job

In NetRatel, open **System connections** and select **Paired Run as** on the
automation connection. Select the job and review its targets and effective
credential profiles. A tenant administrator with integration, jobs, Run as
and secret-use permissions can select **Approve saved accounts**.

Approval applies to the current connection revision, reviewed job and the
displayed target and profile set. For a selector job, review requires a
resolvable target set. A changed mapping, definition, membership or profile
requires review again. **Revoke** removes that approval. An existing paired
installation can use this workflow on its current mapping.

Jobs using the agent service account need no saved-account approval. An
inherited client default or an explicit job override needs approval when it
uses a saved profile. An HTTP 422 `run_as_denied` means NetRatel rejected the
attempt before creating an execution. Search the RatelDesk attempt reference
in NetRatel operations to locate the matching rejection.

## Addresses and credentials

Valid HTTPS works with public or private DNS and reverse proxies. Deliberately
entered private HTTP hostnames or IPs use the same two-field form. Normal TLS
certificate and hostname verification remain enabled. The server checks DNS
destinations at connection time, blocks reserved and metadata destinations,
uses fixed protocol routes, refuses redirects and bounds request time and
response size. Separate Web/API origins are resolved through verified peer
metadata; there is no additional API-address input or private-HTTP switch.

Codes and credentials belong outside URLs, logs, notifications and browser
storage. Long-lived credentials stay protected on the server. The pairing
retains each administrator's setup authority and rechecks current permissions
when selecting resources, saving and using a business capability.

## Target-aware self-service automation

The planned pair for durable deadline/receipt reconciliation is RatelDesk
**0.1.3-beta.10** and NetRatel **0.1.5-beta.5**. Candidate release and paired
acceptance gates remain pending; use their verified public artifacts after
those gates complete. Existing catalogue, invocation and callback contracts
from the **0.1.3-beta.9 / 0.1.5-beta.4** pair remain supported.
Upgrade NetRatel first, complete its ordinary automatic database migrations and
replace older API replicas before admitting modern automation. Then upgrade
RatelDesk. Keep the current pairing, named tenant connections, primary
organization, protected credentials and persistent keys. This upgrade needs no
new credential, scope, manual policy/configuration change or re-pairing.

The additive migration introduced in NetRatel **0.1.5-beta.4** permits an absent
fixed agent and records the aggregate target-plan digest on managed bindings.
Existing fixed-agent bindings, in-flight intents, callbacks and history retain
their identities. NetRatel **0.1.5-beta.5** adds no schema migration; RatelDesk
applies its own normal upgrade migrations and continues to support SQLite and
PostgreSQL. Back up the databases and persistent keys through the normal upgrade
procedure.

For a saved connection with **Run automation**, open the request-service workflow,
select that exact connection and choose **Browse jobs**. Canonical fixed-agent,
explicit-node, HA group, selector and declared runtime-target jobs are projected
from the normal NetRatel Jobs catalogue. Target summaries, required inputs and
readiness describe definitions; online-worker eligibility is checked at invocation.
An empty catalogue can still be opened and refreshed. Bindings retain the exact
connection, canonical job ID, input mappings and runtime settings across reopening.

**Create job** offers current tenant nodes and enabled HA groups, a supported
command runner, executable content, execution policy and optional inputs. Choose
a runner installed on the workers. Failed creation retains the draft and shows
the structured operation/status/code/reference. Creation refreshes the same
connection and selects the resulting ordinary NetRatel job. Existing input-sync
operations preserve the job's identity and execution target.

HA admits one eligible worker. Explicit-node jobs retain their complete frozen
node set and native concurrency/failure policy. Each request-task attempt owns
one durable aggregate parent, persisted before child dispatch. Exact retries
observe that parent; changed payloads conflict and later attempts require a
verified terminal predecessor. Child success cannot complete the RatelDesk task.
Final callbacks bind aggregate terminal readiness and result hashes, include
bounded child summaries/results, and preserve acknowledgement, rejection and
explicit replay. Unknown physical outcomes stay unknown.

Contract 2 is selected by `X-NetRatel-Automation-Contract: 2` on catalogue requests.
Read-only capability and tenant target-choice endpoints use the existing paired
read grant; creation and invocation retain the paired invoke grant. Older
consumers continue receiving fixed-agent catalogue entries. A newer consumer
identifies an older peer through the missing capability endpoint and reports its
fixed-agent limitation; modern creation/execution requires the compatible pair.
A connection test proves current authentication/mapping and reports supported
contract capability without creating or executing a job.

After redeployment, verify both API/Web release identities. Keep the saved
connections, refresh the intended connection, bind the HA job, save/reopen and
submit. Verify one worker and final task status. Repeat for the five-node job,
checking the intended nodes and parent outcome. Create a job from real target and
content choices, independently find it in NetRatel, bind and run it, then inspect
correlation/run links and any structured errors. Hosted source/distribution
verification is separate from this owner deployment acceptance.

## Execution deadlines and outcome reads

### Execution reconciliation

The additive authenticated `GET /internal/executions/{orchestrationRequestId}/{orchestrationRunId}`
receipt accepts the original RatelDesk request, task and attempt identity in
`X-RatelDesk-Request-Id`, `X-RatelDesk-Task-Id` and `X-Correlation-Id` headers.
The read URL contains no query string. It matches the admitted service principal, tenant, saved mapping,
revision, peer identity and current authority. A receipt read never admits,
recovers, replays or resubmits work. Terminal status requires the existing durable
terminal-readiness/result-hash proof; otherwise the outcome remains unknown.

RatelDesk can use this receipt to reconcile a missed callback. Its own persisted
deadline remains authoritative for the local waiting period. An unavailable or
older provider cannot confirm termination: the task needs reconciliation and
must retain its attempt identifiers without automatically retrying. The normal
authenticated callback can reconcile that same attempt later. Existing pairing
and read/invoke grants remain unchanged.

The opt-in source-pair lane also drives the real rendered self-service form using
an ordinary unlinked local requester and the existing unlinked administrator in their original organization. It records
correlated success, controlled native failure, explicit on-behalf regression,
normal local requester ownership in My Requests, idempotent request retry and a dropped-callback/provider-read outage across a
RatelDesk restart. Faults exist only in the owned loopback transport fixture;
restoring the original terminal callback must recover the same task and parent.
Passing source/CI receipts and owner production testing remain distinct.

## Upgrade from the retired pre-pairing setup

Upgrade both products before pairing. Forward migrations retire previous
NetRatel ↔ RatelDesk setups, revoke their credentials and disable their old
connector/provider bindings. Pair afresh and save each desired mapping using
the steps above. Old permissions are not transferred to new connections.
No manual SQL cleanup, installation identity reset or cleanup wizard is needed.
Mixed-version use of the discarded protocol is unsupported.

Installation and Flow producer identities, signing keys, unrelated API/MCP
credentials, Netclaw connections and business history are preserved. Keep the
normal database and persistent-key backups when upgrading.

After installation, check pairing from either product, Save the intended
mapping, optionally Test, exercise each desired business capability, and
Delete a disposable connection. Published artifact integrity and source-pair
validation are separate from acceptance of your deployed installation.

## Automation traceability and internal reports

The coordinated feature requires **RatelDesk 0.1.3-beta.12** and **NetRatel 0.1.5-beta.6** or compatible later readers. These are candidate minimums until both public release publication records pass distribution verification. Existing v1 flows, keys, accepted receipts and prepared deliveries remain supported.

Upgrade the RatelDesk provider first, using its ordinary SQLite or PostgreSQL migrations and retained database/key backups. Then upgrade NetRatel and apply its PostgreSQL migrations. Keep the saved connection and its protected credentials. The existing admission, callback and receipt mechanisms retain their authority and IDs; business payload keys cannot provide origin context. Do not activate the new writer while older receiver replicas still serve traffic.

In RatelDesk, open **Administration → Templates → NetRatel** (`/admin/templates`). Choose an organization and clone the appropriate explicit system default: job, request, script/task, alert or generic execution. Edit the customer-safe subject and internal body, preview synthetic data, save the draft and publish an immutable revision. Publishing expands and freezes the selected layout. Later draft/layout edits do not alter that revision. Retiring removes a template from new selection while retaining pinned history. Subject tokens are restricted to request/service/definition labels, outcome and totals; diagnostics, AI findings and recovery evidence are internal body tokens.

On the saved system connection, explicitly enable **Browse and preview internal incident templates** when custom templates are required. Saving uses the existing current-authority approval path and grants only `rateldesk.incident-templates.read` for the mapped organization. Standard presentation uses existing incident/receipt authority. No email-administration credential goes to NetRatel.

In NetRatel, edit each intended **RatelDesk: Create incident** action (including individual targets in an action group). Enable **Use human summary and source relations** for the pinned standard presentation. To customize it, enable **Use RatelDesk incident template**, search the scoped catalogue and choose the exact published revision. Preview, validate and publish normally. Existing published versions do not opt in automatically. Priority, category and assignment mappings remain effective; RatelDesk owns the restricted subject and internal report. An incompatible peer, missing/revoked revision or changed connector is an explicit error, never a substituted template.

A source dispatch snapshots the authoritative request, service/form/task, submitted-by and requested-for people and exact attempt before the first POST. The configured integration customer and machine actor remain separate; origin people do not become incident listeners. Missing legacy people are displayed as unavailable. Early incident delivery defers on the same key until retained admission/callback evidence proves the exact native execution. An unavailable origin fails explicitly; source-less monitoring/script events produce no invented origin relation.

Review the request task’s existing failure policy when adding an incident flow. `BlockRequest` and `FailRequest` retain their separate RatelDesk confirmed-failure escalation, so those policies can produce an additional incident alongside the configured NetRatel action. The paired traceability fixture uses `Continue` for its additional task to isolate one incident per action; its original task and native execution still fail. This feature does not change those request policies.

The incident and source request show an **Automation** report with source outcome, times, target counts, grouped failed activities, permitted excerpts, explicit omissions and real navigation. Failed activities are selected before bounds, so a later failing step is retained. Reports are staff-only records; ordinary subject/summary, notifications, search and public/portal serializers receive the restricted projection. Successful incident delivery does not change the original failed execution. Successful request automation keeps its existing fulfillment/closure behavior.

Neutral **Link ticket** is available in incident, request and change Relations tabs, with scoped type/reference/title search and reciprocal links. It changes no state, SLA, assignment, listener or notification. **Duplicate of** and **Close / Link** remain explicit incident operations. Both ends must be visible and manageable; counts and choices are permission-filtered before paging.

NetRatel indexes only trusted admitted source references and verified committed incident receipts. Search `REQ-DEMO-001` or `INC-DEMO-001` through the top bar, full results or Requests filter. Matches are exact-first, case-insensitive bounded prefixes, scoped to tenant/peer/organization. Request, job execution and flow run are distinct results. Related resources show safe source people/context and implemented request/job/flow routes. IDs such as `639300000000000001` remain decimal strings on JSON boundaries.

The existing local outbox projects associations in bounded transactions. Projection recovery never dispatches an incident. A resumable cursor scans retained bindings (100 per batch), then verified receipt sidecars (50 per batch); pending projections use batches of 32. An unprovable legacy origin is unavailable, not reconstructed from arbitrary payload/log text. The checkpoint records unavailable admission/receipt evidence. Backfill neither runs jobs/flows nor modifies remote tickets or historical source evidence.

For owner retesting after distribution verification: publish/select an internal template, submit a successful isolated request and confirm normal fulfillment, then submit a deliberately failing isolated multi-node directory check. Inspect the safe subject, **5 of 5 targets failed**, actual later activity/exit code, report and reciprocal request relation. Search both tracking references, open the exact job execution and flow, and confirm that source failure and successful incident delivery remain separate. Retry/restart the delivery without another incident, report or relation. Production deployment and owner acceptance remain separate from automated and distribution checks.

See the mirrored [v2 contract](../contracts/rateldesk-incident-create.v2.md) for exact bounds and fingerprint semantics.
