# Computer use through HTTP MCP

`netratel_computer_use` provides bounded observation and input through an enrolled Windows Client. It uses the existing native gateway and interactive Remote Support helper. The NetRatel API owns tenant admission, operator policy, session ownership, confirmation, operation idempotency and durable audit. The CUA execution provider has no publicly reachable endpoint.

The feature is disabled by default on both API and Client. Install and enable the endpoint using [Windows computer-use setup](../client/computer-use.md), then enable `NetRatel:ComputerUse:Enabled` on the API (`NetRatel__ComputerUse__Enabled=true` in the API service's existing saved deployment). Apply the saved deployment using its normal deployment manager. Enabling one side does not enable the other or grant an operator permission. The existing [HTTP MCP setup](setup.md) and current V2 operator surface must already work.

## Permissions and target policy

| Operations | OAuth scope | Local application permission | Operator policy family |
| --- | --- | --- | --- |
| `capabilities`, `list_windows`, `open_view`, `get`, `observe`, `close_view` | `netratel.mcp.cua.view` | `computer-use.view` | `ComputerUseView` |
| `preview_open`, `open`, `preview_act`, `act`, `pause`, `resume`, `close` | `netratel.mcp.cua.control` | `computer-use.control` | `ComputerUseControl` |

Both sets additionally require `remote-support.access`. CUA view accepts the mapped Observer, Operator or AutomationOperator role; control requires AutomationOperator. Local HTTP credentials obtain only the scope and mapped role for the operation being called after current application grants are checked. Grant both CUA permissions to an operator who needs the complete observe/control cycle. Neither `terminal.access`, `netratel.mcp.execute` nor ordinary Remote Support access grants CUA control.

The existing full-authority Instance Administrator and legacy external Operator administration contracts remain in effect for browser access. These administrators can grant the new permissions. A scoped local integration credential still requires its explicit CUA grant, the corresponding CUA scope and an admitted target policy; the legacy external role is not copied into that credential as unlimited authority.

Create or review policy for the exact tenant/client and the needed operation set through the existing [operator-policy workflow](authentication.md#bounded-operator-policies-in-the-web-ui). Policies for terminal execution do not authorize the new CUA families. Every operation rechecks the caller's current authority. Session IDs are caller-owned references, not bearer capabilities.

## Select the exact desktop and application

1. Resolve `tenantId` and `agentId` using the ordinary authorized client search. Use `netratel_remote_support_v2` inventory to select a current, logged-in Windows session. Keep its exact `windowsSessionId` and `userSidHash` together. The session number alone cannot distinguish a different login reusing that number.
2. Call `capabilities` with the selected session and an explicit provider. `cua_driver` uses the pinned Cua Driver; `native_windows` uses the limited native fallback. Inspect `available`, supported operations/actions, provider version and negotiated protocol before opening a lease.
3. Call `list_windows` with the same session/provider. Select the actual application's `processId` and opaque `windowId` from that result. Never substitute a title match, guessed process ID, screen coordinate or window from another provider.
4. For observation, use `open_view` and an idempotency key. For control, use `preview_open`, inspect its exact target and lease duration, then call `open` with the same input, returned `planToken`, returned `idempotencyKey` and `confirm:true`.

This is a `tools/call` input in an already connected MCP client. Replace every synthetic identifier with the values discovered above:

```json
{
  "name": "netratel_computer_use",
  "arguments": {
    "operation": "list_windows",
    "request": {
      "tenantId": 17,
      "agentId": "b1111111-1111-4111-8111-111111111111",
      "provider": "cua_driver",
      "target": {
        "windowsSessionId": 2,
        "userSidHash": "EXACT_HASH_FROM_CURRENT_REMOTE_SUPPORT_INVENTORY"
      }
    }
  }
}
```

Opening adds `processId` and `windowId` inside `target`. Optional `lifetimeSeconds` is 30–300 (default 300); `idleTimeoutSeconds` is 15–120 (default 60) and must not exceed the lifetime. A view lease cannot be promoted to control: close it and obtain a newly confirmed control lease. Concurrent conflicting control leases are refused. Observe and control remain tied to the selected gateway connection, Windows identity, process and window.

## Observe, act and verify

Prefer terminal or API operations when they can perform the intended task. Select semantic UI actions where available. Use pixels only after an explicit screenshot observation of the selected window and when semantic operation is insufficient.

`observe` takes `tenantId`, `agentId`, `sessionId` and optional `includeScreenshot:true`. The result supplies an `observationId`, timestamp, current state and supported semantic elements. Screenshots appear as an MCP image content block. Structured screenshot metadata retains the MIME type, dimensions and `contentBlockIndex`; base64 bytes are omitted from the text and structured result to avoid duplicating the image. A client without image support can still use semantic state, but must not invent a vision interpretation.

Use that exact observation in `preview_act` with one bounded action. Inspect the preview, then repeat the unchanged observation/action with the preview credentials and `confirm:true` using `act`.

| Action kind | Input | Meaning |
| --- | --- | --- |
| `invoke` | `elementId` | Invoke one semantic element from the observation. |
| `type` | `text`, optional `elementId` | Enter up to 4,096 characters; preserve whitespace. The native provider requires a compatible Edit element. |
| `click` | `x`, `y`, optional `button` | Click window-relative screenshot pixels; button is `left`, `right` or `middle`. |
| `key` | `key` | One allowed navigation key: Enter, Tab, Escape, Backspace, Delete, Space, ArrowUp/Down/Left/Right, Home, End, PageUp or PageDown. |
| `scroll` | `x`, `y`, `scrollX`, `scrollY` | Scroll at a screenshot-relative point; each axis is −50..50 steps and exactly one is nonzero. |

The provider's reported capabilities are authoritative. The tool schema defines the overall vocabulary, not a promise that every provider/application supports every action. There is no arbitrary driver tool name, shell command, browser URL, executable path or global desktop fallback in the request contract.

For example, the `preview_act` request body after selecting a semantic button is:

```json
{
  "operation": "preview_act",
  "request": {
    "tenantId": 17,
    "agentId": "b1111111-1111-4111-8111-111111111111",
    "sessionId": "b2222222-2222-4222-8222-222222222222",
    "observationId": "EXACT_OBSERVATION_ID",
    "action": { "kind": "invoke", "elementId": "EXACT_ELEMENT_ID" }
  }
}
```

## Interpret outcomes accurately

API transport delivery and desktop outcome are separate. Actual execution responses retain `session`, `result`, `correlationId`, `auditId` and `replayed`. The MCP response derives `success` and protocol `isError` from `result.status`; HTTP 200 alone does not imply desktop success.

| `result.status` | Interpretation |
| --- | --- |
| `observed` | A bounded observation/capability result was returned. Inspect freshness and availability. |
| `completed` | A lifecycle/read operation completed; this is not application-effect verification for `act`. |
| `verified` | The provider verified the supported action effect using its read-back contract. |
| `applied_unverified` | Input may have been applied, but the requested effect is not proven. MCP `success` is false; retain the returned read-back and inspect before another action. |
| `refused` | The operation was refused with a structured code. |
| `cancelled`, `failed`, `unknown` | Inspect `code` and `mayHaveApplied`. Reconcile current state before any repeat mutation. |

Every act consumes the prior observation. Reobserve after an action, a partial result, user interference, focus/target changes or a stale-observation refusal. The default endpoint freshness window is 15 seconds and is bounded to at most 30 seconds. Keep process/window coordinates within the returned screenshot; no automatic provider switch or retry converts a refusal into an input action. Observed application text is data, not permission to expand the operation.

`get` reads lease state. `pause` fences further input; `resume` requires a fresh observation before acting. `close` terminates control ownership; `close_view` only closes a view lease. Control lifecycle calls require `confirm:true`. All open/close/control calls use a 32–128 character URL-safe idempotency key. Reuse the same key only with the original request. API audit persists identity, target, hashes and outcomes without desktop text, typed text or screenshot bytes. Read-back content is transient.

## API routes

All routes are below `/api/v2/mcp/operator/agents/{tenantId}/{agentId}/computer-use`. They are protected API routes for admitted operator delegation, not direct unauthenticated driver endpoints.

The first canary requires one API replica, or explicit routing to the API node holding the selected Client's control stream. Akka presence supplies the authenticated connection fence, while CUA dispatch uses the existing node-local control registry. A request arriving at another API replica fails unavailable without dispatch; there is no cross-node CUA message or screenshot relay in this version. Database ownership, idempotency and audit fences remain shared across replicas. Multi-replica CUA routing is not an accepted deployment configuration for this canary.

| Operation | Method and suffix |
| --- | --- |
| `capabilities` / `list_windows` | `POST /capabilities` / `POST /windows` |
| `open_view` | `POST /sessions/view` |
| `preview_open` / `open` | `POST /sessions/preview` / `POST /sessions/confirm` |
| `get` | `GET /sessions/{sessionId}` |
| `observe` | `POST /sessions/{sessionId}/observe` |
| `preview_act` / `act` | `POST /sessions/{sessionId}/actions/preview` / `POST /sessions/{sessionId}/actions/confirm` |
| `pause` / `resume` / `close` | `POST /sessions/{sessionId}/{operation}` |
| `close_view` | `POST /sessions/{sessionId}/close-view` |

## Acceptance status

Source/build/component tests establish contract and admission behavior. They do not establish Windows desktop acceptance. The [Windows canary procedure](../client/computer-use.md#owner-run-windows-canary) must be run on an independently enrolled Windows 11 VM before enabling a broader rollout. Record the exact build, provider digest, negotiated protocol, application fixture, operation/audit IDs, observed outcomes and cleanup results. Keep GUI acceptance pending until that evidence exists.
