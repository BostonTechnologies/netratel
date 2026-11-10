# ADR 0006: immutable job target plans and durable node orchestration

Status: accepted, 10 November 2026; HA admission amended by [#716](https://github.com/BostonTechnologies/netratel/issues/716). Scope: [execution groups and multi-node jobs](https://github.com/BostonTechnologies/netratel/issues/696).

## Context

Native run authority, cancellation, physical dispatch receipts and protected execution inputs bind one AgentId to one execution. The initial schema-one group assumption was corrected by #716: an HA Group is an explicit tenant-owned availability pool, from which one healthy authorized worker runs a fresh invocation. Explicit nodes, filters and runtime lists retain multi-node execution. Reusing a native run for several agents would confuse authority, callbacks, output and recovery.

## Decision

An additive version 1 `JobTargetDefinition` selects exactly one source: ExecutionGroup, ExplicitNodes, NodeFilter or RuntimeInput. An absent target definition retains the historical AgentId contract. A supplied definition replaces the previous target and clears legacy AgentId and ClientIdentity, including when updating a fixed-target job to RuntimeInput. Supplying both a definition and AgentId is invalid. Existing enum values and native receipt formats are unchanged. New nullable properties are omitted from old protected snapshots so their serialized definition digests remain valid.

The server resolves and authorizes the complete set before admitting a run. `JobResolvedTargetPlan` copies and deduplicates AgentIds, orders nodes by case-insensitive ordinal hostname (or separate registered/display label when unreported) and stable AgentId, and retains presentation metadata, source, group revision, definition digest, binding origin and execution policy. Its explicit schema 1 canonical JSON produces a SHA256 target-plan digest independently of serializer options. Input values remain in the protected execution envelope; binding provenance contains names and origins only. An unreported hostname stays empty; a presentation fallback never becomes hostname authority. Target context is server-owned. Fresh runs resolve current membership; continuation, reviewed confirmation and rerun-original use the admitted plan and recheck current authority.

`JobRuns` retains existing public IDs and adds an aggregate flag and nullable parent link. An aggregate has no AgentId. Each `JobRunNodes` entry has a unique parent plus AgentId, unique parent ordinal and a distinct nullable native run identity. A claim binds the native identity before dispatch. The native child retains its own single-agent run control, owner epoch, attempt identity and receipt. Children remain accessible through node-specific details and output routes and are excluded from normal top-level history and aggregate lifecycle subscriptions. Existing singleton history and links remain valid.

`JobAggregateControls` stores a revision fence and durable stop/cancel gate. PostgreSQL serializes slot claims and gate transitions; no transaction spans native execution. A claim committed before the gate closes is active work; no claim may commit afterward. Native acceptance with a lost response is not evidence of failure or termination. Recovery may resume NeverStarted targets. Claimed/admitted or ambiguous native identities are observed and reconciled through their original controls and receipts, never replayed as new physical execution.

For explicit nodes, filters and runtime lists, Thread Count defaults to 1 and accepts 1 through 100. It limits whole node workflows, not individual steps or simultaneous invocations of the same job. The configured count is retained; effective concurrency is bounded by target count and explicit capacity. An HA invocation has one physical target and effective concurrency 1 regardless of its saved Thread Count. Enabled steps remain sequential inside each node.

## HA admission amendment

Fresh HA resolutions use target-plan schema 2. Preview binds the complete eligible authorized worker pool and reports one execution target; it does not choose a worker or advance balancing. During admission, a PostgreSQL group-row lock serializes the durable round-robin cursor across API instances. Current committed connection leases and authentication expiry determine worker availability. Cursor advance, the selected-worker plan, parent and single-node queue commit in the same transaction. Membership revisions do not change when the cursor rotates. Unchanged healthy pools divide 100 admissions 50/50 across two workers or 25 each across four; this balances invocation counts rather than CPU time.

The selected plan binds its one worker to the approved frozen pool and digest. Schema-2 HA hashes exclude only volatile online hints; group revision, identities, metadata, definition and settings remain bound. Validated invocation-only subsets also bind their exact identities without changing the saved policy. Reviewed execution retains confirmation, current per-agent grants and atomic quotas across every eligible policy. Recovery compares the persisted selected-worker proof against the approved policy; it never chooses a replacement after physical admission. Duplicate delivery and original-execution reruns keep their selected worker.

The `ExecutionGroup = 1` wire identifier and routes remain compatible. Schema-one hashes and already accepted admissions retain their original frozen targets and behavior; the correction does not rewrite history. An empty healthy pool fails admission before creating a parent. Native uncertainty, cancellation, timeout and receipt safeguards below continue to apply to that one worker.

## Deadlines and terminal outcomes

Queue wait is separate from native node/step runtime. Existing expected/grace/hard dispatch deadlines continue to start at their existing native boundaries. They do not become a shared parent timer that expires later queued nodes. A caller or Flow action deadline, when supplied, bounds its whole action and may close the queue and cancel active children.

StopRemaining closes the queue after an accepted terminal node failure and marks never-started targets Skipped with `StoppedAfterNodeFailure`. Already claimed children settle before parent completion. ContinueRemaining continues known work but fails the parent if any required node fails. Retryable intermediate step failures do not close the node gate.

Unresolved physical occupancy closes admission under either policy. Never-started nodes are Skipped with `StoppedAfterUnknownExecution`; active nodes drain or reconcile. Unknown physical outcome maps to the existing parent Unknown status. Cancellation is durable, closes the queue and invokes existing native cancellation; a request or lost transport response does not prove the process stopped. Terminal aggregation retains per-node facts and uses Unknown before cancellation, timeout and known failure; success requires every required node to succeed. Caller cancellation and deadline reasons remain distinct. A terminal parent is published only after all admitted children have truthful terminal projections or a conservative unresolved outcome.

One canonical job completion event belongs to the parent. Internal native children do not produce duplicate aggregate recovery triggers. An aggregate-only event has no singular client; a runtime ClientId binding requires a real trusted event/node identity. Public Requests remain singular and reject a plan unless its only node is the protected TargetAgentId.

## Persistence and compatibility

New columns and tables are additive. Historic incomplete rows retain null tenant/agent fields; migrations do not invent target authority. Unique constraints prevent duplicate queue entries and native identities. Agent identity remains its stable Id; a mutable unique index over TenantId and Id plus PostgreSQL composite foreign keys retains group/node tenant integrity without making TenantId an EF key. Unreferenced legacy agents retain their existing tenant-change behavior. Tenant consistency is checked by database constraints without turning nullable historical JobRun tenant data into a required field. Protected execution material and output redaction continue to use the existing envelope and native services.

These contracts are the foundation for target/group resolution, orchestration, Flows and tooling. They do not themselves claim native deployment acceptance; that evidence is recorded separately from build and distribution integrity.
