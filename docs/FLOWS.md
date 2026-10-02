# Monitoring flows

Flows are part of the `0.1.1-beta.1` development work. RC.17 remains the client bootstrap release candidate. The Flows editor is useful for bounded drafts and previews; automatic incident delivery requires an authorized connector whose receiver provides verified atomic idempotency. The currently examined RatelDesk normal create endpoint does not provide that contract, so its connector is visibly unavailable and cannot be published as an executable dependency.

## Editing and publishing

Open **Signals → Flows**, choose an authorized tenant, and create the named incident template. The real VeloxDev canvas supports moving nodes, connecting typed ports, deleting edges/nodes, pan, zoom, fit and minimap. The property forms offer a keyboard path to node positions, conditions, mappings and connections. Save retains stable node IDs, properties, positions and viewport in NetRatel's canonical schema; a canvas or CLR object is never persisted.

The initial catalogue is deliberately small:

1. Alert raised.
2. An optional typed condition.
3. Map incident fields.
4. RatelDesk: Create incident.

A published graph has one reachable acyclic path, at most one condition, one mapping and one external action. Drafts can remain incomplete while being edited. Publish requires a complete valid path and an enabled, currently authorized connector with executable receiver capability. Publish pins the connector configuration revision and creates an immutable version. Later draft edits and credential rotation cannot silently change the semantic target or fields of an existing version/run. Clone creates a disabled draft with new node IDs.

Read, draft save, validate and dry run never construct an executing workflow or create an incident. Dry run is pure DTO evaluation and shows mapped fields or an explicit condition-false skip. Publish stores a version; monitoring's durable event ingress is the execution boundary. No browser execution endpoint is exposed.

Allowed mapping placeholders are `{ruleName}`, `{clientName}`, `{resource}`, `{metric}`, `{severity}`, `{value}`, `{serviceState}` and `{occurredAt}`. Numeric comparisons use a finite numeric value. Text comparisons use exact ordinal equality/inequality. Arbitrary code, CLR type names, SQL, shell commands, webhooks, loops, runtime redirects, schedules and remediation are unavailable.

## Authority and durable execution

The API evaluates `flow.read`, `flow.edit`, `flow.publish` and `flow.execute` together with the selected tenant. Existing roles are not silently widened; deployments must explicitly grant the new permissions. A flow/immutable-version/run ID from another tenant is insufficient to read or change it.

Monitoring lists immutable published versions only for an exact tenant with both `flow.read` and `flow.execute`. Selecting or changing a rule action requires those permissions; editing other fields while retaining its selected version preserves the original configuring identity. The bridge persists the real run ID under the exact outbox lease. Recovery of a known run polls it, and a lost handoff response uses the exact tenant/event/version lookup without another enqueue.

Monitoring supplies the persisted configuring principal and optional integration credential on its pinned event. Backend execution uses that authority, with no borrowed Operator or global system principal. Current principal existence, local-account enablement, tenant permission and integration credential scope/revocation/expiry are checked again before a side effect. Flow enablement and monitoring's current occurrence/suppression guard are checked before preparation and again immediately before the durable send transition. Connector dispatch checks its current owner, grants, enablement, independent credential availability and pinned configuration revision.

Each run creates a separate actual VeloxDev Core compiler/model/helper/runtime context. The backend worker can continue while every browser is closed. The initial topology is one bounded worker; no additional broker or cluster is required.

Before the first send, the database commits a stable action key and the exact prepared semantic payload, including the resolved organization, customer, assignment, categories, priority, plain title/description and source occurrence metadata. A database transaction marks the action `Dispatching` under the current run lease before the dispatcher is called. The lease token, worker identity, increasing fence and expiry protect every receipt/outcome write. A stale worker cannot overwrite a reclaimed run.

Recovery preserves these distinctions:

| Durable evidence | Recovery behavior |
| --- | --- |
| No send started | Current admission and the exact persisted payload may proceed under a new lease. |
| Successful incident receipt committed | Finalize the run without sending again. |
| A send started, with no receipt and no verified safe replay | Record `DeliveryUnknown`; do not retry blindly. |
| Verified receiver replay contract | A permitted retry retains the same key and semantic payload. |
| Explicit safe transient failure | Retry within the bounded attempts/backoff/age budget. |
| Suppressed, revoked, disabled, unavailable or invalid dependency | Preserve an explicit failed/skipped outcome. |

Exhausting a retry budget preserves a verified success receipt or an ambiguous send instead of overwriting it with a generic failure. An event aimed at a known version of a disabled flow records a real failed run, and re-enabling the definition does not resurrect that event. Metric recovery does not automatically resolve a helpdesk incident.

The initial bounds are eight nodes, seven edges, 32 KiB canonical graph, 8 KiB event, 24 KiB prepared action, 20-second execution deadline, 60-second lease, five action attempts and a 24-hour retry age. There are at most 128 definitions/tenant, 128 versions/definition, 256 active runs/tenant and 4,096 retained runs/tenant. Run history reads return at most 100 rows. Detailed terminal history is pruned after 90 days; ambiguous deliveries retain their keys and evidence. After pruning, an original event older than the 24-hour ingress window cannot start a new action. The monitoring bridge must retain the original immutable event timestamp when redelivering. Receiver deduplication retention must cover the supported retry window before safe replay can be enabled.

## VeloxDev compatibility and notices

NetRatel pins the actual published `VeloxDev.Core` and `VeloxDev.Razor` packages to **8.0.0**. The published source metadata points to upstream commit `784efa2f28a25568a9d37a263035b92d3873f068`. Core's net5.0 and Razor's net6.0 assets compile and run in the existing .NET 10 InteractiveServer application with MudBlazor 9.11.0.

The adapter uses real native create/connect/move commands and waits for the matching completion event before replaying a following edge. Library command task completion alone does not guarantee model mutation has finished. Replacement canvases get unique instance IDs and keyed component lifetimes; callbacks and JS imports are fenced by cancellation and the selected tenant/flow generation. Captured geometry is canonical finite data. The backend adapter reconstructs only the four reviewed node types and uses the actual Core compiler/runtime; it never calls the library's arbitrary-type deserializer.

The native Razor CSS/JS assets remain under `_content/VeloxDev.Razor/` and work under the application's base path. The full upstream MIT copyright/permission notice is in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and is copied into build/publish output.

Focused verification uses `NetRatel.Tests.Flows.*` for canonical validation, actual Core context isolation, API authorization/inertness/body limits and real PostgreSQL migrations/restarts/receipt/fencing/retention. PostgreSQL cases use the repository's standard Testcontainers fixture. Web component and Chromium tests exercise the actual editor, native connections and drag/layout/viewport save/reload, theme/zoom/mobile reflow and inert preview counters. Tests use deterministic receiver fixtures; they do not create tickets on a live helpdesk.
