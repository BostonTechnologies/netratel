# Netclaw AI operations

NetRatel uses the same authenticated native SignalR chat protocol as RatelDesk. Each tenant has its own protected device credential, general chats and durable investigations. **AI Ops → Chat** opens `/ai/chat`; **AI Ops → Investigations** opens `/ai/ops`. Connection administration is the **Netclaw** tab at `/account/integration-credentials?tab=netclaw&tenantId=<id>`. Older `/ai/integration` bookmarks redirect while preserving the tenant.

## Connect a daemon

1. Select the intended tenant in **AI Ops → Connect Netclaw**. The account needs `telemetry.read` to find the tenant and `tenant.admin` to configure its connection.
2. On the Netclaw host, run `netclaw daemon pair`. Enter the address and one-time pairing code it prints, then choose **Pair & connect**. See the [Netclaw pairing guide](https://netclaw.dev/guides/pairing-remote-devices/).
3. NetRatel exchanges the code, protects the returned device token using the deployment's ASP.NET Core Data Protection key ring, and saves it before verifying an authenticated SignalR session. Neither the code nor device token is returned to the browser or MCP response.
4. If verification fails, the exchanged credential remains saved. Use **Test connection** after fixing connectivity. If the request itself was interrupted, use **Reload saved connection** before obtaining another code. Changing a profile requires its current revision; a stale request is rejected before sending a pairing code.
5. Choose **Open Chat** to ask a general question, or open **Investigations** to select systems and start a scoped investigation. Both use the saved tenant connection.

Use the daemon root address or its `/hub/session` URL. HTTPS is supported. Explicit private IP literals printed by Netclaw can use HTTP under the same private-network policy as RatelDesk; arbitrary public HTTP, URL credentials, query strings, alternate hub paths, metadata destinations, redirects, and unsafe resolved addresses are rejected. Preserve the deployment's Data Protection keys when replacing an API container so stored device credentials remain usable.

The green **Connected** badge requires a fresh observation from the existing authenticated SignalR session owner. Historical verification is shown separately with its recorded time. Hover or keyboard focus shows the authorized endpoint and available session activity; an idle tenant or another API replica can have no current observation. Viewing diagnostics does not connect a new session. A new profile revision invalidates verification and retires affected active transports. Disabling the connection stops new turns; an already running remote turn may continue and its outcome remains available for recovery.

### Grant tool access separately

The device token authorizes chat with Netclaw. A NetRatel MCP credential authorizes Netclaw to inspect or control systems. These are separate connections and credentials.

First [configure and verify the HTTP MCP server's connection to NetRatel](../mcp-http/setup.md). Then follow [Give Netclaw customer connections HTTP MCP access](netclaw-http-mcp.md) for the exact HTTP transport, protected bearer storage, per-customer grants and allowed/denied read checks. Each customer can use the same MCP endpoint with a separately scoped HTTP MCP credential. The daemon pairing above does not provision that server or install those client connections. HTTP MCP, stdio MCP and API credentials retain distinct authentication and delegation rules.

For tenant-isolated AI Ops, pair each tenant to its own isolated Netclaw daemon and endpoint, containing only that tenant's NetRatel MCP credential. Current native SignalR turns all use Netclaw's Personal audience; separate device tokens, registration names or prompts do not partition tools within one shared daemon. Follow [customer SignalR isolation](netclaw-http-mcp.md#isolate-customer-signalr-sessions) before registering or pairing multiple tenants.

Choose a credential whose permitted tenant and clients match the investigation's intended scope. Selected systems supply context; they do not widen a credential's authority or grant the daemon additional permissions. Every actual tool operation still passes through NetRatel's current tenant authorization and MCP operation policy. Configure write tools to request an operator decision in Netclaw if that is the intended runbook. The chat prompt asks for approval, but the daemon's configured tool policy is the execution authority.

## General chat

Open **AI Ops → Chat**, select the intended tenant, and type a message. The tenant must have an enabled Netclaw connection with a saved device credential. The connection badge opens its tenant-specific settings; an unconfigured tenant shows a setup action. A saved successful connection check and a currently connected session remain distinct states.

Chat starts directly from the first **Send**. It needs no system selection, title dialog, or prompt selection. The first message supplies the conversation title. The welcome cards help draft requests to create a script, review notifications, plan a flow or ask an operational question; selecting a card fills the composer and leaves sending to the operator. Use **Enter** to send and **Shift+Enter** for a new line. **Ctrl+Enter** and **⌘+Enter** also send. IME composition does not submit a message.

The history sidebar contains this tenant's general chats. Search it by title, open a conversation to resume its saved transcript, or choose **New chat** for another topic. **Show archived** includes retained completed conversations. Conversation links preserve both the tenant and chat ID, for example `/ai/chat?tenantId=17&chat=<conversation-uuid>`. Tenant changes clear the previous transcript and pending local input before loading the new tenant.

Chat sends the operator's question with the tenant identity and fixed platform instructions for scope, tool authorization, approvals and recovery. It does not attach selected clients, system observations, alert occurrences, or the investigation prompt library's default and optional guidance. Netclaw can retrieve information through its configured permitted tools when the operator asks. Pairing, tool grants and the daemon's own configured behavior continue to apply.

Assistant replies retain readable text and fenced code blocks with **Copy** actions. Tool activity can be expanded separately. The transcript and history list scroll independently within the available viewport; the composer and header actions remain visible with long history. The transcript follows new output when already at the bottom and lets the operator scroll back to read earlier messages. Approval requests, interrupted delivery, recovery and archive confirmation use the same durable mechanisms described below for investigations. Refreshing or opening history never submits an AI turn.

General chats and investigations have separate history lists. They share the transcript store, transport and tenant conversation limits. Run the one-shot migrations service when upgrading to the structured trace/recovery model. Read access requires `telemetry.read`; creating or archiving requires `client.manage` as well, and sending, approvals and recovery also require `secret.use`.

### Conversation display

Both routes use the same interactive conversation surface. **Maximise conversation**, beside Archive, fills the application viewport with that selected conversation, its context, trace, approvals and composer. **Restore conversation** or Escape returns to the workspace. A nested settings panel or dialog handles Escape first. Changing the layout retains the draft, loaded transcript, expanded details, scroll and existing transport. Transcript readers can maximise archived and unresolved conversations without write authority.

Assistant Markdown pipe tables render as tables in both Chat and investigations, including streamed replies and saved history. Column alignment, bold text and inline code remain visible; wide tables scroll horizontally within the response. **Copy table** copies that table as Markdown, including its header and separator, for reuse in documents or another conversation. **Copy response** copies the complete original reply. Clipboard feedback appears beside the copy action; if browser access is unavailable, select the text and copy it manually.

Tool rows show the name, recorded status and a compact UTC time without expansion. **Result received** does not assert that a business operation succeeded. Expand a row for its call ID, session/message identity, source and received times, and supported arguments/results/failure metadata. Matching call IDs are scoped to the same session and active message; repeated or parallel calls keep separate events. Any elapsed time is labelled as derived from matched source timestamps. Older events explicitly say their details were not recorded.

The **Chat display settings** cog remembers timestamp, call-ID, expansion and optional usage/processing/compaction/subagent choices in this browser across both routes. Approval, recovery and error notices remain available. Trace fields are optional across daemon versions; NetRatel stores only selected, bounded, server-redacted details and Copy uses that recorded content. Raw frames, private thinking and exception internals are excluded. Redaction, truncation and unavailable fields are labelled. Public upstream fields do not establish which fields a custom daemon actually emits.

## Investigate manually or from an alert

The workspace lists recent manual and alert-linked investigations, with separate counts for running turns, pending approvals, and unconfirmed outcomes. Choose a title and 1–20 client IDs for a new investigation. The picker searches hostname, friendly name, IP addresses and tags using the authorized client directory. It excludes deleted and superseded registrations. Unknown hostnames are labelled explicitly; immutable IDs remain available as references. The selected scope stays attached to the investigation so later messages refer to the same systems.

Each request supplies tenant ID/name, selected client identities, OS/architecture, IPs, tags, status, version and last-known observation times where available. Offline observations remain explicitly last-known. Alert investigations include the linked monitoring occurrence and flow-run reference. Later turns refresh authorized metadata and record the snapshot actually sent. The shared preflight checks serialized `[sessionId, request]` against 28,000 UTF-8 bytes. Oversized combinations report a useful error and retain the operator's input; required guidance, scope IDs and questions are not silently truncated. The assistant can retrieve further detail through its permitted MCP tools.

### Tenant guidance

Open **AI Ops → Prompts** to create, view, edit, copy or archive tenant guidance using Monaco. Read/use follows `telemetry.read`; management follows `client.manage`. Every save creates an immutable revision and requires the current edit token. Setting or removing the single tenant default also requires its current token. Archiving a default requires explicitly removing or replacing it; archive preserves historical revisions.

New investigation sessions automatically snapshot the tenant default when configured, followed by ordered optional selections from **Add prompts to LLM context**. Zero optional selections is valid. Existing sessions retain their saved guidance after library edits. Published flow optional guidance retains its deliberately published revisions; the tenant default is resolved when a new investigation session is created. Fixed platform instructions, tenant guidance, clearly delimited untrusted observations and the question use one shared composer. Prompt names for MCP connections are guidance text and do not create credentials or expand grants. Creating or previewing a manual investigation does not send an LLM turn; **Send** remains explicit.

The conversation keeps operator messages, completed assistant text, tool activity, approval requests and decisions, and recovery events. Streaming draft text is temporary; committed transcript entries survive browser navigation and API restarts. Refresh and polling retain the existing conversation during transient failures. Text is rendered as text, including untrusted client or assistant content; it is not executed as HTML.

### Interpret the states

| State | Meaning and next action |
| --- | --- |
| `Idle` / Ready | A new operator message can be admitted. |
| `Queued` | The server committed the request. The transport has not begun sending it. |
| `Processing` / Investigating | Sending or assistant execution is in progress. Dispatch acceptance does not prove completion. |
| `AwaitingApproval` | Netclaw offered an interaction for the active turn. Review the proposal before choosing one of its exact options. |
| `DeliveryUnknown` / Outcome unconfirmed | The turn, output stream, or approval response was interrupted. Recover the saved session before deciding what to do next. |
| `Archived` | The local transcript is retained and cannot accept more messages. Archiving does not cancel actions already running remotely. |

Messages have stable caller-generated UUIDs. Repeating the same message ID and identical body returns the original admission; reusing it for different content is rejected. The browser keeps the original request while an HTTP result is uncertain and offers **Retry same request**. It does not silently turn a retry into a new remote message.

### Long model calls

A healthy connected receiver remains attached while the model is quiet, including calls longer than five minutes and pending approvals. Application-event silence is not transport failure. Connection/profile changes, disconnected transport, stopped receivers and buffer overflow still produce an explicit unresolved outcome and bounded cleanup. The worker retains its connection, transcript and buffer capacity limits.

Late output is accepted only for the saved tenant, daemon fingerprint, session and active message, using supported live-send admission or exact history evidence. Native numeric turn identities must match the saved pre-send baseline; the `turnNumber: null` written by NetClaw 0.28.2 on ordinary text, delta and tool events means no identity was supplied. Malformed identities and mismatched numbers are rejected. Full text replaces its preceding streaming draft, and delta-only replies are committed on completion or buffer flush. A completed assistant turn can report a denied monitoring operation; read the answer and tool evidence to determine whether the operational check succeeded. Nothing is automatically resent or approved.

A completion without nonblank assistant output stays **Outcome unconfirmed**, with an explicit missing-answer notice. The healthy receiver stays attached: delayed full text, or a delta draft followed by a buffer flush or terminal event, can restore the answer without replaying the request. **Recover session** checks available history without sending a new message. Explicit `quietEnd: true` completions are labelled as intentionally quiet rather than presented as answered. An automated Flow already given an unknown disposition is never released again by delayed text. Bounded `NetclawOutput` API logs record event type, trusted session/message correlation, numeric turn identity, text length and admission/persistence reason; message contents and secrets are excluded.

The previously saved unresolved conversation remains preserved. Use **Recover session** only with valid evidence. Missing historical correlation can still prevent reconciliation. The observed 642–644 second model phases and rejected calls with a missing `_rationale` remain a native/provider tool-loop dependency. Review the saved tool outcome and correlated daemon logs before taking further action. [NetRatel #754](https://github.com/BostonTechnologies/netratel/issues/754), [NetClaw #2791](https://github.com/netclaw-dev/netclaw/issues/2791)

### Scheduled reports

NetRatel saves bounded current-session reminder references under the original message as soon as registration is observed. The parent terminal records which work remains pending. A saved report and its matching terminal must commit together before NetRatel removes that one-shot reference. Other reminders and delegated work stay pending. An open approval keeps its existing decision controls. Assistant text and a successful terminal describe the report, while operational success requires its own evidence.

**NetClaw 0.28.2 leaves scheduled delivery unconfirmed in NetRatel.** The daemon tells the scheduler it accepted a fire, potentially while the parent is busy. The dispatch acknowledgement can mark the reminder delivered before the report runs. A connected SignalR session can receive a reminder's text and terminal, while routing depends on the current recipient. The pinned source and isolated wire check show that registration returns a reusable definition slug and a next-fire time at second precision. The terminal carries `sourceReminderId` as `<slug>:<scheduled-time-in-ms>`. The text frame has no turn or occurrence identifier. A child delivery can also inherit a reminder link. The existing `EnsureSession` advertises text admission and offers no exact scheduled-result cursor or authoritative pending-reminder snapshot. [0.28.2 release source](https://github.com/netclaw-dev/netclaw/tree/7419d84f7317dae42783d8a9ed6e353f78f961a8), [registration](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/Reminders/SetReminderTool.cs), [dispatch](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/Reminders/ReminderExecutionActor.cs), [wire DTO](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Actors/Protocol/SessionOutputDto.cs), [SignalR hub](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Daemon/Gateway/SessionHub.cs)

With this daemon, Chat and AI Ops show **Scheduled report delivery unconfirmed** after the parent ends, with the saved reminder references and an archive action. A separately observable child or approval may still be active. Ask the daemon administrator to verify the matching reports and any operations in the original session. A disconnect, restart or observation cutoff can also leave a saved report unconfirmed. The current recovery action cannot retrieve an exact scheduled report, so the app preserves the references and the unconfirmed state on reload. [NetRatel #757](https://github.com/BostonTechnologies/netratel/issues/757), [NetRatel #475](https://github.com/BostonTechnologies/netratel/issues/475)

#### Native contract required for delivery

A future daemon must preserve the authenticated SignalR recipient and bind each occurrence to the immutable registration, original operator admission and native session. This applies both to buffered fires and to fires dispatched after the parent turn ends. The app implements the following proposed live format. NetClaw 0.28.2 does not advertise it.

- `EnsureSession` advertises `scheduledReportVersion: 1` alongside `textAdmissionVersion: 1` when the daemon can honor the live contract for that attached session.
- A successful `set_reminder` tool result carries the native registration turn number and a `scheduledReminder` object with `id`, `recurring`, `registrationId` and `messageId`. The daemon assigns a new registration UUID on replacement, preserves it across each occurrence of that generation, and binds the application message UUID to the actual admitted operator turn. The daemon supplies these identities from its own saved state.
- The routed `turn_completed` carries the exact ordered turn number, outcome and `sourceReminderId` occurrence key, plus a `scheduledReport` object containing the original `registrationId`, `messageId` and complete `finalText` for a successful report. Other reminder-originated frames carry the same source key so they cannot be interpreted as another turn's output. The exact terminal payload binds the final text to the occurrence even if progress was interrupted. A failed or quiet terminal leaves that one-shot report unconfirmed.
- The daemon also needs an authorized pending-work and exact-result reconciliation interface for lost recipients or interrupted observation. It must distinguish queued, dispatched, running, terminal, cancelled and replaced work, and replay the same identified result and terminal without executing the occurrence again. Client-side tests of the proposed live format leave this native requirement open. [NetRatel #475](https://github.com/BostonTechnologies/netratel/issues/475), [NetClaw #2791](https://github.com/netclaw-dev/netclaw/issues/2791)

#### Opt-in native browser acceptance

Start a disposable NetRatel installation with PostgreSQL, complete local setup, and pair it to an isolated daemon using a fresh device credential. Use the official supported NetClaw 0.28.2 image with digest `sha256:59bb46d084e440793bf0f382de8be772ae063c18a7941cb89f74c970a634093d` when reproducing the dependency. Its binary reports source revision `7419d84f7317dae42783d8a9ed6e353f78f961a8`.

In a disposable operator terminal, run `python3 tools/testing/netclaw-scheduled-report-provider.py --bind 127.0.0.1 --port 15205`. Select `scheduled-report-fixture` as the isolated daemon's main model using an `openai-compatible` provider pointed at the fixture's `/v1` endpoint. For containers, bind the fixture to the private test-network interface the daemon can reach. Use a fixture posture that permits the native scheduler and keeps shell execution off. The provider only supplies scripted model responses. The official image runs turn admission, the scheduler, buffering, report generation and the SignalR gateway.

From the NetRatel repository, install Playwright for Node in a temporary directory and run the browser check against the disposable Web URL. Point the password variable at the local fixture administrator's password file.

```bash
scratch_dir=$(mktemp -d)
npm install --prefix "$scratch_dir" --no-audit --no-fund playwright@1.63.0
"$scratch_dir/node_modules/.bin/playwright" install chromium
NODE_PATH="$scratch_dir/node_modules" \
NETRATEL_RUN_NATIVE_SCHEDULED_REPORTS=1 \
NETRATEL_SCHEDULED_REPORT_WEB_URL=http://127.0.0.1:8080 \
NETRATEL_SCHEDULED_REPORT_EMAIL=fixture@example.test \
NETRATEL_SCHEDULED_REPORT_PASSWORD_FILE=/path/to/fixture-password \
NETRATEL_SCHEDULED_REPORT_EVIDENCE="$scratch_dir/evidence" \
node tools/testing/netclaw-scheduled-reports.cjs
```

The check sends once from the UI in each of two conversations, captures parent and reload screenshots, and saves the ordered API evidence. Use the recorded session and reminder IDs to correlate native dispatch, native final generation and terminal timestamps. The buffered case holds the parent for 35 seconds after registering the 30-second reminder. A passing run requires both exact finals, exactly one matching terminal each, zero pending reminders, Idle and an enabled composer after reload. **0.28.2 remains a failing delivery gate**, including when both native reports ran and the unconfirmed notice was shown.

### Recovery and approvals

**Recover session** checks the saved remote session without resending the operator message or an approval. When the daemon supplies a `session_joined` history snapshot, admission is confirmed only by exactly one daemon-supplied user message beginning with the exact investigation/message marker. A quoted marker in assistant text, a marker from another turn, or duplicate user markers cannot prove admission.

NetClaw 0.28.2 advertises `textAdmissionVersion=1` in `EnsureSession`, but an already initialized session does not emit another joined snapshot on attachment. NetRatel uses that supported acknowledgement contract to permit a new explicit turn on an otherwise ready conversation. Reattachment alone never completes an unresolved message. Recovery returns a persisted **protocol-limited** result immediately when there is no snapshot, with the last attempt and safe next action, instead of waiting for an event this daemon does not send. A daemon with neither a snapshot nor the supported text-admission version returns an explicit compatibility limitation. Existing useful streams are retained; snapshot-only completed or uncorrelated recovery owners are retired.

Text admission is not a snapshot or terminal-correlation capability. A new session has an isolated first turn. A continuation uses a fresh native snapshot or the previously committed live terminal count for that same session and exact preceding message. Its terminal number must advance that baseline exactly once. Without a verified baseline, even a live `completed` event leaves the local message **Outcome unconfirmed**: an older console turn may still be finishing. The session limitation is visible before that terminal event and does not invent a recovery-attempt timestamp. This case also requires the daemon contract follow-up before NetRatel can claim exact completion; a successful send alone does not remove that limitation.

Opening the same session in another console can divert assistant output while the overall connection remains healthy. The shared Chat and AI Ops conversation surface explains that distinction. The native 0.28.2 contract does not notify a displaced connection. NetRatel cannot claim it detected ownership loss or reconstruct an earlier exact completed turn after a later console turn without additional daemon evidence. [#475](https://github.com/BostonTechnologies/netratel/issues/475) tracks the deferred daemon contract; it is not implemented by this release.

Admission evidence does not prove completion. Recovery restores and deduplicates the recent assistant excerpts after the exact latest message marker; these excerpts have no original timestamp and may be truncated by the daemon. New sends retain the pre-send native turn count when a real joined snapshot supplies one. Exactly one count advance, together with admission of the exact latest message, can establish that the turn ended and allow another operator question. A historical terminal count does not supply success/failure or independently verify side effects, and never releases an automated Flow as successful. A live terminal event during recovery must also carry the exact expected native `turnNumber`; a later turn or missing baseline/turn number cannot complete the saved message. Running, approval, completed, interrupted and ended-with-unknown-outcome recovery notices remain distinct. Model-turn completion never establishes successful operational work.

Older saved turns and attachments without a snapshot have no recorded pre-send count. The currently supported joined-history contract contains only a session turn count and bounded role/content excerpts, without a durable per-message terminal outcome. **Those historical turns can remain unconfirmed even when a final-looking assistant reply is restored.** The recovery notice explains the limitation, records the last attempt, and offers review/archive/new-conversation guidance. Repeated clicks within 30 seconds reuse the current result; unchanged recovery results and excerpts are not repeatedly appended. Cancellation, timeout and connection failures persist a concrete recovery result and retire unused owners. Recovery never resends a message or approval. Review daemon/tool history before replacement work. Recovery results and the last attempt remain beside the conversation header, outside transcript scrolling. Each completed check provides visible feedback, including when the 30-second cooldown reuses a saved result. Archive opens a viewport dialog rather than inserting a confirmation above the transcript. Archiving an active or unresolved investigation requires acknowledging possible remote execution and supplying the current message ID; legacy unresolved records without an active message ID use the empty UUID. A changed active turn still invalidates the confirmation.

Approval decisions are persisted before transport, bound to the exact active message and call ID, and accepted only once. Only offered `approve_once`, `approve_session`, and `deny` choices are exposed; global or permanent approval choices are omitted. An interrupted decision is never automatically submitted again. After a restart or disconnect, an unanswered saved approval is reopened only when the resumed daemon session confirms that same current request. MCP assistants must not approve their own proposed changes automatically.

A provider failure such as `HTTP 400`, `input[16].name`, and “string does not match pattern” means the model provider rejected a name in the daemon's saved input history. This is separate from connection health and missing reply delivery. NetRatel displays **History rejected** and an actionable notice for the current session and turn, even when the error is beyond the first transcript page. Reconnecting does not rewrite native history and is not offered as its repair. Archive the conversation and start a new chat, or ask the daemon administrator to repair that history before explicitly continuing. NetRatel preserves the transcript and error reference; it neither edits native history nor resends prior operations. The exact offending name must be determined from daemon diagnostics.

## Trigger an investigation from Flows

1. Pair and successfully test the tenant's Netclaw connection.
2. Create or edit a flow draft with **Alert raised → Map alert fields → AI: Investigate alert → RatelDesk: Create incident**. Insert AI before an existing incident action and select its Netclaw connection, optional guidance, deadline and failure disposition. An AI-only terminal path remains supported.
3. An **Action group** still supports independent parallel destinations. Use an AI node before the incident action when the incident must depend on investigation completion.
4. Preview the draft, review its mapping and targets, publish an immutable version, and bind that published version to the intended monitoring rule.

The preview is inert. Publication pins the connection and optional prompt revisions. A monitoring action creates one local investigation using its stable action key and queues one exact automated message; retrying admission cannot create another remote turn. Queue admission is a waiting checkpoint. The downstream path waits for an explicit committed terminal result correlated to tenant, run/version, action, investigation and message. Approval waits, failure, timeout, cancellation and unknown delivery remain distinct. The default failure disposition continues with original alert facts and a labelled unsuccessful AI outcome; operators can instead stop the flow. The configured principal and credential need `flow.execute`, `telemetry.read`, `monitoring.read`, and `secret.use`. Existing flow publication, rule management, target selection, suppression, and current-evidence gates still apply.

The incident retains original alert facts and adds an **AI investigation** section with outcome, evidence, observation time and the investigation reference. If a mapped description leaves insufficient space, the incident keeps canonical alert facts and a labelled excerpt; the full original event and mapping remain in flow history. AI discovery does not become fresh telemetry. Map-before-AI flows receive the same enrichment. The final semantic incident payload is persisted before its first send; restart, duplicate completion and receiver recovery retain its key and payload. Late completion or a manual follow-up cannot release a second incident.

Before sending an automatic queued message, the worker checks the saved authority, selected clients, exact action/connection revision, current monitoring occurrence/evidence, target membership, silence state, and enabled published flow again. A clear, silence, removed target, disabled flow, changed connection, or revoked credential can therefore prevent a queued message that has not started transport. Completed local admission is a receipt for the investigation's creation; it does not claim that the AI has completed its work. Use **Open AI investigation** from the flow-run result to follow its actual outcome.

## API, MCP, and CLI

The ordinary API routes start at `/api/v1/tenants/{tenantId}/ai`. Tenant discovery is `GET /api/v1/ai/tenants`.

The AI and client-metadata APIs accept OIDC accounts, local sessions, and API-purpose integration credentials subject to their current tenant grants. CLI and isolated stdio clients can send their API-purpose credential directly to the managed operation API. The HTTP MCP relay authenticates with its configured API M2M credential and supplies a separately verified, short-lived delegation bound to the exact caller, operation, resource, environment, tenant, and client target. A relay token alone grants no account authority. HTTP-MCP-purpose credentials remain confined to the existing exchange and delegation path; they are not accepted directly on these account APIs.

| API suffix | Purpose | Canonical permissions |
| --- | --- | --- |
| `GET /connection`, `GET /clients` | Safe connection status and selectable client metadata | `telemetry.read` |
| `POST /connection/pair`, `POST /connection/test`, `PUT /connection/enabled` | Pair, verify, or change profile availability | `tenant.admin` |
| `GET /chats` | Recent general chats, separately from investigations | `telemetry.read` |
| `POST /chats` | Create a general chat without selected systems or investigation prompts | `client.manage`, `telemetry.read`; requires an enabled configured connection |
| `GET /investigations`, `GET /investigations/{id}` | Recent investigations and paged transcript | `telemetry.read`; linked alerts also require `monitoring.read` |
| `POST /investigations` | Create a titled investigation with explicit clients | `client.manage`, `telemetry.read`; linked alerts also require `monitoring.read` |
| `POST /investigations/{id}/messages` | Admit an operator turn | `client.manage`, `telemetry.read`, `secret.use`; linked alerts also require `monitoring.read` |
| `POST /investigations/{id}/approvals/{callId}`, `POST /investigations/{id}/reconcile` | Answer a current interaction or recover existing history | Same as operator turns |
| `POST /investigations/{id}/archive` | Archive while retaining history | `client.manage`, `telemetry.read`; linked alerts also require `monitoring.read` |

General chats reuse the existing conversation item routes for transcript reads, messages, approvals, recovery and archiving. Their summaries set `isGeneralChat` to `true`. Chat creation accepts `requestId` and `title`; the separate investigation creation contract continues to require explicit client IDs. Browser chat does not add new MCP or CLI operation names.

HTTP MCP and stdio MCP expose `netratel_ai`; CLI exposes `netratel ai`. Operations are `connection`, `pair`, `test_connection`, `set_enabled`, `clients`, `list`, `get`, `create`, `send`, `respond`, `reconcile`, and `archive`. CLI operation names use hyphens. Their typed schemas derive from the shared API contracts. MCP and CLI additionally apply current credential purpose, operation policy and target admission at preview and acceptance; mutations require explicit confirmation. An unconfirmed mutation produces a preview. Queue acceptance is the MCP policy authorization point. Before daemon invocation the worker rechecks canonical actor and credential grants; it does not reconstruct or re-evaluate the original MCP request policy context. The daemon's subsequent NetRatel tool calls each undergo their own current permissions and operation policy.

Read discovery and the operation schema first. Example MCP create arguments, using your actual selected client UUIDs and a newly generated request UUID:

```json
{
  "operation": "create",
  "request": {
    "tenantId": 17,
    "data": {
      "requestId": "11111111-1111-4111-8111-111111111111",
      "title": "Investigate the worker outage",
      "agentIds": ["22222222-2222-4222-8222-222222222222"]
    }
  },
  "confirm": true
}
```

For the returned investigation ID, use `send` with `data: {"messageId":"<new UUID>","text":"<question>"}`, then `get` with `investigationId` and `after`. Each snapshot contains the current `lastSequence` plus at most 200 ordered events; when a page is partial, advance from the last event actually received until the saved head is reached. Listing returns at most 100 rows per page and supports `skip`. Reading never starts a conversation or sends an AI message.

CLI examples after authenticating with the intended credential:

```bash
netratel ai connection --tenant-id 17
netratel ai clients --tenant-id 17
netratel ai create --tenant-id 17 --body-file investigation.json
netratel ai create --tenant-id 17 --body-file investigation.json --confirm
netratel ai get --tenant-id 17 --investigation-id <investigation-uuid> --after 0
netratel ai send --tenant-id 17 --investigation-id <investigation-uuid> --body-file message.json --confirm
```

`investigation.json` contains the typed `data` object from the create example; `message.json` contains only `messageId` and `text`. Preserve IDs when checking the same request. A new UUID means a new action, not a retry mechanism. The LLM instructions in the MCP catalog require evidence-based reads, explicit tenant/client selection, current schema discovery, and completion checks before reporting a result.

## Protocol and operational limits

The adaptation follows [RatelDesk's Netclaw integration](https://github.com/BostonTechnologies/rateldesk/blob/main/docs/netclaw.md):

- `POST /api/pair/exchange` with `{code, deviceName}` returns `{token}`. Only an explicit device-name conflict receives a new name and retry. Transport failures and other uncertain responses are not replayed.
- Authenticated SignalR uses `/hub/session`, `EnsureSession(sessionIdOrNull, "signalr")`, `SendMessage(sessionId, text)`, `RespondToInteraction(sessionId, callId, key)`, and `ReceiveOutput`.
- WebSockets and the native client's authenticated fallback transports share endpoint, DNS and connection guards.
- PostgreSQL owns durable message ordering and a single active transport owner using a dedicated advisory-lock connection. The worker limits itself to 25 live sessions and four queued dispatches per pass. A five-minute gap in assistant activity marks a turn unresolved.
- Each tenant can retain up to 256 unarchived investigations; an individual transcript permits 10,000 normal entries before requiring a new investigation; terminal and recovery status entries may follow. Output channels, message size, approval count, client metadata and history pages are bounded.

Run **one API replica when using interactive Netclaw chat**. PostgreSQL elects one transport owner and prevents duplicate dispatch across replicas, but approval responses and explicit recovery are currently handled by the API instance receiving the request. Those actions require that instance to own the transport; they are not forwarded to another replica. An unavailable owner leaves the result unresolved without replaying an approval or message. Saved investigations and queued admission remain durable during owner restarts.

Focused tests cover the real protocol against a disposable authenticated SignalR hub, WebSocket fallback, pairing retry boundaries, tenant/message/approval persistence, protected credentials, restart recovery without resend, immutable Flow execution/preview, and the browser's retained conversation/request identity. The PostgreSQL cases run in the existing provider suite. Connecting a released installation to the owner's actual Netclaw daemon and verifying its configured tools remains deployment acceptance.
