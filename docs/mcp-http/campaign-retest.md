# HTTP MCP owner repeat campaign

Use this guide after deploying the distribution-verified corrective beta. Keep raw
evidence, original tenant names and deployment values private. Source regressions,
publication integrity and owner functional acceptance are separate stages.
[Epic #282](https://github.com/BostonTechnologies/netratel/issues/282) and
[campaign #294](https://github.com/BostonTechnologies/netratel/issues/294) remain
open until the owner explicitly disposes the campaign contexts.

## Beta.23 carry-forward evidence

Use the final private `report.html` and JSON ledgers rather than the early draft
report. The final beta.23 decision remains rework before production acceptance.
`operation-coverage.json` contains **436 operation/context rows: 17 Passed,
2 Failed, 403 Blocked and 14 Prohibited**. Separately, `case-results.json` contains
**382 native invocation cases: 178 Passed, 194 Blocked, 3 Failed and 7 invalid
test inputs**, covering 65 distinct native operations. Passing negative controls
are part of the case count; only 154 cases were marked as positive execution
proof. Loaded operations, executed cases and accepted contexts are different
measures. All six resources were read natively; the eight prompt retrievals were
diagnostic, with native prompt workflows still incomplete.

The retained primary authenticated, but all 29 evaluated admission targets were
denied and the review did not apply grants or change credential expiry. A receipt
filename containing an approval label was not evidence of an approved state:
the recorded request remained pending and ultimately expired. Successful receipt,
replay or resume responses established inspection/reconciliation only. Preserve
the owner-requested exact UTC instant; a newly calculated campaign-relative
expiry requires a new actual operator decision.

The retained synthetic run's UI hid legacy input, step and output fields. That
fail-closed observation does not complete fresh secret/readership testing. The
published bridge's synthetic controls passed, while the active campaign adapter
and native client migration remained separate evidence. No owned business
fixtures or auxiliary identities were established. Retain the protected assets
and pre-existing sessions while these prerequisites are completed.

## Connection and reviewed prerequisites

1. Record release/source SHA, configuration revisions, all five deployed registry
   **manifest** digests and catalog counts. Local image configuration hashes are
   separate. Preserve the original **30/223/6/8** inventory and beta.23's
   **30/229/6/8** inventory with all existing matrix rows. The six added
   `netratel_access` operations were `request`, `requests`, `receipt`, `resume`,
   `cancel` and `credential`. Beta.24 also adds read-only
   `netratel_capabilities/status` and `schema`: rediscover the selected deployed
   surface and add its actual differences instead of treating an old count as a
   completeness gate. Compare compact host release/catalog identity and the
   complete requested operation schema with the client's loaded native metadata
   after its supported reconnect. The server cannot certify or refresh metadata
   inside the client.
2. Install the [versioned bridge](../integrations/http-mcp-bridge.md) or use native
   Streamable HTTP. Record the actual installed version, reconnect and migration.
   Loopback evidence does not prove owner-machine migration or live streaming.
3. Use [reviewed setup](setup.md) and [local credentials](local-credential-mode.md).
   Keep protected capabilities and secrets outside model input/output. Inspect the
   retained credential and preserve its **owner-requested exact UTC expiry**;
   restarting the campaign does not restart its approval window. Record the actual
   UI/server expiry separately; the owner-requested instant was not applied in
   the assessed campaigns.
4. Discover each original approved tenant and designated target independently,
   following published search pagination and clarifying ambiguity. Inspect
   configured grants, delegation and policy/tenant/target admission separately.
   Verify an effective native read in **both original owner tenants** before the
   admitted endurance phase; synthetic CI tenants cannot replace them.
5. Record protected API liveness/readiness and HealthRead admission through the
   saved destination separately from MCP-host health and business admission.
   Deploy the alias-capable API before or with MCP, then verify both
   `/api/v2/mcp/operator/health/live` and `/api/v2/mcp/operator/health/ready` through
   the actual ingress using the saved MCP client. Source tests do not resolve
   deployment routing, and a 404 must remain observable without fallback.

Register each fixture's owner, tenant, exact string ID, revision, permitted
operations, UTC expiry, confirmation/idempotency references and cleanup state.
Use reviewed restricted/revoked/expired auxiliaries, designated disposable
agents/tenants, bounded directories, an inert service, disabled schedules and
controlled sinks. Confirm the sink before delivery. Inspect the retained synthetic
run without executing it again. Re-inventory agent versions and resolve each
current tenant/agent binding; a GUID retained in a historical run is not a verified
positive execution target.

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
| G06 | Fresh synthetic secret absent from ordinary API/UI/MCP/history/stream/log/error/export; ordinary-authorized, unauthorized and cross-tenant readers; audited authorized reveal; ordinary false/zero inputs stay useful |
| G07 | Untrusted content cannot expand server-enforced authority or targets |
| G08 | Admitted endurance and a separate pagination-under-change phase |
| G09 | Existing JobsAutofillAcceptanceTests saved-dummy positive control plus deployed-form evidence |
| G10 | Reviewed restricted/revoked/controlled-time expired identities; exact expiry and effective revocation |
| G11 | Source/catalog/component manifest digests and configuration revisions |
| G12 | Native prompt workflows, progress before completion and in-flight cancellation |

Keep the beta.23 gap identifiers too: G-ADMISSION, G-ISOLATION, G-FIXTURES,
G-SECRET, G-AUTOFILL, G-ENDURANCE, G-RECONNECT, G-INJECTION, G-DELIVERY,
G-VALUES, G-PAGING, G-POLICY, G-PROTECTED and G-PRESERVATION. Map each to the
appropriate group and evidence rather than replacing it with a broad group pass.
NR-005 remains a historical administrative API diagnostic, with no API-token
workaround in this campaign.

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
exceptions in each completed hour:

| Campaign | Calls | Core p95 | Denied-tenant p95 | Comparability |
| --- | --- | --- | --- | --- |
| Beta.22 | 240; 120 core successes and 60 denials per tenant | 836 ms | 830 ms | Original paced comparison |
| Beta.23 | 240; 120 core successes and 60 denials per tenant | 948.35 ms | 967.55 ms | The phase used concurrency one, but independent diagnostic/review calls overlapped the campaign; maximum cadence deviation was 85.28 seconds |

The beta.23 timing receipt does not prove strict global single concurrency or
exact 15-second spacing. Its native dispatch timing is not network-only latency,
and the monotonic observation included clock-probe overhead. Keep background
diagnostics, approval interactions and all other native calls outside the next
comparison hour; record deviations without overlap or catch-up bursts. Neither
completed hour establishes successful tenant-workload endurance. If admission
fails, record a blocked admitted phase and label any denial-only measurements
separately.

Measure admitted tenant latency separately. The beta.22 1700 ms core pilot and
beta.23 1900 ms proposal are both unadopted criteria, not SLAs. Record which
criterion, if any, the owner explicitly accepts before using latency as an
acceptance decision.

## Checkpoint, cleanup and handoff

Retain original case IDs, the original 430 operation/context rows and the beta.23
436-row expansion; add rows for newly discovered operations and required contexts.
Record Passed, Failed, Blocked or Prohibited with observable postconditions and
evidence. Track invalid test inputs and their corrected follow-ups separately
from product failures. Preserve the original `ai-quality-scores.json` and
beta.23's `ai-usability-scores.json`; rescore assessed dimensions from current
evidence and leave unassessed dimensions null. At interruption save the last
completed case/call, pending request IDs, actual
mutation state, catalog/fixture revisions, credential receipts and next safe
checkpoint. Resume by inspection before creating another request or retrying a
mutation. A pending, expired or unapplied receipt never proves reviewed access
was applied; verify both the authoritative receipt state and the resulting native
business reads.

Revoke owned auxiliaries through reviewed lifecycle; remove only designated inert
fixtures with their required confirmations. Record blocked cleanup and retain the
primary and protected assets. Return coverage, case results, gaps, scores,
fixture/cleanup receipts and checkpoint instructions. Keep raw owner evidence private.
