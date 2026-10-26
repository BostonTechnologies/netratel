# HTTP MCP authentication modes

Select one ingress mode explicitly. Routing `/mcp` through Web does not choose or bypass authentication. The MCP host authenticates the ingress bearer; the API enforces current account, tenant, credential purpose/resource and operation policy during delegation and execution.

| Mode | Client credential | Discovery |
| --- | --- | --- |
| Local integration credentials | `HttpMcp`-purpose `nrt_ic_` credential bound to canonical HTTPS `/mcp` | NetRatel credential pairing/capabilities; no OAuth protected-resource metadata |
| External OIDC | Token from explicitly configured HTTPS OIDC authority with MCP audience/groups/scopes | Exact `/.well-known/oauth-protected-resource/mcp` under the same public origin |

## Local credentials and browser approval

Client setup uses the deployment HTTPS instance URL and the server's supported credential-pairing capability. The client receives a short user code plus a separate high-entropy device capability. Open the trusted verification link explicitly, sign in to the existing NetRatel account UX, enter the code and approve the individually selected tenants/permissions and bounded expiry. Tenant and permission authority remains on the server; a client-provided list cannot grant access. An OIDC browser provider's Operator/administrator claim alone does not authorize credential grants: the account needs current durable NetRatel tenant/server role assignments for the selected permissions. If approval reports missing authority, a NetRatel administrator must assign those permissions before approval; provider groups are not copied into the credential.

This is **NetRatel integration-credential pairing**, not an OAuth device grant or a new OIDC authorization server. CLI/stdio approval issues an API-purpose credential; HTTP MCP client approval issues an HttpMcp-purpose credential tied to the canonical `/mcp` resource. Do not interchange those purposes or use the HTTP client bearer as the host's outbound gateway identity.

The verification URL points to the existing `/account/integration-credentials?tab=credentials` page without putting the short code in a query string. Pending approval expires after ten minutes. Polling starts at the server's five-second interval and slows down when instructed, up to thirty seconds. Pending, denied, expired, cancelled and throttled states are explicit. Poll only at the supplied interval. Successful delivery creates one normal revocable integration credential; a protected five-minute delivery envelope handles a lost response before secure-save acknowledgement. Unacknowledged delivery expiry revokes that newly issued credential; only acknowledge after durable secure save. Cancellation cleans up an undelivered approval, while an already acknowledged credential follows ordinary credential lifecycle. Deleting local profile files does not revoke the server credential. Manage/revoke it at `/account/integration-credentials`.

An older instance without pairing support can use an explicitly created existing credential. Create the correct purpose and resource on the account page, inject it through an owner-only file or supported secret store, and validate its actual grants. There is no ambient credential fallback.

The host requires local mode, the same canonical resource on API and MCP, an independent outbound API identity, and matched delegation settings. See [local mode](local-credential-mode.md), [configuration](configuration.md), and [setup](setup.md).

## External OIDC

Set `LocalCredentialMode=false` and configure the explicit HTTPS `Authority`, `Audience` equal to `PublicResourceUri`, and required groups/scopes. Keep issuer, audience, signing-key, lifetime and HTTPS discovery validation enabled. `RequireHttpsMetadata=false` is restricted to Development disposable authority tests; it is not production configuration.

Web routes the exact `/.well-known/oauth-protected-resource/mcp` endpoint to MCP. It advertises that resource, authority and applicable scopes. API `/.well-known/oauth-authorization-server` and `/connect/token` remain separate service-identity endpoints. Do not send MCP protected-resource discovery to API or advertise fabricated OAuth metadata in local mode.

External OAuth client compatibility is a separate explicit option; browser integration pairing does not retrofit `/connect/token`, issue an external OIDC token or alter existing provider registration. Configure the third-party client's supported authority/audience/scopes and redirect registration through the provider's established process.

## Authorization and rotation

Bearer forwarding must preserve an explicit Authorization header end to end. Configure HTTPS, origin restrictions and the current allow/deny operation policy; do not authorize every tenant or tool merely to make setup pass. Read scopes do not imply write, execute, administrator or all-target grants.

For local client rotation, create/approve the replacement credential, securely update the intended consumer, initialize and perform a permitted read-only call, then revoke the old credential. Host service-identity and delegation-key rotation are separate coordinated administrator operations, described in [setup](hosting-reference.md#rotation). Denial or revocation is an expected failed authorization result, not a reason to switch identities.

Connectivity application grants are explicit in both ingress modes: inspection and preview require instance `mcp.connectivity.read`; confirmed dispatch additionally requires `mcp.connectivity.test`. External OIDC identities resolve to their durable NetRatel account grants. OAuth scope/role and the active Connectivity ControlPlane policy remain independent requirements. See [operation targets and connectivity permissions](local-credential-mode.md#operation-targets-and-connectivity-permissions) for the exact operation matrix.

## Interpreting delegated status and policy evaluation

Local HTTP MCP exchanges issue only the scope and mapped roles required by the operation being called, after checking its application permissions. For example, `netratel_policy/evaluate` carries `netratel.mcp.admin` and `PolicyAdministrator`, while a separate `netratel_flows/list` exchange uses `netratel.mcp.observe` and `Operator`. `netratel_auth/status` reports its own read assertion. Its scopes and roles are not an inventory of every configured credential grant. External OAuth assertions retain their supplied scopes and roles; they are not necessarily attenuated to one scope. `delegationContext.authenticationSource` and `scopeContext` identify the signed source facts; local status uses `identityProvider: "local_integration_credential"`, while a verified external issuer uses `"oidc"`.

`netratel_policy/evaluate` evaluates the present inspection principal against the exact requested operation. An Admin-only inspection of `netratel_terminal/availability`, for example, retains `allowed: false` and `failureCode: "oauth_scope_missing"` for its Observe requirement. The additive `interpretation` names the inspection and evaluated operations, supplied scopes/roles, required scopes and alternative mapped roles. `assessment: "current_assertion_only"` and `authoritativeAdmissionReceipt: false` distinguish this diagnostic from a caller-owned reviewed admission receipt. Unassessed application grants, tenant admission, policy, runtime and capabilities remain `not_checked`; the diagnostic dispatches no business work. A matching policy summary or an allowed policy-only decision cannot establish execution success.

For current credential admission, inspect the caller-owned `netratel_access/credential` admission or `receipt` for the exact reviewed operation, tenant, environment and expiry. Then invoke a supported permitted operation for its current prerequisites and outcome. A separate `tenant_not_authorized` result calls for a current matching tenant policy through reviewed access; an inspection-context scope mismatch does not explain that denial or authorize broader scopes.

## Temporary host verification

Host setup requests a separate **Temporary verification client**. The administrator must explicitly consent to `mcp.discovery.read`; the server rejects tenant grants or any broader scope and caps the credential at 15 minutes. It cannot be saved as an ordinary profile. Setup cancels/revokes it after success, failure, cancellation or disposal. Unacknowledged delivery also has a five-minute recovery deadline.

Interrupted setup retains an owner-only cleanup capability record, containing no bearer credential. Resume retries cleanup before requesting another verifier. If the API is unavailable, the terminal reports `cleanup_pending`; retain the private setup/client volume and rerun verification after connectivity is restored. The server cleanup worker and credential expiry bound abandoned access independently. Persistent host identity and unrelated credentials are unaffected.

Deleting a client credential removes its authentication verifier, grants and protected pending delivery. Minimal nonsecret actor/time/correlation history remains for audit. MCP audit schema 2 adds separate transport, protocol, operation and overall outcomes; legacy `Success` retains its protocol meaning. Unstructured results can leave operation/overall outcome unknown.

Reviewed access uses the existing pairing and credential authorities. `netratel_access request/receipt/resume/cancel/credential` requires the narrow instance permission `mcp.discovery.read`; requesting scopes never grants them. An interactive operator with current grant and policy authority may approve the exact immutable proposal. The credential owner may be that operator; the receipt marks `owner_self_approval` and audit records the same owner and actor. An owner administrator with current `mcp.policy.admin` instance permission may use `owner_apply` with the exact pending receipt hash and explicit confirmation; the persisted mode is `owner_direct`. Approval, issuance and redemption recheck owner state, resource/environment, operation mapping and target/policy/credential revisions. A changed grant or expiry needs a new request. Creation uses the protected setup channel `/mcp/setup/access/start` and the existing one-time device delivery; native tool output contains safe metadata only. The [setup guide](setup.md#reviewed-client-access-and-exact-expiry) gives runnable client commands.

Reviewed credential expiry uses exact UTC instants at microsecond resolution: at most six fractional digits, such as `2030-04-01T10:11:12.123456Z`. Unsupported precision is rejected before date parsing; accepted input is never rounded. The 30-day preset adds exactly 720 hours to the displayed representable UTC reference.

## Bounded operator policies in the Web UI

Open **Admin → MCP Policies** (`/admin/operator-policies`) while signed in with current `mcp.policy.admin` authority. The page is hidden from accounts without this permission, and its API checks the current assignment on every request. Integration credentials and service tokens cannot use the account mutation adapter.

Choose the environment and optional tenant filter to load the authorized scope.
Within those results, search by policy name or ID, principal, tenant, client/tag,
or operation/family. Search is case-insensitive. Lifecycle filters select Active,
Disabled or Revoked policies; target filters select Exact client, Tenant, Client
tag, Control plane or Development environment. The list starts at ten rows per
page and offers page sizes of 10, 25, 50 or 100. Changing a search or filter
returns to the first page.

Choose **Inspect** on the required row. A centered, bounded dialog opens with a
loading state, so inspection stays visible even when the selected policy is
near the end of the list. The dialog shows its exact principal and target
selectors, classification filter, operation families, path grants, limits,
expiry, version, lifecycle state and immutable change audit. Exact-client
policies also show the server-owned target profile and its version.
Working-directory grants are exact: `/tmp` does not grant `/tmp/example`.
Inspection, editing, frozen preview, confirmation and readback remain inside the
scrollable dialog with its footer actions available. Closing it preserves the
list's search, filters, page and selected row; it does not undo an accepted
change or discard its receipt.

For an existing active policy, select **Edit reviewed bounds**, add the required directory as a separate line and choose a bounded expiry and audit reference. Unchanged limits are preserved. **Preview exact change** freezes the draft and shows the complete proposed authority. Tick the review acknowledgement and confirm. Create, replace, disable and revoke use the same policy engine, target-profile binding, durable preview/confirm service and idempotency checks as native MCP. Plan credentials remain in server-side component memory and are never displayed. An interrupted confirmation can retry the same plan to retrieve its original receipt. After acceptance, the accepted version and correlation remain visible while policy and audit readback is checked. A different current version, missing change audit or failed read is shown separately; **Refresh accepted receipt** repeats only the reads.

New create receipts retain the original creation metadata and persisted timestamp precision. Exact replay returns that accepted definition even after a later replacement or disable. Legacy identifier-only create receipts can replay only while the original version remains unchanged; otherwise `idempotency_replay_unavailable` requires current policy/audit inspection instead of presenting a later definition as the original result. Current authority, plan expiry and exact payload checks still apply, and replay does not write another policy or audit.

The signed-in administrator also needs an active, independently assigned parent operator policy that permits `PolicyAdministration` for the selected environment, tenant and target. A `ControlPlane` parent cannot authorize a `DevelopmentEnvironment` policy; environment-wide lifecycle changes require an environment-wide parent on every phase. The page displays the authenticated subject needed for that selector. This account route does not bypass a missing parent policy or allow an administrator to change a policy selecting their own subject, group, role or client. Initial authority setup remains an independent administrator's existing policy-administration task. A stale policy version, changed authorizing policy or changed target profile requires a fresh review.

To remove a temporary grant, replace the active policy with its previous reviewed bounds and confirm a new preview. **Disable** leaves an inactive, auditable policy; a later grant needs a new reviewed successor. **Revoke** is terminal. Neither action erases history. Use **Check an owned credential's current admission** in the selected policy to inspect the separate campaign credential's exact operation and current target-profile revision. Choose the named HTTP MCP credential from your account's list, then enter the catalogued tool and operation. The page never requests its bearer value. The existing owner-scoped admission API checks current authentication, configured grants, owner permission, tenant visibility and matching policy versions. Runtime/capability fields retain `not_checked` or conditional states until an actual permitted operation verifies them. Credentials owned by another account remain inaccessible through this check. Policy readback shows policy bounds; the admission result and a permitted operation establish current access across all matching policies. No operator is impersonated by the Web page.
