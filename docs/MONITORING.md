# Monitoring

Monitoring evaluates authenticated CPU, disk and explicitly selected service evidence. Rules, static groups, alert occurrences, operator commands and suppression belong to one application tenant. Memory remains available as telemetry; it is not a phase-one rule kind.

A series is identified by tenant, rule, agent and stable resource key. Disk series use normalized volume paths. Windows service names compare without case; systemd unit names compare exactly. Static groups select clients and do not create a second alarm identity. Overlapping client/group membership deduplicates. An all-client rule also covers future eligible registrations and requires `monitoring.targets.all` authority.

## Evidence and episodes

The existing telemetry stream carries metrics and Services chunks through one sequence cursor, one outbound writer and shared acknowledgements. The 16 KiB gRPC limit stays unchanged. Services frames must remain below 12 KiB, with bounded inventory assembly.

After existing authentication, presence, connection, epoch and sequence admission, the gateway awaits monitoring persistence before acknowledging an input or publishing its compatibility display update. The server's telemetry registration ID becomes the evidence stream ID; it is never accepted from the client. Replacing a telemetry stream within the same presence connection starts a new evidence boundary even when its sequence remains increasing. Persistence failure closes the stream; it cannot acknowledge a missing monitoring transaction and continue an old hold window.

CPU values are percentages. Disk free-space thresholds require explicit units and are evaluated in bytes. Complete Monitoring disk evidence requires paired exact total/free byte fields and valid collection identity, time and Complete quality, together with current authenticated evidence. Legacy rounded GiB telemetry remains compatible for transport and display; missing collection proof or exact bytes leaves Monitoring Unknown. Partial, stale or ambiguous evidence and a missing selected volume are Unknown rather than zero free space.

Service monitoring accepts complete, current-watch-revision evidence for the exact selected service. Missing can breach only when the collector supplies authoritative missing evidence. Partial, unsupported, stale, invalid or out-of-order evidence cannot imply stopped, zero usage or recovery. Inventory discovery and automatic start mode do not select watches.

Fresh accepted breaches move Healthy → Pending → Firing. The full breach hold requires continuing qualifying observations. Recovery uses its own threshold/hysteresis and fresh hold, moving Firing → Recovering → Resolved. Timers cannot complete a breach or recovery without a new observation. Unknown interrupts pending/recovery continuity and retains an existing unresolved occurrence. Reconnect or restart does not manufacture an occurrence or uninterrupted time over a gap.

An acknowledgement marks the same occurrence and does not rearm it. Manual clear requires a reason, ends that occurrence and fences the cleared evidence; a new occurrence needs new observations and a new full hold. Disabling a rule or removing a target suspends its applicability rather than claiming recovery. Semantic condition/target/hold/freshness edits require an explicit reset policy and evaluation revision. Rename/severity/action edits do not replay or replace a pinned occurrence.

Suppression is an audited tenant/rule/client/group/resource overlay with a reason and optional expiry. Evaluation continues. Expiry may release at most one currently fresh, previously undelivered active occurrence; it cannot replay historical resolved episodes or an already delivered occurrence.

## Persistence and display

A sequential per-client actor evaluates bounded state. PostgreSQL atomically fences configuration/state/evidence revisions and commits state, occurrence transition, immutable event, delivery intent and audit before any fanout. Unique series/open-occurrence/event/action identities and fenced delivery leases reject stale or concurrent writers. This is a single-node actor runtime; database concurrency protection does not claim multi-node actor high availability.

Monitoring events have durable tenant-scoped reads and an atomic display mirror in existing notification storage. The reserved `NetRatel.Monitoring.` namespace is excluded from every legacy global notification query, mutation, retry processor and stream. Notification read/deletion is separate from alert acknowledgement or resolution.

Rules without a validated immutable published flow remain display-only. The fallback published-flow provider returns an empty list and rejects selection. A real automation provider must validate exact flow authority; future occurrences pin the selected version and server-attributed configuring principal/credential. Existing occurrences retain their pinned definition. A local delivery key does not guarantee exactly-once external side effects.

A handed-off intent retains its real persisted flow run ID. Once the intent exceeds 15 minutes without a verified terminal outcome, the next claim records `DeliveryUnknown` with that same ID and removes it from dispatch claims. Bounded receipt polling continues to look up the existing run and can reconcile a verified terminal outcome, including after manual clear, disconnection or a newer occurrence. This lookup cannot enqueue another run. An interrupted handoff without a recorded run ID also remains explicit Unknown and uses exact event/version lookup to recover an already persisted outcome.

## Authorization and API

The stable permissions are `monitoring.read`, `monitoring.manage`, `monitoring.ack`, `monitoring.clear`, `monitoring.bypass` and `monitoring.targets.all`. Each operation authorizes its exact tenant/resource pair before disclosure or mutation. New dedicated MonitoringReader, MonitoringManager and MonitoringResponder role definitions do not widen existing persisted roles during upgrade. Operator identity comes from the verified stable NetRatel principal, and audited writes require a reason.

`GET /api/v2/monitoring/tenants` exposes only approved monitoring tenants. Tenant operations use `/api/v2/tenants/{tenantId}/monitoring`:

- Read `permissions`, `configuration`, `summary`, paged `series`/`events`, `clients` and `published-flows`.
- Preview exact deduplicated targets with `POST targets/preview`. Bounded detail includes honest cached support/unknown notes, total count and an explicit truncation flag. Preview does not collect from clients.
- Save/delete `rules/{ruleId}`, `groups/{groupId}` or `bypasses/{bypassId}` with an expected configuration revision and reason.
- Read `agents/{agentId}/series` or acknowledge/clear through `POST agents/{agentId}/rules/{ruleId}/ack` and `/clear`. The resource key is a query parameter so volume/service names are not path segments.

Shared HTTP DTOs live in `Shared/Contracts/Monitoring/MonitoringApiContracts.cs`. A durable save returns its saved revision even if service-watch reconciliation is pending; `WatchPolicyUpdatePending` indicates that the background renewal must finish, rather than asking the operator to repeat the save.

## Service policies and bounds

The watch source unions enabled, tenant-owned resolved service rules for the client. It rejects a selection above 64 names or the encoded policy budget instead of silently selecting a subset. Removing/disabling the last service rule clears the selection. Policy selection revisions are persisted under bounded striped coordination; renewal can retain the selection revision while extending expiry. Configuration changes reconcile currently connected registrations, and bounded background renewal operates independently of viewers. Every command rechecks the captured server registration and uses the existing outbound writer.

A configuration save attempts at most 64 registrations within five seconds; unfinished activation is reported by `WatchPolicyUpdatePending`. Renewal runs every 30 seconds through keyset batches of 128 active Services registrations, with eight concurrent attempts, one second per client and a 15-second batch deadline. The continuation cursor advances through every registration, including failed attempts, instead of repeatedly favoring the first clients. Eligibility checks use an indexed point query during renewal. Selection policies expire after one hour if renewal cannot finish.

API validation budgets freshness for the default cadence and jitter: at least 10 seconds for CPU, 65 seconds for disks and 35 seconds for 30-second service watches. Slower configured collectors can still become Unknown; freshness is never inferred over a gap.

Monitoring request bodies, configuration aggregates and response pages are capped at 4 MiB. Paged durable reads use continuation cursors when their byte budget fills; full oversized responses and writes fail visibly rather than truncate. Other principal limits include 256 rules and 128 groups per tenant, 4,096 eligible target clients, 1,024 series per client, 16,384 series per tenant, 200 rows per read, 50 outbox claims, eight delivery attempts and 90-day history retention. Capacity limits can leave evidence Unknown and must never fabricate recovery.

Acceptance includes TimeProvider-based evaluator tests, actual gRPC acknowledgement/failure barriers, exact tenant/resource API negatives, legacy notification disclosure/mutation guards and real PostgreSQL rollback/concurrency/restart/lease tests. CI does not enroll or manage live OS clients.
