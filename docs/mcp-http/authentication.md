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

For local client rotation, create/approve the replacement credential, securely update the intended consumer, initialize and perform a permitted read-only call, then revoke the old credential. Host service-identity and delegation-key rotation are separate coordinated administrator operations, described in [setup](setup.md#rotation). Denial or revocation is an expected failed authorization result, not a reason to switch identities.
