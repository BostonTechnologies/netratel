# Flows

Flows connect monitoring alerts and execution outcomes to typed decisions, investigation, permitted library scripts or jobs, and incidents or notifications. Automatic RatelDesk delivery requires an authorized connector with verified atomic idempotency. Discord and Slack use separately configured incoming webhooks; see [notification connectors](../integrations/NOTIFICATION-CONNECTORS.md).

## Create and configure a draft

Open **Signals → Flows** and choose an authorized tenant. New flow retains the alert template and blank schema-one draft choices. When the server supports schema-two drafts, choose an execution template, give the flow a meaningful name, and configure its source, definitions, targets and destinations. Templates are disabled, unpublished drafts. Creation, editing, validation and preview perform no real actions. Publishing creates an immutable version; enabling admits matching future source transitions under that version's configuring authority.

The templates cover:

| Template | Configure before publication |
| --- | --- |
| Alert to incident | The existing alert mapping and RatelDesk destination; optional investigation remains available. |
| Job or request failure to incident | Execution scope, failure filter, supported investigation connection and RatelDesk destination. |
| Library script repair and linked rerun | Selected source scripts, a permitted pinned repair script and unresolved-incident destination. |
| Job success to follow-up script | Job scope, pinned follow-up script, explicit target/input choices and failure destination. |
| Job success to notification | Job scope and named notification destinations. |
| Request timeout and unknown outcome | Request scope and incident destination; distinct messages without automatic reruns. |

The server advertises draft editing and execution support separately. Publish explains when the selected schema is not executable. Unconfigured selections remain visible in a draft. Complete validation and publication reject missing required selections, inputs, outcome routes or connector references. Selected source definitions never silently become an all-tenant subscription. A clone is another disabled draft with independent node, edge and action identities.

## Editor and viewer

The compact toolbar contains save, validation, preview and fullscreen controls. The searchable, collapsible palette leaves most space for the canvas; select a node to edit its configuration. Node headers identify their category and source or action, with useful definition, target and configuration summaries. Typed condition cards show their configured source, comparison and value; full settings remain in the inspector. Destination selectors configure actual integrations and credentials.

**Fullscreen workspace** fills the viewport in both editor and immutable viewer modes. The browser Fullscreen API is used where supported, with a full-window fallback. Dialogs, tooltips and popovers stay inside that root. Entering or leaving preserves the draft, selection, focus and pan/zoom. Escape cancels a connection or route gesture first and never discards the editor; use its explicit Close action.

Visible named ports are the connection controls. Drag an output to an input, or activate an output with keyboard/touch and then activate a compatible input. A dashed curved line appears before pointer release. Compatible targets highlight; invalid targets explain the rejection. Cancel clears the gesture without saving an edge. The same draft validation applies to every authoring path.

Select a saved curve to inspect or remove it. In schema two, **Add route point** adds a saved bend; drag a point, adjust its coordinates, or use arrow keys (Shift moves by ten units). Tab leaves a handle normally. **Reset route** returns to automatic curves. Up to eight finite world-coordinate points are saved per edge; selection, hover, ghost lines and playback are transient. Save/reload retains stable node/edge identities, routes, positions and viewport. Pan, zoom, fit, resize and the minimap use the native Velox surface.

**Undo graph edit** and **Redo graph edit** retain up to 32 local snapshots, coalescing a name edit or physical canvas gesture. Ctrl+Z, Ctrl+Shift+Z and Ctrl+Y use graph history when focus is outside an editable field; text fields retain their native undo behavior. Restoring an edit keeps the current pan/zoom and saved definition revision, and never saves, publishes or runs an action. Equivalent JSON object order and numeric spellings do not create a false schema-two edit or undo entry; captured null survives restoration. Incomplete unsafe intermediate forms are skipped on restoration. Reloading or changing tenant/flow starts a new local history.

Fit completes its native zoom and final positioning before a preview captures the viewport. Repeated Fit requests share the active operation. A failed or cancelled fit stays explicit; entering preview does not save the draft.

## Triggers, actions and branches

A graph has one trigger and an acyclic sequence of exclusive decisions. **Execution event** subscribes to job executions, library-script executions or requests. It is a different category from **Run job** and **Run library script**, which create work. Script events describe actual executions, not edits to a library definition.

Use the Job executions, Library script executions or Requests preset, then choose source families and genuine terminal outcomes. Scope the trigger to all matching executions in the authorized tenant or selected jobs/scripts. Searchable names retain their source family, so a job and a script with the same numeric ID are distinct. Agent and request-kind filters narrow supported sources. Request source-system values match exactly and case-sensitively; no selection matches every supported value. A request may match its proven backing job/script definition while retaining the Request family. A job action shows and retains its own bound tenant/agent; it does not substitute the triggering event's client.

Combined subscriptions may aggregate correlated job/request/script facts for the same accepted execution attempt and outcome. Each flow version and trigger is admitted independently. A known job-backed request or script step in an explicitly combined aggregate waits for its parent's complete terminal result, even when the Job source family is not selected; a request-only admission failure can trigger immediately without a job. Separate attempts or different outcomes remain distinct. Current enabled interval and event age are checked before choosing the deterministic bounded group.

History shows up to 16 source-event references frozen at admission. A separate notice means additional eligible references were already known then; reaching 16 alone does not imply truncation. Later delivery receipts never rewrite this original source snapshot. Missing legacy references and references withheld by current source permission are labelled unavailable. Request target labels come from the same tenant's enrolled agent directory at the transition and retain those names after later edits.

| Outcome | Meaning and operator response |
| --- | --- |
| `succeeded` | A committed native terminal success. Request `Success` and `Completed` normalize to success while retaining the raw status. |
| `failed` | A definitive failure, including a completed script with an authoritative nonzero exit result. |
| `timed_out` | An authoritative deadline result; a mutable request label alone is insufficient. |
| `cancelled` | A committed cancellation. |
| `unknown` | Completion or delivery is unconfirmed. Preserve evidence and investigate; do not blindly rerun. |
| `unsupported` | Required replay material, execution support or enforced investigation capability is unavailable. |
| `denied` | Current source, execution, credential or target authority does not permit the action. |

Named outcome ports choose one next node. Conditions choose true or false; mappings produce a result. Alternatives may converge on one downstream node as an exclusive token, not a parallel join. Every declared outcome needs an explicit route in a published graph. Use **Finish** for an intentionally terminal branch. An independent notification action group retains each destination's stable identity and delivery receipt.

Typed inputs come from allowlisted event facts, earlier typed outputs or literals. Native parameters declared `secret_reference` (case-insensitive, with no whitespace trimming or aliases) accept a target-local reference name using `[A-Za-z_][A-Za-z0-9_]{0,127}`. Publication and dispatch check the actual pinned native parameter type; only the reference name reaches protected canonical admission, while secret values remain on the target. Checkpoints, trace snapshots and preview never expose that reference's value. Definitions retain their parameter names/types and required inputs. Missing data is unavailable; captured null, an empty string, zero and false remain distinct. A value from an earlier node must be available on every incoming path. Typed mapping aliases use `{input.name}`; the title/description token picker separates original event facts from AI findings. Additional investigation bindings add named event facts or guaranteed earlier outputs without replacing original evidence or authorizing a repair. Secret references are not rendered into incident templates or exposed as preview values. Arbitrary expressions, generated shell code, CLR types, SQL, graph-defined webhook URLs, cycles and hidden health checks are not execution inputs.

## Versions and existing alert flows

**Upgrade draft to named outcomes** changes an old draft locally until Save creates its next revision. Undo can restore the prior draft schema. Existing schema-one drafts, published graph bytes/configuration hashes, connector revisions, receiver receipts and running continuations retain their original behavior. An old schema-one writer cannot downgrade and erase a newer draft. Published viewers and historical replay use immutable versions.

New schema-two configuration hashes sort JSON object members and normalize equivalent numeric spellings without converting through floating point. This preserves exact numeric strings and makes hashes stable when PostgreSQL stores JSONB. Schema-one serialization and published hashes keep their historical bytes.

Schema-one alert graphs retain their bounded single path: alert, optional condition, mapping, optional sequential investigation and a final incident/action group, or an AI-only final step. Their eight-node/seven-edge/32 KiB limits remain unchanged. Schema two permits repeated action kinds and bounded branches, with 32 nodes, 64 edges and 128 KiB canonical graph JSON. These authoring bounds do not raise execution, action-payload or trace budgets.

Alert mappings retain `{ruleName}`, `{clientName}`, `{resource}`, `{metric}`, `{severity}`, `{value}`, `{serviceState}`, `{occurredAt}`, `{offlineSummary}`, `{offlineCount}` and `{offlineClients}`. Offline alerts add their bounded outage summary even for older templates. Execution mappings use native values such as `{sourceKind}`, `{definitionName}`, `{definitionRevision}`, `{executionId}`, `{attemptId}`, `{targetName}`, `{outcome}`, `{rawStatus}`, `{reason}`, `{exitCode}` and `{executionLink}`. They never manufacture monitoring rule, metric or alert data.

Publication pins execution definitions, library revisions/content hashes, input schemas, job-bound or fixed targets, connector configuration revisions and optional investigation guidance. A script configured to use the source event target resolves and pins its actual target at canonical admission. Native definition names are retained exactly up to the 512-character flow metadata bound; an overlong name is explicitly unavailable for publication rather than silently truncated into a different pin. Later definition or credential edits cannot change an admitted run's semantic payload. After changing a destination, choose its updated reference and republish the flows that should use it.

## Preview and historical playback

Preview setup opens in a dialog and does not push the canvas down. Select a synthetic monitoring or execution sample. To follow an external action's outcome, explicitly provide its sample outcome/output fixture. Without a fixture, the pure evaluator stops at that effect boundary and labels unavailable data. Conditions and mappings calculate real pure values; preview does not invent action success or AI findings.

A floating player offers play/pause, next/previous, restart, speed and exit. Selecting a node or captured step shows Input, Output, Outcome, chosen port, attempt and available timing/error data. Simulated, recorded, missing, captured empty/null and truncated values are labelled. Copy exports only the visible authorized bounded value or excerpt. Unvisited nodes have no captured output. Recorded node duration uses elapsed trusted time from its persisted start, including a durable wait; simulated duration is unavailable rather than invented.

Native action output comes from the committed execution's existing redacted detail projection. Missing, empty, withheld and truncated output remain explicit. The full authorized native execution reference stays available when a captured excerpt is bounded; identifiers are exact strings throughout.

Historical replay reads the run's immutable graph and recorded snapshots, not the current draft. Older runs without snapshots say data unavailable. A changed draft or tenant/flow/version context removes stale traces and fences late responses. Recorded values are not replaced with edited preview samples.

Playback and backward stepping are presentation only. They perform zero script/job dispatches, AI turns, notifications or incident writes. Motion stays in the browser, respects reduced motion, pauses when hidden/offscreen and disposes timers/listeners when closed. It does not save the graph, dirty the draft or send per-frame SignalR updates.

## Authority and durable execution

The API checks `flow.read`, `flow.edit`, `flow.publish` and `flow.execute` for the selected tenant. Existing roles are not widened. A foreign flow/version/run ID grants no access. Source subscriptions and native execution selectors also check their current source permissions. Configuring an action grants no service/global-admin authority.

Monitoring uses its pinned configuring principal and optional integration credential, retaining occurrence, suppression, bypass and fresh-evidence admission. Job/script/request sources use their own immutable transition and subscription evidence. RequestEventBus is a UI channel, not automation ingress. Native source state, transition fact and selected subscription intent commit durably together, including direct relational callback projections; a job event waits for its complete terminal result. Subscription creation, publication, enable changes and source selection share one tenant transaction order. The occurrence boundary uses trusted live database time after those locks; native Started/Completed timestamps remain separate. A delayed projection may therefore commit after enable even when its native completion was earlier. A later enable cannot resurrect an earlier committed transition or retarget an already selected version.

Before an unstarted effect, current principal/account status, tenant permission, credential scope/revocation/expiry, target validity, flow enablement and pinned revision are checked again. Native jobs keep their bound target. Already accepted work is observed through its canonical service; queue admission is pending, not success.

Runs persist node inputs/output references, decisions, selected outcomes, accepted execution IDs, attempt keys and continuations. Long jobs/scripts/investigations yield the worker and resume on the exact committed result. Routine polling does not spend another execution attempt. A graph's completed status means its selected path finished; recovery is verified only by the linked rerun's committed native success. Lease token, worker identity, increasing fence, expiry and checkpoint revision protect writes; a stale worker cannot overwrite a reclaimed run.

Canonical admission commits a stable action key and protected exact source/inputs/target before exposing work. A crash after acceptance but before the flow saves its receipt recovers the same native execution ID. Transport retries reuse their key; a deliberate rerun creates one new linked attempt.

| Durable evidence | Recovery behavior |
| --- | --- |
| No send started | A fresh authorized lease may proceed with the same pinned payload/key. |
| A successful receipt is committed | Finalize without sending again. |
| Send started, no receipt, no verified replay contract | Record Unknown/DeliveryUnknown; do not retry blindly. |
| Verified receiver replay contract | A permitted retry uses the same key and frozen semantic payload. |
| Proven safe transient rejection | Retry within the existing attempts/backoff/age budget. |
| Suppressed, revoked, disabled, unavailable or invalid dependency | Preserve the explicit failed/skipped/denied disposition. |

Incoming webhooks do not provide RatelDesk's verified replay contract. A rate-limit rejection may be safely retried; an ambiguous timeout after a send remains DeliveryUnknown. Exhaustion never overwrites verified success or ambiguous delivery evidence with a generic failure. Re-enabling a definition does not resurrect an old disabled event, and metric recovery does not automatically resolve a helpdesk ticket.

## Investigation, repair and verified recovery

Investigation receives the original failure, useful source/target/definition names, authorized bounded diagnostics and earlier outputs. Findings stay separate from original evidence. Execution-source investigations follow explicit named outcome ports; the legacy Stop/Continue-with-alert setting applies only to schema one. The incident may use `{aiSummary}`, `{aiEvidence}`, `{aiRecommendations}`, `{aiStatus}`, `{aiObservedAt}` and `{aiInvestigationId}`; mapping before investigation still receives final enrichment.

Automatic investigation requires an enforceable restricted tool capability. The current Netclaw protocol cannot express a per-turn restricted profile. When that restriction cannot be enforced, execution-source investigation returns explicit Unsupported/unavailable before sending a remote turn. A session name or prompt is not tool authorization. Existing approval requirements remain in force; an already authorized library action does not gain a new mandatory reviewer. Generated arbitrary code is not silently executable. The exact current failure code is `netclaw-investigation-tool-policy-unavailable`. See the [protocol evidence and integration boundary](../evidence/flows-investigation-policy-20261105.md) and [Netclaw integration](../integrations/netclaw-http-mcp.md).

Configure permitted pinned library/job repair actions and explicit targets in the graph. **Rerun original execution** uses a trusted replay descriptor for the original definition, inputs and target. Supported script/job-backed requests use their canonical execution services. A replayable child library-script step reruns that exact pinned step and target, never its whole parent job. Missing historical replay material, raw/unpinned steps and non-replayable requests return Unsupported. Unknown outcomes do not cause blind reruns.

The default recovery budget is one linked rerun, with a finite configured maximum of three and explicit causation to prevent recursive failure-trigger loops. Repair-command success, accepted rerun IDs and AI text do not prove recovery. Only the linked rerun's authoritative terminal success reaches the recovered branch. An additional health check is an explicit graph action and condition.

An unresolved incident includes the original failure, source/target names, definition/run/attempt links, bounded diagnostic evidence, separately labelled findings, actual repair attempts and the verified rerun outcome or explicit uncertainty. Final enrichment is assembled before the first send; its semantic payload then stays frozen under the existing receiver key and receipt contract.

## Operational bounds and retest

Graph limits are independent of the 20-second worker execution slice, 60-second lease, five safe action attempts and 24-hour retry age. Operation deadlines are explicit and finite; yielding does not hold a worker for the operation's lifetime. Execution events are bounded to 16 KiB with exact string IDs and bounded diagnostic references. Detailed snapshots and excerpts have separate capture budgets; truncation is visible.

There are at most 128 definitions/tenant, 128 versions/definition, 256 active runs/tenant and 4,096 retained runs/tenant. History reads return at most 100 rows. Detailed terminal history is pruned after 90 days; ambiguous deliveries retain their keys and evidence. An original event older than the 24-hour ingress window cannot start another effect after pruning. Receiver deduplication retention must cover the supported retry window before safe replay can be enabled.

Use synthetic executions in a disposable tenant for acceptance:

1. Load an old alert flow and its immutable version; verify its original mappings and receipt.
2. Create a disabled template, configure named sources/definitions/targets/destinations, validate, then publish/enable deliberately.
3. Enter editor and viewer fullscreen; open preview/settings, connect/cancel ports, adjust a route at several zoom/pan positions and save/reload.
4. Preview a failed execution with explicit action fixtures. Step backward/restart and inspect missing/null/empty/truncated values; verify no real action occurred.
5. Run the configured failure path in the disposable environment. Verify permitted repair and one linked rerun by native IDs and actual terminal result; unresolved/Unknown/Unsupported/Denied/exhausted paths stay explicit.
6. Restart at acceptance and waiting boundaries, redeliver the same source event and confirm one native execution and one enriched incident. Inspect historical replay against the recorded version.
7. Check selected and all-tenant subscriptions, request-only rejection, correlated job/request aggregation, revoked authority and a representative graph near the v2 bounds.

## VeloxDev compatibility and notices

NetRatel pins the actual published `VeloxDev.Core` and `VeloxDev.Razor` packages to **10.0.1**. The published source metadata points to upstream commit `f2ac8a79de0448bcb29398a5ec77b13a5985b922`. Both packages supply the net8.0 assets selected by the .NET 10 InteractiveServer host with MudBlazor 9.11.0. Compatibility is checked by the assembled stage's normal build and focused runtime/editor tests; browser acceptance covers native gestures and geometry.

The adapter uses real native create/connect/move commands and waits for the matching completion event before replaying a following edge. Library command task completion alone does not guarantee model mutation has finished. Replacement canvases get unique instance IDs and keyed component lifetimes; callbacks and JS imports are fenced by cancellation and the selected tenant/flow generation. Captured geometry is canonical finite data. The backend adapter reconstructs only the reviewed node types, including the bounded action group, and uses the actual Core compiler/runtime; it never calls the library's arbitrary-type deserializer.

The native Razor CSS/JS assets are referenced under `_content/VeloxDev.Razor/` using the application's base path. The full upstream MIT copyright/permission notice is in [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) and is copied into build/publish output.

Focused verification uses `NetRatel.Tests.Flows.*` for canonical validation, actual Core context isolation, API authorization/inertness/body limits and real PostgreSQL migrations/restarts/receipt/fencing/retention. PostgreSQL cases use the repository's standard Testcontainers fixture. Web component and Chromium tests exercise the actual editor, native connections and drag/layout/viewport save/reload, theme/zoom/mobile reflow and inert preview counters. Tests use deterministic receiver fixtures; they do not create tickets on a live helpdesk.
