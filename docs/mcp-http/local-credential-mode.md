# Local HTTP MCP credentials

Local credential mode is an explicit alternative to the normal external OIDC
HTTP MCP deployment. It never starts an OIDC discovery request and does not
publish OAuth protected-resource metadata. It is appropriate only for an
operator-managed installation using NetRatel local credentials with an already configured API
and an HTTPS MCP origin.

The user creates a `HttpMcp` integration credential through explicit browser-approved client setup, the account API or the Integration Credentials page. Its resource must exactly equal the public
`https://.../mcp` URL. The credential is securely saved by setup or supplied to an MCP client as `Authorization: Bearer nrt_ic_...`; do not put it in a URL,
browser store, shell history, image, or Compose file.

For a combined Compose installation, use the normal image bundle plus the
optional overlay:

```sh
docker compose --env-file .env -f compose.images.yaml -f compose.mcp-http.yaml config --quiet
docker compose --env-file .env -f compose.images.yaml -f compose.mcp-http.yaml up -d
```

Set `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true` and `NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential`, the canonical public resource,
the selected API base URL, and a deployment-owned delegation key reference in
the environment file. Set the same `NETRATEL_MCP_DELEGATION_*` values for API
and MCP host. The key must be base64-encoded random material of at least 32
bytes, unique per environment and rotation generation. Do not set the OIDC
variables in this mode.

For a standalone MCP host paired with an existing API, configure the same
`NetRatel:Mcp:Delegation` values on both deployments, supply complete dedicated `NetRatel:Mcp:Outbound` environment/secret references or mount an isolated outbound configuration at `NETRATEL_MCP_CONFIG`, and set exactly one selected
development or production API allowlist URL. The host rejects a configuration
file whose API endpoint differs from that selected allowlist entry. Never use
a production API URL from a development host.

Local ingress authentication uses `/api/v2/mcp/local-delegation/authenticate`. Each delegated tool execution exchanges the ingress bearer only with
`/api/v2/mcp/local-delegation/exchange`. The API checks purpose, canonical
resource, active owner, current grant, target scope, and policy, then returns
a short-lived signed execution assertion. Business APIs receive their existing
gateway identity and that assertion, never the ingress bearer. The API
rechecks credential revocation, expiry, account status, resource, tenant grant
and effective access before each execution; confirmation, classification,
idempotency and audit behavior therefore remains caller-bound.

To use the established external OIDC path instead, leave
`NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=false`, set `NETRATEL_MCP_AUTHENTICATION_MODE=Oidc`, and configure the OIDC authority,
audience, required group and required scope. Existing OIDC deployments retain
their OAuth protected-resource metadata and behavior unchanged.

Use [the complete reference](configuration.md) for exact host/outbound/delegation mappings and [setup](setup.md) for persistence, container commands and rotation. [Web/YARP routing](../deployment/web-gateway.md) exposes `/mcp` under the same HTTPS origin without new DNS; routing is separate from authentication. [Verification](troubleshooting.md) must include initialize and a permitted read-only operation.

## Operation targets and connectivity permissions

Use each operation's published `targetModel`. Policy lists accept no filter or a tenant-only filter; unfiltered lists require an explicit instance `mcp.policy.admin` grant. Tenant filters require that tenant's grant. Policy previews accept their actual control-plane, tenant, client-tag or exact-agent selector. Exact-target inspection and target-profile changes retain the tenant/agent pair. A policy-ID read resolves its tenant and agent from persistence before checking the application grant. Existing policy mutations independently compare the persisted selector and version with the reviewed request. Supplying IDs cannot establish ownership.

Connectivity is supported in local and OIDC modes with these explicit application permissions:

| Operations | Application permissions | Scope and policy |
| --- | --- | --- |
| `settings`, `netratel` | Instance `mcp.connectivity.read` | `netratel.mcp.admin`, PolicyAdministrator role, active Connectivity ControlPlane policy |
| `preview_test` | Instance `mcp.connectivity.read` | `netratel.mcp.execute`, AutomationOperator role, active Connectivity ControlPlane policy |
| `test` | Instance `mcp.connectivity.test` and `mcp.connectivity.read` | `netratel.mcp.execute`, AutomationOperator role, active Connectivity ControlPlane policy |

Preview creates a confirmation plan and performs no delivery. The confirmed test retains identity/resource/instance/plan binding and idempotency; its only destination is the server-owned configured test destination. No caller-supplied host, URL, headers, credentials, timeout or configuration override is supported. An operator must review the concrete test before dispatch.

Capabilities publish `localCredentialSupported`, `applicationPermission` and `additionalApplicationPermissions` for each operation. These describe prerequisites; configured credential grants, attenuated delegation and effective policy admission remain separate facts. A missing application permission produces `local_credential_permission_denied`, not an expired-credential diagnosis. Permission removal, malformed target, ownership mismatch, revoked known execution and expired known execution retain distinct safe errors and correlation. Unknown incoming credentials keep a limited invalid-credential response.

Known raw bearers also receive `local_credential_expired` or `local_credential_revoked` only after a valid signed pairing proof and constant-time full-hash, purpose and resource checks. Wrong tokens, purposes and resources remain `local_credential_invalid`. HTTP 401 bodies carry only the bounded code, server-authored failure/remediation and safe correlation; they omit account, credential and ownership identifiers. These permanent authentication failures are non-retryable.

For reviewed bootstrap and additional scoped credentials, use the [reviewed access workflow](setup.md#reviewed-client-access-and-exact-expiry). Exact UTC expiry is retained from proposal through browser review, persistence, protected delivery and safe inspection; the UI's thirty-day preset means 720 hours from its displayed reference instant. A saved approved expiry is reused during resume. Native safe receipts expose the configured grant set separately from the single operation's delegated scopes/roles and effective policy admission. Existing secrets remain unretrievable.

Reviewed credential expiry uses exact UTC instants at microsecond resolution: at most six fractional digits, such as `2030-04-01T10:11:12.123456Z`. Unsupported precision is rejected before date parsing; accepted input is never rounded. The 30-day preset adds exactly 720 hours to the displayed representable UTC reference.


## Reviewed admission

Ordinary HTTP MCP credential creation stores the configured application grants. It does not install operator policies. A valid credential with `flow.read` grants for synthetic tenants 17 and 29 can authenticate while both `netratel_flows/list` calls correctly return `tenant_not_authorized` when matching Production policies are absent.

The create acknowledgment and credential details show configured grants separately from the current owner-scoped admission view. Open **Details → Refresh admission** after a reviewed reconciliation or an owner-authority change. The view identifies the credential lifecycle, current authentication validity, canonical resource, instance/environment, exact UTC expiry, durable owner permission, tenant visibility and matching exact-operation policies with their expiry. Its default read samples cover only `netratel_flows/list` in configured flow-read tenants, up to 32 targets with an explicit truncation notice. Other grants remain visible without an inferred operation decision. Agent readiness and capability remain not checked or conditional; authorization admission does not certify dispatch success.

For an exact additional target, the interactive account API accepts `POST /api/v2/account/integration-credentials/{credentialId}/admission` with `{"targets":[{"tenantId":17,"tool":"netratel_clients","operation":"telemetry","agentId":"11111111-1111-4111-8111-111111111111","profileRevision":1}]}`. Select the actual catalogued operation and persisted target/profile revision from authorized discovery. The API reuses the reviewed-access validator and receipt evaluator; this read-only inspection never creates a policy or changes grants.

Missing Production policies require the [supported reviewed proposal](setup.md#reviewed-client-access-and-exact-expiry): submit an exact `reconcile` proposal with the retained configured grants, selected tenant/operation targets and original exact UTC expiry, have an authorized operator inspect and approve it, then inspect/resume the same saved request ID. The credential owner may self-approve only with current MCP policy administration and every selected durable permission; the receipt identifies `owner_self_approval`. Do not request blanket scopes, replace a working credential or create hidden setup policies. The resulting admission applies only to the reviewed targets; unrelated tenants retain their existing access boundaries. Verify a permitted read through the configured client after reconciliation.
