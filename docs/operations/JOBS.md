# Jobs, HA Groups and node runs

A job has one target source: **HA Group**, **Selected nodes**, **Node filter**,
or **Provided at run time**. A high availability group is a tenant-owned worker
pool. Each fresh job invocation runs its complete ordered workflow on exactly
**one healthy, authorized worker**. Selected nodes, filters and runtime target
lists continue to run on every selected node. Membership grants no execution
permission.

## Configure a group

Open the tenant editor and select **HA Groups**. Create a named group, search
the authorized client directory and choose workers. Search and paging retain
selections, including offline or subsequently disabled members. Save explicitly
with **Save group**; tenant Save/Cancel does not undo an independently saved
group. Names are trimmed, Unicode Form KC normalized and compared with
invariant uppercase within the tenant, with a 200-character bound. Renaming
preserves the group ID. Groups contain at most 10,000 members; group and target
HTTP JSON is capped at 1 MiB before binding. Empty groups may be saved during
setup. Disabled, archived or missing groups cannot admit fresh work. Referenced
groups must be retired or their job/retained Flow references removed before
hard deletion. A stale edit preserves the draft for reconciliation.

The orchestrator rotates through the stable ordered eligible pool using a
PostgreSQL cursor shared across API instances. Worker choice, cursor advance,
parent and single node queue commit together. Preview does not rotate the
cursor. Duplicate keyed or schedule delivery returns the original run and
worker without advancing it. With unchanged healthy membership, 100 new runs
split 50/50 across two workers, or 25 each across four; adding workers shares
subsequent work across the new pool. This balances admission counts, not job
runtime or CPU usage, so different job durations can produce different active
loads.

Availability comes from the shared committed connection lease and validated
authentication expiry. Offline, expired, disabled, revoked or otherwise
unauthorized members cannot be chosen; healthy eligible survivors remain
usable. If no eligible members remain, target resolution fails. If eligible members
exist but none has a healthy lease, admission returns
`ha_group_no_healthy_workers` before creating a parent. A worker already
admitted to a run is not silently replaced: native ownership, receipts,
cancellation and unknown-execution safeguards still prevent duplicate work.
**Rerun original execution** keeps that original worker; a new ordinary run
balances again.

The wire identifiers `ExecutionGroup = 1`, `executionGroupId`,
`execution-groups` and `netratel_execution_groups` remain compatible. Fresh
HA resolutions use target-plan schema 2. A selected plan binds its one worker
to the approved frozen candidate pool and its digest. Volatile online hints
are not part of the schema-2 HA policy digest; committed availability is
checked when choosing the worker. Membership revision, identities, metadata,
definition and execution settings remain bound. Historical schema-1 plans,
including already accepted reviewed/keyed submissions, retain their original
fanout and canonical hashes. This correction does not rewrite admitted history.

## Select targets

The full-width job editor contains Definition, Targets, Execution, Steps,
Parameters and Schedule sections. Changing target mode replaces its previous
target, including the old singular AgentId. A legacy job with only AgentId
continues to mean exactly that client.

Use stable IDs for selected nodes. The shared server resolver powers search,
preview and admission. It resolves the complete authorized result before
paginating the preview. The displayed page never limits execution. Empty input
does not mean all nodes; selecting all is deliberate.

| Filter | Meaning |
| --- | --- |
| `.*` | Every authorized node in the tenant; the explicit `all: true` form is equivalent. |
| `lab-node-01 lab-node-02` | Either exact hostname or registered name; ambiguous names need IDs. |
| `hostname: lab-node-01,lab-node-02` | Either exact hostname. |
| `tags: TestSystem` | Exact case-insensitive tag membership. |
| `tags: TestSystem+Linux` | Both tags. |
| `tags: TestSystem,Staging` | Either tag. |
| `os: Linux tags: TestSystem` | Both property clauses. |
| `hostname: /^lab-node-[0-9]+$/` | Explicit bounded regular expression. |
| `tags: TestSystem !tags: Excluded` | Include TestSystem and exclude Excluded; exclusion wins. |
| `tags: "Lab + tools"` | The punctuation-bearing tag is one literal. |
| `tags: @option.TargetTag@` | Bind the declared parameter as a literal operand. |

Tag `+` binds before comma. Different property clauses are combined with AND;
repeated clauses for the same property are OR alternatives. Exclusions always
win. Quote literals containing spaces, operators, colons, slashes or quotes.
Either single or double quotes delimit a literal; backslash escapes the
following character. Inside a regex, `\/` escapes its delimiter and other
backslashes retain regex meaning. Use the builder's **Regex** switch or explicit
`/.../` operands for regular expressions. Bare `.*` is an all-nodes shorthand
and can be combined with property clauses or exclusions; quote `".*"` to match
that exact node name. Parameter values remain literal, including `.*`.
Invalid/unsupported expressions, ambiguity, exhausted work or
regex bounds, and no matches are errors rather than partial results.

The property catalogue returned by the server is authoritative. Version 1
supports `id` (aliases `agentid`, `clientid`), `hostname`, `name`, `displayname`
(alias `friendlyname`), `tags` (alias `tag`), `os`, `architecture` (alias
`arch`) and explicit `all: true`. Environment remains the existing invocation
lane, supplied separately; it is not a reported node property or filter field.
Matching is ordinal case insensitive;
missing properties do not match. Bare names are checked for ambiguity within
authorized inventory. Fields use the existing client directory; arbitrary
device-JSON paths and remote property lookups are unsupported. Availability describes dispatch readiness,
not target membership.

`os` matches the exact last-known directory value. New native enrollments
report Linux, Windows or macOS from the actual platform. Older saved enrollment
metadata may say Unix (which does not distinguish Linux from macOS) or
Win32NT. Those values remain literal; the server does not infer Linux from
Unix. Inspect the preview's OS value or use explicit canonical tags when
selecting an older mixed inventory.

Queries are limited to 4,096 characters, 32 clauses and 128 operands (including
expanded parameter values); literals have at most 512 characters. Regexes have
at most 256 characters and use the culture-invariant case-insensitive .NET
nonbacktracking engine with a 50ms match timeout. Backreferences and lookaround
are unsupported. Complete evaluation has a 500ms budget and at most 10,000
authorized candidates/results; exceeding a bound fails instead of truncating.
There are at most 128 named binding parameters and 128 raw expanded operands
before deduplication. Preview pages are one-based with 1–100 rows; identity
lookup batches contain at most 1,000 IDs. Search is a separate substring
operation rather than a change to saved exact equality.

## Runtime inputs and schedules

Runtime targets declare a distinct ClientId, ClientIds, Hostname, Hostnames,
Tag or Tags input. ID and hostname lists select a union. Tags selects any listed
tag unless all-of is explicitly configured. Invocation values take precedence
over declared defaults; a missing required input fails before dispatch.

Fixed group/node/filter jobs cannot be widened by arbitrary invocation fields.
Ordinary REST Run accepts optional `selectedTargetAgentIds`: a nonempty,
unique subset of the complete resolved set, limited to 10,000 IDs. It changes
only that invocation, never the saved job. Foreign, duplicate or empty choices
fail before admission. For HA Groups it narrows the approved worker pool,
from which one healthy worker is chosen. Schema 2 binds this subset explicitly. A filter may deliberately reference a declared `@option.Name@`
operand. Values such as `.*`, commas, quotes, `+` or `!tags:` remain data and
cannot inject selector clauses. Script parameters and reserved target bindings
are separate.

Only a whole unquoted `@option.Name@` operand is a parameter reference; a
quoted reference is literal text. Names use ASCII letters/digits, `_` and `-`.

Steps can read server-owned `@node.id@`, `@node.hostname@`,
`@node.displayName@` and `@node.tags@`. Inputs cannot replace reserved node
context. Library-script parameter values use the existing typed argument and
JSON serialization path; `node.tags` is JSON array text. For direct shell steps,
use standalone tokens with an
explicit Bash or PowerShell runner, for example `printf '%s\n' @node.hostname@`
or `Write-Output @node.hostname@`; the server escapes each value as a shell
literal. Direct shell node bindings support a single ordinary command line;
multiline source, comments, here-documents, substitutions, quoted token
positions and Bash-backslash/PowerShell-backtick prefixes are rejected. Use
typed library-script parameters for general scripts and multiline content.

Schedules resolve current group/filter membership for each fresh occurrence.
A retry of the same persisted Hangfire occurrence reuses its admission; the
next firing has a distinct identity. A runtime-target schedule needs complete
saved/default bindings before activation. Existing timezone and schedule
controls retain their meaning.

Historical fixed-target schedules retain a narrow single-agent authority
adapter. Saving a schedule captures its current configuring identity; new target
modes require that saved authority. Retries of old serialized Hangfire dispatcher
methods also use the actual background-job occurrence identity.

Public Requests remain singular. Their resolved set must be exactly their
protected TargetAgentId. RuntimeInput may bind that ID; a wider or different
group/filter set is rejected.

## Concurrency, failures and timeouts

For explicit nodes, filters and runtime lists, Thread Count defaults to 1 and
is the maximum number of nodes running this job concurrently. HA runs always
have effective concurrency 1; saved Thread Count does not fan out an HA pool. Each slot owns one node's complete ordered enabled-step workflow.
The configured count is retained when it exceeds the target count; effective
concurrency also reflects available execution capacity.

The API host's `Jobs:Aggregate:MaximumActiveNodes` setting defaults to 100 and
accepts 1–100. It caps active slots per parent; with environment-variable
configuration, use `Jobs__Aggregate__MaximumActiveNodes`. It does not change
the job's saved Thread Count. Reviewed admission's configured target quotas
remain separate and may reject a whole request before execution.

For five targets, 1 finishes one node workflow before starting the next, 2 runs
up to two and refills a freed slot, and 100 runs up to five when capacity is
available. Steps within a node remain sequential with their existing retry and
step-failure behavior.

| If a node fails | Queue and parent outcome |
| --- | --- |
| Stop starting remaining nodes (default) | Persist the stop gate; unstarted nodes become Skipped / StoppedAfterNodeFailure. Already claimed nodes settle. The parent fails. |
| Continue on remaining nodes | Refill slots until all selected nodes settle. Any required node failure makes the parent fail. |

A claim committed before a stop/cancel decision is in-flight. Claims after
that decision are prohibited. Intermediate retryable step failures do not trip
the terminal-node gate.

Unresolved physical execution is distinct from known failure. An authenticated
unknown outcome or unresolved reconciliation boundary closes new admission
under either policy. Unstarted nodes become Skipped /
StoppedAfterUnknownExecution; existing children retain their execution IDs and
are drained/reconciled. A lost response is never proof that a process stopped,
and recovery never blindly replays uncertain work. The parent reports Unknown.

Expected/grace/hard native runtime limits retain their existing node/dispatch
scope. Waiting in the aggregate queue does not spend another node's native
execution deadline. A caller or Flow whole-action deadline still bounds its
action. Cancellation is durable: it closes the queue, cancels unstarted nodes,
uses existing native cancellation for active children, and waits for truthful
receipts/deadlines. Success requires every required node to succeed.

## Snapshots, history and output

Admission resolves, deduplicates and authorizes the complete target set before
physical dispatch. It freezes deterministic hostname/ID ordering, group
revision or selector/binding provenance, presentation metadata, definition
digest, target-plan digest and execution policy. New invocations resolve live
membership. Continuation, duplicate delivery and **Rerun original execution**
retain the original target set and definition, with current authority checked
again before dispatch.

One public parent contains distinct native single-agent child runs. Internal
children do not duplicate normal top-level history or completion triggers.
Aggregate parents have no invented singular AgentId. Run details are organized
as **Parent summary → Node hostname → Ordered steps → Output**. Equal step
ordinals on two clients do not identify the same output: node/native-run and
step-run identity are required. Older singleton deep links remain useful. An
unreported hostname is labelled unavailable; a friendly/registered label is
shown separately and does not become `node.hostname`.

Expand only the node and step being inspected. Node details/logs are loaded in
bounded pages. **Follow selected node** follows that node without jumping
between concurrent clients; manual inspection retains expansion through live
updates and reconnect. Full output retains current authorization and redaction.

| REST read | Identity and bounds |
| --- | --- |
| `POST /api/v1/jobruns/preview/{jobId}?page=1&pageSize=25` | Read-only current launch preview; complete target resolution/digest, one metadata page. Later pages and launch can bind `expectedTargetPlanDigest`. |
| `GET /api/v1/jobruns/{id}/target-plan?page=1&pageSize=25` | Original validated immutable plan, group revision/binding origins and both digests; no membership re-resolution or protected input values. |
| `GET /api/v1/jobruns/{id}/nodes?page=1&pageSize=25` | Stable admitted node order and status, at most 100 rows per page. |
| `GET /api/v1/jobruns/{id}/nodes/{agentId}/steps` | Only the persisted matching node's native steps. |
| `GET /api/v1/jobruns/{id}/nodes/{agentId}/steps/{stepRunId}/logs?limit=500&maximumBytes=65536` | Exact node/step-run output; optional opaque `cursor`, at most 1,000 rows and 64 KiB per page. |

Historical runs without a saved plan return `target_plan_not_recorded` from
the plan endpoint. A multi-node ordinal-only output request returns
`node_identity_required`; it never selects a first matching node.

Forward log pages return `nextCursor`, `hasMore`, `truncatedLogIds` and
`fullOutputUrl`. Pass `nextCursor` unchanged and URI-escaped to the next
request. It identifies the last row actually returned using sequence and
stable log ID, so stdout/stderr rows with equal sequence values remain
distinct. An oversized line is explicitly marked and has a full-output link.
The older tail-preview contract remains available for legacy history.

Reviewed execution binds the whole resolved plan, inputs, definition, execution
settings and effective policies. Group/tag changes cannot widen an approved
preview: confirmation either uses its unchanged approved set under current
authority or returns a stale-review conflict. Preview performs permitted reads
and simulations and dispatches no execution. HA previews report physical
`targetCount: 1`, candidate `workerPoolCount`, and the group name. Metadata
pages still cover the entire approved candidate pool. MCP confirmation remains
required and captures grants for every eligible candidate. Each distinct
eligible quota policy conservatively reserves one physical slot; a shared
policy reserves one slot for the run, not one per pool member.
An accepted reviewed admission also retains its protected original definition
and inputs before parent materialization, so recovery cannot re-resolve a
later group edit. An unresolved physical execution retains its configured
concurrency reservation until reconciled.

## Low-disk Flow

Create **Investigate low disk space**, choose Provided at run time, and declare
a required ClientId input. Add supported investigation steps, then publish a
Flow whose Run job action maps the alert's trusted `alert.agentId` value to
that runtime target. Execution-event mappings use `event.target.agentId` only
when the source has a real singular client. An aggregate-only source cannot
invent one.

The REST target declaration uses the versioned numeric enum contract:

```json
{
  "target": {
    "version": 1,
    "source": 4,
    "runtimeInput": { "kind": 1, "name": "ClientId" }
  },
  "executionPolicy": { "threadCount": 1, "nodeFailurePolicy": 0 }
}
```

The Run job action's typed binding is:

```json
{
  "targetBinding": {
    "kind": 1,
    "value": { "source": 1, "dataType": 1, "path": "alert.agentId" },
    "allOf": false
  }
}
```

Here target source 4 is RuntimeInput, target kind 1 is ClientId, input source 1
is Event, data type 1 is Text and node-failure policy 0 is StopRemaining. The
editor supplies the pinned job target/settings. List bindings use a typed JSON
array, not a comma-separated filter expression.

Flow publication pins the job definition/scripts, target policy/group ID,
binding schema and execution settings. Each fresh action resolves current
membership, then freezes its complete plan. Duplicate action admission and
recovery observe the original parent. One parent emits one canonical job
completion event. Node output is explicitly identified, bounded and redacted.
See [Flows](FLOWS.md) for versioning, causation and recovery budgets.

## Automated validation

The [HA admission fixtures](../../src/NetRatel/NetRatel.Tests/API/JobAggregatePostgresTests.Ha.cs)
check exact 100-run distributions, scale-out, shared cursor contention, offline
failover, duplicate delivery, original-worker rerun and historical compatibility.
The [aggregate PostgreSQL fixtures](../../src/NetRatel/NetRatel.Tests/API/JobAggregatePostgresTests.cs)
exercise five-node concurrency, native authority/receipt ownership, failure
policies, recovery, cancellation and deadlines using a synthetic gateway.
The [connected reviewed-admission fixture](../../src/NetRatel/NetRatel.Tests/API/JobAggregatePostgresTests.Mcp.cs)
uses real confirmation, protected recovery after source edits and current
credential verification. [Typed Flow binding checks](../../src/NetRatel/NetRatel.Tests/Flows/FlowJobTargetBindingsTests.cs)
and [responsive browser fixtures](../../src/NetRatel/NetRatel.Web.PlaywrightTests/MultiNodeWorkspacesAcceptanceTests.cs)
cover event targeting and the workspaces. Native deployment acceptance is
recorded separately using the installed candidate below.

## Owner functional acceptance

Run these checks after installing the verified beta in a disposable tenant.
Build/distribution integrity and automated fixtures do not establish native
deployment acceptance. Retain actual run IDs, plan digests, source revision,
node/step identities and relevant redacted OTEL/log evidence.

1. Enroll five synthetic clients with the same supported shell/platform and
   reported hostnames lab-node-01 through lab-node-05 (or substitute their
   actual reported hostnames throughout these checks). Tag each
   TestSystem, then save workers in an HA Group. Reload, rename the
   group and confirm its ID and membership persist. Search across pages and
   use the node editor to check retained choices. Run 100 fresh HA invocations
   with two healthy members and verify 50 each, then add two and verify 25 each
   for the next 100. Disconnect a worker and confirm new runs use survivors;
   with all workers offline, no run should be admitted.
2. Create an explicit five-node job with two supported sequential investigation
   steps: report node identity/start and inspect local free disk space/finish.
   Use a harmless bounded delay in the test script when overlap needs to be
   visible. Run with Thread Count 1, 2 and 100. Expect exactly five nodes and
   two ordered step executions per node, maxima 1/2/5 and immediate slot refill.
   The count 100 must remain configured after reload.
3. With count 2, make one active node's test step fail definitively while the
   other remains active. Under StopRemaining, the other settles and queued
   nodes show StoppedAfterNodeFailure. Under ContinueRemaining, the queue
   drains. Both parents fail; already running work is not falsely cancelled.
4. Preview `tags: TestSystem` with a page smaller than five and a matching
   hostname regex. Run and confirm all five targets, including an offline
   selected node's visible outcome. Empty/invalid/ambiguous selectors and one
   denied/foreign target must fail admission without dispatching a subset.
5. Edit group membership after admission and during a reviewed preview. The
   admitted run keeps its original set. An old confirmation cannot widen it.
   A fresh run uses current membership. Cancel a queued/running parent and
   inspect native receipts; a disconnected/unknown process must not replay.
6. Publish the low-disk Flow above. In Monitoring, create a disposable rule
   targeting only lab-node-03, choose **Disk free space**, its exact volume,
   **GiB** and that published Flow. To exercise targeting without filling a
   disk, set **Breach below** above the currently observed free space and
   **Recover above** higher again. Use a 120-second freshness budget and
   60-second breach/recovery holds, then preview cached resources and save
   with a test reason. Wait for fresh authenticated collections spanning the
   hold. Expect one parent, precisely that client, its hostname and the two
   ordered steps; further samples in the same firing episode must not create
   another parent. Retest an old published fixed-target Flow and singular
   Request. See [Monitoring](MONITORING.md) for evidence and reset semantics.
7. Expand equal step ordinals on different nodes; verify outputs, full-log
   links and reconnect preserve the correct node. Check one parent completion
   event, with no per-child multiplication of recovery flows.
8. Exercise tenant/group, job/Run and run-details workspaces at desktop, 768px,
   390px and 320px, light/dark, 200% zoom, keyboard and touch. Verify every
   section is reachable, focus visible, controls readable, selections retained
   and long output scrolls within its console.

For a reproducible Linux concurrency check, paste these one-line commands into
two ordered **Command** steps with an explicit **Bash** runner. They execute on
the selected native clients. The first step leaves a visible overlap window:

```sh
printf 'start node=%s hostname=%s\n' @node.id@ @node.hostname@ ; sleep 15
```

```sh
df -P / ; printf 'finish node=%s\n' @node.id@
```

For the failure-policy check, temporarily prepend the following condition to
the first command, replacing `FAILING_NODE_ID` with the stable ID of the first
hostname-sorted client. Confirm both slots become active before its delay ends;
increase the bounded delay if the test environment needs longer to dispatch.

```sh
if [ @node.id@ = 'FAILING_NODE_ID' ] ; then sleep 10 ; exit 7 ; fi ; printf 'start node=%s hostname=%s\n' @node.id@ @node.hostname@ ; sleep 15
```

Report native checks as pending until actually executed on the installed
candidate. [Release engineering](../deployment/RELEASES.md) describes the
separate authenticated build and distribution evidence.
