# HTTP MCP owner repeat campaign

Use this guide after deploying the distribution-verified corrective beta. Keep raw
evidence, original tenant names and deployment values private. Source regressions,
publication integrity and owner functional acceptance are separate stages.
[Epic #282](https://github.com/BostonTechnologies/netratel/issues/282) and
[campaign #294](https://github.com/BostonTechnologies/netratel/issues/294) remain
open until the owner explicitly disposes the campaign contexts.

## Connection and reviewed prerequisites

1. Record release/source SHA, configuration revisions, all five deployed registry
   **manifest** digests and catalog counts. Local image configuration hashes are
   separate. The corrective operator catalog has **30 tools / 229 operations /
   6 resources / 8 prompts**. Preserve the original 30/223/6/8 baseline and all
   existing matrix rows; add rows for the six new `netratel_access` operations:
   `request`, `requests`, `receipt`, `resume`, `cancel` and `credential`. Rediscover
   the deployed catalog and record any further differences.
2. Install the [versioned bridge](../integrations/http-mcp-bridge.md) or use native
   Streamable HTTP. Record the actual installed version, reconnect and migration.
   Loopback evidence does not prove owner-machine migration or live streaming.
3. Use [reviewed setup](setup.md) and [local credentials](local-credential-mode.md).
   Keep protected capabilities and secrets outside model input/output. Inspect the
   retained credential and preserve its **previously approved exact UTC expiry**;
   restarting the campaign does not restart its approval window.
4. Discover each original approved tenant and designated target independently,
   following published search pagination and clarifying ambiguity. Inspect
   configured grants, delegation and policy/tenant/target admission separately.
   Verify an effective native read in **both original owner tenants** before the
   admitted endurance phase; synthetic CI tenants cannot replace them.
5. Record protected API liveness/readiness and HealthRead admission through the
   saved destination separately from MCP-host health and business admission.

Register each fixture's owner, tenant, exact string ID, revision, permitted
operations, UTC expiry, confirmation/idempotency references and cleanup state.
Use reviewed restricted/revoked/expired auxiliaries, designated disposable
agents/tenants, bounded directories, an inert service, disabled schedules and
controlled sinks. Confirm the sink before delivery. Inspect the retained synthetic
run without executing it again.

The eight existing live agents and two original tenants remain protected: no
delete/update/disable/reboot/reenrollment or service control against them. Production
delivery and fabricated operator AI decisions remain Prohibited. Reconcile actual
state before retrying an uncertain mutation; the bridge never automatically replays it.

| Group | Required evidence on designated fixtures |
| --- | --- |
| G01 | Exact reviewed identity/resource/instance/grants/roles/targets/revisions/expiry, then admitted reads |
| G02 | Discovery, freshness, pagination, restricted and cross-tenant isolation |
| G03 | Preview, confirmation binding/reuse/expiry, stale revisions, idempotency, read-after-write and uncertain-outcome reconciliation |
| G04 | Benign execution, bounded files/hashes/traversal denial, terminal cancellation and designated inert service |
| G05 | Monitoring/flows/events/notifications, controlled delivery and explicit human AI decisions |
| G06 | Secret absent from ordinary API/UI/MCP/history/stream/log/error/export; denied and cross-tenant controls |
| G07 | Untrusted content cannot expand server-enforced authority or targets |
| G08 | Admitted endurance and a separate pagination-under-change phase |
| G09 | Existing JobsAutofillAcceptanceTests saved-dummy positive control plus deployed-form evidence |
| G10 | Reviewed restricted/revoked/controlled-time expired identities; exact expiry and effective revocation |
| G11 | Source/catalog/component manifest digests and configuration revisions |
| G12 | Native prompt workflows, progress before completion and in-flight cancellation |

Label protocol integration, source inspection, browser evidence, simulations and
actual native AI-host calls separately. Diagnostic prompt retrieval does not
complete a native workflow. Use existing optional integration/browser selections
for expensive acceptance; the owner hour does not run in required CI.

Install the [GitHub CLI](https://cli.github.com/manual/installation) and use an
authenticated account with repository Actions access. From that client terminal,
run the bounded disposable
protocol/client check against the exact published tag deployed for this campaign.
Replace the placeholder with that tag; it must agree with the retained release,
source and manifest-digest receipt:

```sh
RELEASE_TAG='vREPLACE_WITH_DEPLOYED_PUBLISHED_VERSION'
gh workflow run integration-validation.yml --repo BostonTechnologies/netratel \
  --ref main -f suite=mcp-campaign -f source_ref="$RELEASE_TAG"
```

This selection exercises independent reviewed setup and actual reads in synthetic
tenants, the shipped protected health gateway and gated bridge streaming/recovery.
It is a protocol/client integration receipt, not an actual AI-host invocation or
the original owner-tenant campaign. Browser selection with
`browser_classes=NetRatel.Web.PlaywrightTests.JobsAutofillAcceptanceTests` retains
the existing saved-account control; record the actual owner deployed form separately.

## Original paced comparison

After both original tenant reads succeed, repeat **60 minutes, one call every
15 seconds, concurrency one, 240 calls**, in this exact order:

1. Tenant A: `netratel_flows` with `{"operation":"list","request":{"tenantId":17}}`.
2. Server: `netratel_auth` with `{"operation":"status"}`.
3. Tenant B: `netratel_flows` with `{"operation":"list","request":{"tenantId":29}}`.
4. Server: `netratel_system` with `{"operation":"version"}`.

17 and 29 are synthetic placeholders: use the two independently resolved original
IDs. Server calls have no tenant argument. Record scheduled/actual UTC start,
monotonic latency, outcome and cadence deviation for each call; never overlap slow
calls to recover cadence. Expected admitted workload is 120 core successes and
60 successful reads per tenant. Report actual completion time and deviations.
Keep fault/lifecycle/fixture-change phases separate.

Historical results were 120 core successes, 120 tenant denials and zero transport
exceptions, with core p95 836 ms and denied-tenant p95 830 ms. Measure admitted
tenant latency separately. The 1700 ms core pilot remains a proposal, not an SLA.

## Checkpoint, cleanup and handoff

Retain original case IDs and all 430 operation/context rows. Record Passed, Failed,
Blocked or Prohibited with observable postconditions and evidence. Leave
unassessed AI-tooling dimensions null. At interruption save the last completed
case/call, pending request IDs, actual mutation state, catalog/fixture revisions,
credential receipts and next safe checkpoint. Resume by inspection before creating
another request or retrying a mutation.

Revoke owned auxiliaries through reviewed lifecycle; remove only designated inert
fixtures with their required confirmations. Record blocked cleanup and retain the
primary and protected assets. Return coverage, case results, gaps, scores,
fixture/cleanup receipts and checkpoint instructions. Keep raw owner evidence private.
