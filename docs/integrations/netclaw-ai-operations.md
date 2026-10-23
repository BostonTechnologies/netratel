# Netclaw AI operations

NetRatel uses the same authenticated native SignalR chat protocol as RatelDesk. Each tenant has its own protected device credential and durable investigations. The AI Ops workspace is `/ai/ops`; connection administration is the **Netclaw** tab at `/account/integration-credentials?tab=netclaw&tenantId=<id>`. Older `/ai/integration` bookmarks redirect while preserving the tenant.

## Connect a daemon

1. Select the intended tenant in **AI Ops → Connect Netclaw**. The account needs `telemetry.read` to find the tenant and `tenant.admin` to configure its connection.
2. On the Netclaw host, run `netclaw daemon pair`. Enter the address and one-time pairing code it prints, then choose **Pair & connect**. See the [Netclaw pairing guide](https://netclaw.dev/guides/pairing-remote-devices/).
3. NetRatel exchanges the code, protects the returned device token using the deployment's ASP.NET Core Data Protection key ring, and saves it before verifying an authenticated SignalR session. Neither the code nor device token is returned to the browser or MCP response.
4. If verification fails, the exchanged credential remains saved. Use **Test connection** after fixing connectivity. If the request itself was interrupted, use **Reload saved connection** before obtaining another code. Changing a profile requires its current revision; a stale request is rejected before sending a pairing code.
5. Open AI Ops, select one or more systems, create an investigation, and send the first question.

Use the daemon root address or its `/hub/session` URL. HTTPS is supported. Explicit private IP literals printed by Netclaw can use HTTP under the same private-network policy as RatelDesk; arbitrary public HTTP, URL credentials, query strings, alternate hub paths, metadata destinations, redirects, and unsafe resolved addresses are rejected. Preserve the deployment's Data Protection keys when replacing an API container so stored device credentials remain usable.

The green **Connected** badge requires a fresh observation from the existing authenticated SignalR session owner. Historical verification is shown separately with its recorded time. Hover or keyboard focus shows the authorized endpoint and available session activity; an idle tenant or another API replica can have no current observation. Viewing diagnostics does not connect a new session. A new profile revision invalidates verification and retires affected active transports. Disabling the connection stops new turns; an already running remote turn may continue and its outcome remains available for recovery.

### Grant tool access separately

The device token authorizes chat with Netclaw. A NetRatel MCP credential authorizes Netclaw to inspect or control systems. These are separate connections and credentials.

First [configure and verify the HTTP MCP server's connection to NetRatel](../mcp-http/setup.md). Then follow [Give Netclaw customer connections HTTP MCP access](netclaw-http-mcp.md) for the exact HTTP transport, protected bearer storage, per-customer grants and allowed/denied read checks. Each customer can use the same MCP endpoint with a separately scoped HTTP MCP credential. The daemon pairing above does not provision that server or install those client connections. HTTP MCP, stdio MCP and API credentials retain distinct authentication and delegation rules.

For tenant-isolated AI Ops, pair each tenant to its own isolated Netclaw daemon and endpoint, containing only that tenant's NetRatel MCP credential. Current native SignalR turns all use Netclaw's Personal audience; separate device tokens, registration names or prompts do not partition tools within one shared daemon. Follow [customer SignalR isolation](netclaw-http-mcp.md#isolate-customer-signalr-sessions) before registering or pairing multiple tenants.

Choose a credential whose permitted tenant and clients match the investigation's intended scope. Selected systems supply context; they do not widen a credential's authority or grant the daemon additional permissions. Every actual tool operation still passes through NetRatel's current tenant authorization and MCP operation policy. Configure write tools to request an operator decision in Netclaw if that is the intended runbook. The chat prompt asks for approval, but the daemon's configured tool policy is the execution authority.

## Investigate manually or from an alert

The workspace lists recent manual and alert-linked investigations, with separate counts for running turns, pending approvals, and unconfirmed outcomes. Choose a title and 1–20 client IDs for a new investigation. The picker searches hostname, friendly name, IP addresses and tags using the authorized client directory. It excludes deleted and superseded registrations. Unknown hostnames are labelled explicitly; immutable IDs remain available as references. The selected scope stays attached to the investigation so later messages refer to the same systems.

Each request supplies tenant ID/name, selected client identities, OS/architecture, IPs, tags, status, version and last-known observation times where available. Offline observations remain explicitly last-known. Alert investigations include the linked monitoring occurrence and flow-run reference. Later turns refresh authorized metadata and record the snapshot actually sent. The shared preflight checks serialized `[sessionId, request]` against 28,000 UTF-8 bytes. Oversized combinations report a useful error and retain the operator's input; required guidance, scope IDs and questions are not silently truncated. The assistant can retrieve further detail through its permitted MCP tools.

### Tenant guidance

Open **AI Ops → Prompts** to create, view, edit, copy or archive tenant guidance using Monaco. Read/use follows `telemetry.read`; management follows `client.manage`. Every save creates an immutable revision and requires the current edit token. Setting or removing the single tenant default also requires its current token. Archiving a default requires explicitly removing or replacing it; archive preserves historical revisions.

New sessions automatically snapshot the tenant default when configured, followed by ordered optional selections from **Add prompts to LLM context**. Zero optional selections is valid. Existing sessions retain their saved guidance after library edits. Published flow optional guidance retains its deliberately published revisions; the tenant default is resolved when a new investigation session is created. Fixed platform instructions, tenant guidance, clearly delimited untrusted observations and the question use one shared composer. Prompt names for MCP connections are guidance text and do not create credentials or expand grants. Creating or previewing a manual investigation does not send an LLM turn; **Send** remains explicit.

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

### Recovery and approvals

**Recover session** reconnects the saved remote session and reads its history without resending the operator message or an approval. Admission is confirmed only by exactly one daemon-supplied user message beginning with the exact investigation/message marker. A quoted marker in assistant text, a marker from another turn, or duplicate user markers cannot prove admission.

Admission evidence does not prove completion. The investigation remains unconfirmed until the daemon supplies current completion evidence or reoffers a pending approval for that turn. If the daemon has lost its original session or cannot prove the outcome, keep that uncertainty visible. Review daemon/tool history before starting replacement work. Archiving an active or unresolved investigation requires acknowledging possible remote execution and supplying the current message ID.

Approval decisions are persisted before transport, bound to the exact active message and call ID, and accepted only once. Only offered `approve_once`, `approve_session`, and `deny` choices are exposed; global or permanent approval choices are omitted. An interrupted decision is never automatically submitted again. After a restart or disconnect, an unanswered saved approval is reopened only when the resumed daemon session confirms that same current request. MCP assistants must not approve their own proposed changes automatically.

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
| `GET /investigations`, `GET /investigations/{id}` | Recent investigations and paged transcript | `telemetry.read`; linked alerts also require `monitoring.read` |
| `POST /investigations` | Create a titled investigation with explicit clients | `client.manage`, `telemetry.read`; linked alerts also require `monitoring.read` |
| `POST /investigations/{id}/messages` | Admit an operator turn | `client.manage`, `telemetry.read`, `secret.use`; linked alerts also require `monitoring.read` |
| `POST /investigations/{id}/approvals/{callId}`, `POST /investigations/{id}/reconcile` | Answer a current interaction or recover existing history | Same as operator turns |
| `POST /investigations/{id}/archive` | Archive while retaining history | `client.manage`, `telemetry.read`; linked alerts also require `monitoring.read` |

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

Run **one API replica when using interactive Netclaw chat in this beta**. PostgreSQL elects one transport owner and prevents duplicate dispatch across replicas, but approval responses and explicit recovery are currently handled by the API instance receiving the request. Those actions require that instance to own the transport; they are not forwarded to another replica. An unavailable owner leaves the result unresolved without replaying an approval or message. Saved investigations and queued admission remain durable during owner restarts.

Focused tests cover the real protocol against a disposable authenticated SignalR hub, WebSocket fallback, pairing retry boundaries, tenant/message/approval persistence, protected credentials, restart recovery without resend, immutable Flow execution/preview, and the browser's retained conversation/request identity. The PostgreSQL cases run in the existing provider suite. Connecting a released installation to the owner's actual Netclaw daemon and verifying its configured tools remains deployment acceptance.
