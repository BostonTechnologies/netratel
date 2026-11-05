# Monitoring flows

Flows support bounded drafts, previews, RatelDesk incidents and Discord/Slack notifications. Automatic incident delivery requires an authorized RatelDesk connector whose receiver provides verified atomic idempotency. A normal create endpoint alone does not establish that contract. Discord and Slack use separately configured incoming webhook targets; see [notification connectors](../integrations/NOTIFICATION-CONNECTORS.md).

## Editing and publishing

Open **Signals → Flows**, choose an authorized tenant, and create the named incident template. The real VeloxDev canvas supports moving nodes, connecting typed ports, deleting edges/nodes, pan, zoom, fit and minimap. The property forms offer a keyboard path to node positions, conditions and mappings. Visible input/output ports provide keyboard and touch connection authoring: activate an output, then a compatible input, or Cancel connection. Select a saved curve to inspect or remove it; read-only viewers allow inspection. Saved connections use curved paths and share Core hit-test geometry. Pointer-down immediately draws a dashed browser-local preview; compatible inputs highlight, invalid drops explain the rejection, and cancellation clears the preview. Pointer movement and hover do not capture the graph or send rendering frames through the server. Each committed drop uses the same authoritative native command/validation path as keyboard and touch.

Save retains stable node IDs, properties, positions and viewport in NetRatel's canonical schema; a canvas or CLR object is never persisted.

The compact toolbar includes a searchable, collapsible node library and **Fullscreen workspace** for both drafts and immutable viewers. Fullscreen covers the viewport; the browser Fullscreen API is requested directly from the click where supported, with a full-window fallback. The existing dialog and popover providers stay inside the native fullscreen document. Entering or leaving keeps the same canvas adapter, selected node, draft and pan/zoom. Escape keeps the editor open; use its explicit Close action to leave.

Preview sample data opens in a modal dialog and does not reduce the canvas height. Closing the dialog returns focus to Preview. A captured trace opens a floating playback inspector with play/pause, previous/next, restart, speed and per-node Input/Output/Outcome. Selecting a node also selects its captured step. Pure nodes expose calculated values; the trace stops at the first external action and labels its output unavailable. No AI findings or action success is invented. Large snapshots are explicit bounded text excerpts, and missing, captured null and captured empty values remain distinct.

Evaluation uses the current draft and never saves, publishes or dispatches an action. Editing the draft or changing tenant/flow removes its old trace. Playback moves through captured data entirely in the browser, respects reduced motion, and pauses while hidden or offscreen; backward stepping performs no evaluation or network command.

The initial catalogue is deliberately small:

1. Alert raised.
2. An optional typed condition.
3. Map alert fields.
4. An optional AI: Investigate alert step with a bounded deadline, failure disposition and tenant guidance.
5. RatelDesk: Create incident, or an action group containing one to eight supported independent actions. AI may also end an AI-only path.

A published graph has one reachable acyclic path, at most one condition, one mapping, one sequential AI step and one final action or action group, or an AI-only final step. Drafts can remain incomplete while being edited. Publish requires a complete valid path and enabled, currently authorized connectors with the capability required by each action. Publish pins every connector configuration revision and selected optional prompt revision and creates an immutable version. Later draft edits, guidance edits and credential rotation cannot silently change the semantic target or fields of an existing version/run. After editing a target, choose **Use updated target** in the action settings and republish any flow that should use it. Clone creates a disabled draft with new node IDs.

Select an existing incident action and use **Add more actions** to retain that incident target while adding notification actions. Each child has a stable ID and its own target, durable delivery state and receipt. The mapped title and description feed every action; priority applies to incident actions. One failed destination does not prevent the other children from running. History shows each result and reports a partially succeeded run when some destinations succeed and others fail or remain uncertain. A safe retry resumes only the children that still need delivery; successful children are not sent again.

Read, draft save, validate and dry run never construct an executing workflow or create an incident. Dry run is pure DTO evaluation and shows mapped fields or an explicit condition-false skip. Publish stores a version; monitoring's durable event ingress is the execution boundary. No browser execution endpoint is exposed.

Allowed mapping placeholders are `{ruleName}`, `{clientName}`, `{resource}`, `{metric}`, `{severity}`, `{value}`, `{serviceState}`, `{occurredAt}`, `{offlineSummary}`, `{offlineCount}` and `{offlineClients}`. Offline events automatically include the bounded outage summary in incident descriptions and notifications even when an older template does not contain the new placeholders. Numeric comparisons use a finite numeric value. Text comparisons use exact ordinal equality/inequality. Arbitrary code, CLR type names, SQL, shell commands, arbitrary webhook URLs in graphs, loops, runtime redirects, schedules and remediation are unavailable.

## Authority and durable execution

AI discovery supports `{aiSummary}`, `{aiEvidence}`, `{aiRecommendations}`, `{aiStatus}`, `{aiObservedAt}` and `{aiInvestigationId}` in mappings. **Alert raised → Map alert fields → AI: Investigate alert → RatelDesk: Create incident** waits for the exact automated turn's persisted terminal result before incident delivery. A durable continuation resumes at the waiting node rather than replaying the graph. Approval waits remain visible. A new investigation snapshots the current tenant default; published optional guidance stays pinned. Failure policy can stop the flow or continue with the original alert and an accurately labelled unsuccessful outcome. The final incident description adds a labelled investigation reference and discovery section even when mapping precedes AI. Original measured alert facts remain immutable.

Execution-source investigations use the saved flow publisher and credential in the actual tenant, the exact accepted action/message and its native target. They do not claim a Monitoring occurrence or borrow the Netclaw connection owner's permissions. Source admission can outlive the yielded Flow worker lease; current publisher grants, the accepted source/intent, published version and enabled flow are rechecked before a turn starts.

Execution-source AI is diagnostic-only. The current native Netclaw 0.28.2 session protocol cannot enforce or prove a diagnostic-only tool policy for a saved turn. The source worker therefore returns **Unsupported**, code `netclaw-investigation-tool-policy-unavailable`, before `SendMessage`: no AI turn starts and findings remain unavailable. A prompt, connection label or later tool trace does not supply this capability. Existing alert investigations retain their established behavior and configured daemon approvals. Permitted pinned repairs remain separate canonical script/job graph actions with their existing authority and approval policy; an AI completion or repair command is not verified recovery. See the [protocol evidence and integration boundary](../evidence/flows-investigation-policy-20261105.md).

The API evaluates `flow.read`, `flow.edit`, `flow.publish` and `flow.execute` together with the selected tenant. Existing roles are not silently widened; deployments must explicitly grant the new permissions. A flow/immutable-version/run ID from another tenant is insufficient to read or change it.

Monitoring lists immutable published versions only for an exact tenant with both `flow.read` and `flow.execute`. Selecting or changing a rule action requires those permissions; editing other fields while retaining its selected version preserves the original configuring identity. The bridge persists the real run ID under the exact outbox lease. Recovery of a known run polls it, and a lost handoff response uses the exact tenant/event/version lookup without another enqueue.

Monitoring supplies the persisted configuring principal and optional integration credential on its pinned event. Backend execution uses that authority, with no borrowed Operator or global system principal. Current principal existence, local-account enablement, tenant permission and integration credential scope/revocation/expiry are checked again before a side effect. Flow enablement and monitoring's current occurrence/suppression guard are checked before preparation. The accepted-write path uses the caller's PostgreSQL connection and transaction, admitting the exact Flow lease and fresh authority/Monitoring state under the existing configuration, owner, evidence, latest-series, outbox and run/action locks. Entry and late checks use the later of trusted time and live PostgreSQL `clock_timestamp()`. Admission is rechecked before `SaveChanges` and after awaited `SaveChanges`, before commit; denied admission, cancellation, errors or a missing provider/transaction leave the write rolled back. Connector dispatch checks its current owner, grants, enablement, independent credential availability and pinned configuration revision.

Each run creates a separate actual VeloxDev Core compiler/model/helper/runtime context. The backend worker can continue while every browser is closed. The initial topology is one bounded worker; no additional broker or cluster is required.

Before the first send, the database commits a stable action key and the exact semantic draft, pinned target revision and source occurrence metadata. Incident preparation additionally resolves the organization, customer, assignment, categories and priority into its durable receiver payload. A database transaction marks each action `Dispatching` under the current run lease before its dispatcher is called. The lease token, worker identity, increasing fence and expiry protect every receipt/outcome write. A stale worker cannot overwrite a reclaimed run.

Recovery preserves these distinctions:

| Durable evidence | Recovery behavior |
| --- | --- |
| No send started | Current admission and the exact persisted payload may proceed under a new lease. |
| Successful incident receipt committed | Finalize the run without sending again. |
| A send started, with no receipt and no verified safe replay | Record `DeliveryUnknown`; do not retry blindly. |
| Verified receiver replay contract | A permitted retry retains the same key and semantic payload. |
| Explicit safe transient failure | Retry within the bounded attempts/backoff/age budget. |
| Suppressed, revoked, disabled, unavailable or invalid dependency | Preserve an explicit failed/skipped outcome. |

Incoming webhooks do not provide the verified replay contract used for RatelDesk incidents. Notification rate-limit rejections can be retried within the run budget, but a timeout or ambiguous response after a send is recorded as `DeliveryUnknown`. Recovery does not blindly post the notification again.

Exhausting a retry budget preserves a verified success receipt or an ambiguous send instead of overwriting it with a generic failure. An event aimed at a known version of a disabled flow records a real failed run, and re-enabling the definition does not resurrect that event. Metric recovery does not automatically resolve a helpdesk incident.

The initial bounds are eight nodes, seven edges, 32 KiB canonical graph, 8 KiB event, 24 KiB prepared action, 20-second execution deadline, 60-second lease, five action attempts and a 24-hour retry age. There are at most 128 definitions/tenant, 128 versions/definition, 256 active runs/tenant and 4,096 retained runs/tenant. Run history reads return at most 100 rows. Detailed terminal history is pruned after 90 days; ambiguous deliveries retain their keys and evidence. After pruning, an original event older than the 24-hour ingress window cannot start a new action. The monitoring bridge must retain the original immutable event timestamp when redelivering. Receiver deduplication retention must cover the supported retry window before safe replay can be enabled.

## Schema transition

The compatible schema-two contracts are available for the overhaul while the current editor,
save/publish routes and monitoring runtime remain on schema one. A draft upgrade is explicit;
published versions, configuration hashes and running schema-one continuations are not rewritten.
The new contract separates execution-event triggers from script/job/rerun actions and defines
exclusive named outcomes, typed input bindings, stable edge IDs and finite optional routes.
Its 32-node/64-edge/128 KiB budget is independent of existing execution and receipt bounds.
The [API contract reference](../contracts/API_REFERENCE.md#flows) describes this transition.
Execution activation and authoring of those new actions require their durable runtime integration.

## VeloxDev compatibility and notices

NetRatel pins the actual published `VeloxDev.Core` and `VeloxDev.Razor` packages to **10.0.1**. The published source metadata points to upstream commit `f2ac8a79de0448bcb29398a5ec77b13a5985b922`. Both packages supply the net8.0 assets selected by the .NET 10 InteractiveServer host with MudBlazor 9.11.0. Compatibility is checked by the assembled stage's normal build and focused runtime/editor tests; browser acceptance covers native gestures and geometry.

The adapter uses real native create/connect/move commands and waits for the matching completion event before replaying a following edge. Library command task completion alone does not guarantee model mutation has finished. Replacement canvases get unique instance IDs and keyed component lifetimes; callbacks and JS imports are fenced by cancellation and the selected tenant/flow generation. Captured geometry is canonical finite data. The backend adapter reconstructs only the reviewed node types, including the bounded action group, and uses the actual Core compiler/runtime; it never calls the library's arbitrary-type deserializer.

The native Razor CSS/JS assets are referenced under `_content/VeloxDev.Razor/` using the application's base path. The full upstream MIT copyright/permission notice is in [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) and is copied into build/publish output.

Focused verification uses `NetRatel.Tests.Flows.*` for canonical validation, actual Core context isolation, API authorization/inertness/body limits and real PostgreSQL migrations/restarts/receipt/fencing/retention. PostgreSQL cases use the repository's standard Testcontainers fixture. Web component and Chromium tests exercise the actual editor, native connections and drag/layout/viewport save/reload, theme/zoom/mobile reflow and inert preview counters. Tests use deterministic receiver fixtures; they do not create tickets on a live helpdesk.
