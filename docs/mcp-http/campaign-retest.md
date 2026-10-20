# HTTP MCP acceptance completion

Complete the remaining acceptance work after deploying a distribution-verified
release. The release agent builds and publishes; the owner deploys; Codex on the
owner's laptop prepares and runs the live campaign. The laptop prompt is an
execution prompt and contains no source implementation or release assignment.

Keep raw evidence and operational identities private. Track product acceptance
under [#294](https://github.com/BostonTechnologies/netratel/issues/294), with
[#282](https://github.com/BostonTechnologies/netratel/issues/282) as its parent.
The [beta.31 guide](https://github.com/BostonTechnologies/netratel/blob/f89593ccef6fa7385779baf1ba18328426218c07/docs/mcp-http/campaign-retest.md)
retains the former campaign instructions and historical comparison. The current
contract below applies prospectively; it does not rewrite historical outcomes.

## Baseline and finish line

The final beta.31 checkpoint retains 518 distinct context IDs. It records 335
invocations, 283 bounded operation passes, 37 admission denials and six failed
contexts. Invocation is not a pass. The outstanding planning population is 213:
170 untested, 37 denied and six failed. The other records include three
catalog-only observations, two boundary-negative observations, 12 prohibited
original-target mutations, two unsupported browse continuations, one deferred
autofill context and two authorized pin-remediation actions.

Preserve the original ledger and all attempts. Append the next source/version,
fixture and consumer to each new attempt. Reuse a bounded historical observation
only with its original version and limits, after reviewing whether the source or
fixture change affects it. Do not rerun every unaffected read because the release
number changed. Do not turn a historical or expected negative into a fresh
positive business result. Newly discovered operations get separate added rows.

Completion requires an explicit disposition for every remaining row, eight real
prompt-message workflow outcomes, the full measured hour, and dependency-ordered
cleanup. Required positive product behavior must be demonstrated; a missing
mandatory fixture or failed assertion remains a blocker. Protected, unsupported,
deferred and unobservable consumer-specific assertions remain explicitly named.
There is no meaningful target of 518 positive business passes in this population.

## Product corrections to verify

- Tenant create/update/delete confirmation replay returns the original resulting
  version and deletion state, including after later updates/deletion. Current
  authority, resource/instance/policy binding, exact payload and original expiry
  still apply. Fresh invalid/stale plans remain unconsumed; replay adds no write
  or accepted-operation audit. Legacy ID-only receipts retain their documented
  current-row fallback because they never recorded the original version.
- Policy creation and file collection return persisted timestamp precision on
  the first response and replay. Compare actual responses without rounding the
  captured evidence. The six failed beta.31 contexts represent two families:
  tenant replay and three timestamp observations affecting four contexts.
- A returned owned job folder round-trips through update without another tenant
  prefix. Another tenant's canonical operator namespace remains invalid.
- Tenant pin controls support set/change/clear/reopen, visible effective policy,
  current version checks and actor-attributed changes. Clearing a pin changes
  eligibility; actual automatic-update recovery has a separate test.
- Recover conflicting updater outcomes through the documented authenticated
  operator recovery path, retaining old evidence. Prove an actual approved
  offer, download, activation, observed running version and server terminal
  acknowledgement. Manual binary replacement establishes none of that chain.

The original five replay repairs already have 25 bounded beta.31 regression
contexts. Keep those results and repeat only the affected boundaries. Their
implementation issues are not reopened merely because full acceptance is pending.

## Prepare once, before the timed session

| Prerequisite | Concrete preparation | Completion evidence |
| --- | --- | --- |
| Release | Owner deploys the matching published components; select the verified bridge archive. | Running version/source, deployed manifest digests and bridge checksum match the completed publication record. |
| Authority | Inspect effective target-operation policy separately from credential grants. An owner administrator with current MCP policy authority may apply its own exact pending request using `netratel_access/owner_apply`, the saved receipt hash and `confirm=true`. Other callers use browser review. | Actual applied receipt, its decision mode, and admitted no-write first operation for every planned batch. |
| Extra permission | Preserve the original credential's authority and expiry. Use an exact owner-authorized limited scope for genuinely additional permissions. | Current permission, target, expiry and no-reveal readback. |
| Executors | Independently enrol one disposable Windows VM/host and one disposable Linux VM/host. | Exact tenant/agent identity, binary/source, protected profile, writable roots, inert service and disposal owner. |
| Owned objects | Create scripts/jobs/runs/tasks, policies, enrollments and receiver objects in dependency order. | Actual IDs and revisions from successful predecessor responses. |
| Receiver | Configure one owned synthetic notification/email/AI/flow receiver using supported configuration. | Current connector/revision, authorized destination and a real delivery/readback. |
| Readers | Prepare ordinary API/UI, no-reveal and restricted/cross-tenant/expiry controls. | Effective scope plus authorized readback of role restoration where required. |
| Infrastructure | Obtain the original QA runtime/volume disposition and durable Data Protection/backup continuity evidence. | Owner readback without key bytes; exact outstanding inventory request-to-completion relationship. |

The beta.31 dependencies overlap: reviewed access affects 101 outstanding rows
(64 untested plus 37 denied), executors 71, owned predecessors 57 and a receiver
35. Most denials were target-operation policy failures, not missing named grants.
Connectivity explicitly needs its separate connectivity-read permission. Do not
silently broaden the original credential or reuse the expired review request.

Use [reviewed setup](setup.md), [local credentials](local-credential-mode.md),
[disposable executor instructions](../client/CLIENT.md#disposable-executors-for-owner-acceptance)
and the [bridge guide](../integrations/http-mcp-bridge.md). Separate physical
computers are unnecessary; separate enrolled device identities are required.
Do not create another client identity over a protected host's canonical state.

Owner-authorized bounded fixture preparation and ordinary admitted preview/
confirm operations proceed within their recorded scope. The direct owner
decision is made by the authenticated requesting principal with current policy
administration and exact selected grant authority. The receipt records
`owner_direct`; the request retains that mode and the lifecycle audit records
the same owner and actor. Browser owner self-approval remains marked
`owner_self_approval`, and a different account's approval remains an
independent review. Do not infer application from a pending request alone.
For a policy mutation, review both the published `preview_*` and matching
`confirm_*` operation on the same target and profile revision. The API uses one
canonical policy operation across those two phases; the reviewed pair attaches
that internal permission. A preview-only review does not attach it.
Consolidate exact targets before creating short-lived requests. Original clients
and unrelated objects remain protected.
For `netratel_search/clients`, review its control-plane call together with each
exact tenant/agent candidate that should be discoverable at its current target
profile revision. The API filters candidates independently; a control-plane
grant alone cannot authorize an agent or disclose its search result.

Run one preflight, maximum 30 minutes after preparation. Demonstrate each batch's
first actual prerequisite against its selected target. Report one consolidated
Ready/Blocked table with exact affected context IDs. An offline preparation
validator checks records; it does not establish live admission or product success.
Continue ready independent batches, but do not start an inaccessible batch or
spend hours repeating denied calls and process logs without a new question.

The private kit includes the standard-library-only
[preflight checker](../../tools/testing/mcp-campaign-preflight.py) and its register
template. On the owner's laptop, use Python 3 (`python3`, or `py -3` on Windows)
from the extracted kit directory; copy the template to a new private register and
fill it with the actual preparation receipts. Substitute the completed release's
tag and full source SHA in this command:

```shell
python3 tools/mcp-campaign-preflight.py --register private-preflight-register.json --baseline beta31/acceptance-gap-ledger.json --history beta31/context-ledger.json --context-map next-campaign-context-map.json --expected-tag PUBLISHED_TAG --expected-source FULL_SOURCE_SHA --session-minutes 360 --output preflight-report.json
```

The checker performs no network calls or live mutations. It rejects altered
history, missing preparation records, protected executor reuse and insufficient
credential lifetime, and refuses to overwrite its inputs or an existing output.
A zero exit code only establishes that the preparation records are complete;
live admission and the actual assertions still need to execute.

## Declare the consumer evidence contract

The next campaign explicitly separates three evidence boundaries:

1. **Live AI business workflows.** Use the selected laptop AI consumer for real
   tool effects. Record its version and connection. Invoke each actual published
   prompt before its related actions. If the host lacks a native prompt menu,
   retrieve `prompts/get` and place the returned parameterized messages into that
   same AI conversation before it executes them. Record this as a prompt-message
   workflow. Retrieval/rendering or unrelated standalone calls alone do not pass.
2. **Live protocol behavior.** Observe raw schemas, progress and in-flight
   cancellation through a declared consumer using the published bridge and real
   deployed HTTP MCP service. The bridge launch, source and wire replies must be
   identifiable. Loopback tests remain supplementary. A protocol receipt must
   not be labelled an observation of an opaque AI-host schema cache.
3. **Host-specific UX.** Keep an unavailable native prompt menu, internal cache
   dump or exact per-call host-process binding as unobserved consumer assertions.
   They do not gate unrelated product business operations. This prospective
   scope declaration never converts their old missing evidence into passes.

[MCP prompts](https://modelcontextprotocol.io/specification/2025-06-18/server/prompts)
define listing and retrieval without mandating a particular UI. Its
[progress](https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/progress)
and [cancellation](https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/cancellation)
utilities are optional protocol features. NetRatel's advertised behavior remains
a test obligation; use an observable live consumer for it. Ordinary terminal
open/get/input/resize/window/close do not require those controls to be tested first.
Record the chosen modes before execution, including whether the hour uses native
AI calls or an observable live bridge consumer. The latter is a protocol soak,
not proof of a former native-only host scheduling assertion.

## Eight actual prompt workflows

Use the deployed returned messages as the workflow definition. Execute real
allowed effects and any actual human decision; preserve input, prompt identity,
tool sequence, postconditions and useful output. One execution may support
several named assertions, with each tool call counted once.

| Prompt | Required workflow |
| --- | --- |
| `inspect_client` | Resolve the authorized tenant/client, inspect current access and read bounded client evidence. |
| `diagnose_job_run` | Diagnose one real owned run using current state, steps and bounded redacted logs. |
| `controlled_remote_execution` | Preview/confirm one inert action and reconcile its actual terminal outcome. |
| `create_workflow` | Owned script revision → job definition → run/task; validate ETags, confirmation and audit. |
| `manage_operator_access` | Inspect then perform one admitted, versioned policy action without self-escalation. |
| `safe_file_operation` | Bounded owned file read/write with exact values, root enforcement and confirmation. |
| `safe_terminal_operation` | Owned session open, wait until opened, bounded input/read/resize and close. |
| `onboard_client` | Inspect collateral, issue an owned enrollment credential safely and revoke as required. |

`create_workflow` does not require a flow trigger or notification sink; actual flow
delivery belongs to its own tests. `onboard_client` describes credential issuance
and collateral, while actual installation is a client-lifecycle obligation.

## Finite batches

Counts are assigned outstanding contexts, not forecast passes. The private
completion map retains every exact ID, original disposition and dependency.

| Batch | Contexts | Execution order |
| --- | ---: | --- |
| Consumer/runtime preflight | 1 | First; declare observable evidence modes. |
| Tenant replay/timestamp regressions | 6 | Verify the corrective release before broader mutations. |
| Access and policy/profile lifecycle | 24 | Apply reviewed scope; retain exact primary authority. |
| Reads/discovery/connectivity | 18 | Resolve actual IDs and target readiness. |
| Jobs/tasks/scripts/terminal/snapshots | 51 | Produce the owned predecessor objects. |
| Fresh secret producers/readers | 14 | Reuse actual execution objects; test ordinary/reveal/isolation boundaries. |
| Services/log resync/protocol | 12 | Only inert services and isolated controlled fault mechanisms. |
| Monitoring/events/notifications/flows/AI | 51 | Verify controlled receiver before effects. |
| Onboarding/disposable administration | 20 | Enrol early when useful; disable/delete after dependencies. |
| Prompt preparation/eight workflows | 9 | Integrate actual prompts before the corresponding actions above. |
| Original QA/durable runtime disposition | 4 | Obtain infrastructure evidence during preparation. |
| Measured endurance | 1 | After preceding gates and competing traffic stop. |
| Cleanup/reviewer restoration | 2 | Last, with explicit dependency receipts. |
| **Total** | **213** | |

Plan for one prepared 4–6 hour session including the full hour. Provisioning,
deployment and reviewer availability are preparation, not hidden diagnostic time.
For a new product failure, retain one bounded reproduction and continue unaffected
ready batches. A batch must close a named assertion or answer a specific open
question. Save receipts at batch boundaries instead of regenerating a ZIP per call.

## Exact measured hour

After successful reads in both original tenants and the relevant functional/
security gates, finish mutations, review work and alternate diagnostic traffic.
Use the declared live invocation path for 240 serial calls at 15-second scheduled
intervals, repeating this order 60 times:

1. Tenant A: `netratel_flows/list` using the independently resolved original ID.
2. Server: `netratel_auth/status`, without a tenant argument.
3. Tenant B: `netratel_flows/list` using its independently resolved original ID.
4. Server: `netratel_system/version`, without a tenant argument.

Schedule starts at offsets 0, 15, 30 through 3585 seconds; observe through 3600
seconds. Record UTC and monotonic scheduled/actual start, finish, ordinal, outcome,
correlation, latency and deviation. No overlapping calls, catch-up bursts, replaced
failed slots or unrelated campaign traffic. Report actual/240 and actual/3600,
120 core outcomes and 60 per tenant, with latency percentiles and their method.
Do not shorten the hour or count ordinary campaign traffic toward it. A later
rerun is a separate phase with the earlier result retained. Historical denial-only
hours are comparisons, not admitted workload passes. No unadopted latency proposal
is an SLA.

## Cleanup and final report

Reconcile uncertain operations before retry. Complete owned cancellation and
terminal reconciliation, then remove dependent artifacts/runs/jobs/scripts and
receiver fixtures through their supported lifecycle. Revoke owned auxiliary
credentials, restore temporary roles with authorized readback, and dispose client
fixtures only after their readers, update tests and other dependencies are clear.
Preserve original assets, retained audit/history and unresolved updater records.

Deliver one final reconciled ledger, workflow outcomes, hour receipt, fixture/
cleanup status and private evidence package. Keep original failures and distinguish
product failures, incorrect test setup, denied access, prohibited actions,
unsupported capabilities and unobserved host behavior. A scoped product verdict
must name its exclusions; blanket acceptance remains open when a mandatory product
or deployment obligation is unresolved. Source CI and artifact publication are
reported separately from owner acceptance.
