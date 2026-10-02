# API reference

NetRatel serves its generated OpenAPI document at `/api/openapi/v1.json` and
its Scalar reference UI at `/api/docs/`. Both routes are anonymous so an
operator can inspect the contract before signing in; calling a protected
operation remains subject to the API's normal authorization boundary.

The UI is hosted by the Web application but reads the document through the
same-origin API proxy. It does not proxy credentials or store a server-side
secret. Scalar's **Try it** therefore uses the browser's normal same-origin
request rules, including antiforgery and an existing local or OIDC browser
session where that is the supported credential.

## Stable catalog

The release-generated document assigns every operation a deterministic ID and
one of these tags:

- Authentication/Accounts
- Setup/Instance
- Tenants/Access
- Clients/Enrollment
- Telemetry/Logs
- Scripts/Commands
- Jobs/Tasks
- Files/Terminal
- Remote Support
- Integrations/MCP
- Health/Diagnostics

Tags improve navigation only. They do not rename routes, environment-variable
aliases, cookies, MCP tool names, native Client enrollment paths, or existing
request/response contracts.

## Authentication shown by the reference

The document describes the credential boundary for each operation rather than
applying a blanket bearer requirement:

- Anonymous setup/status, discovery, branding, and explicitly public routes
  declare no credential.
- Local-account routes use the protected browser-session cookie.
- Interactive operator routes use the configured OIDC bearer boundary.
- M2M-only and delegated MCP routes use their dedicated bearer credentials.
- Native Client/agent and machine-token routes show their separate bearer
  schemes.

An integration credential is purpose-scoped. A normal API credential is not
an HTTP-MCP credential, and the HTTP-MCP exchange routes remain the only
place the delegated credential is accepted. Do not paste deployment secrets
into the reference UI.

## Client install link contract

`POST /api/v1/client-install-links` creates an idempotent, protected grant for
an existing verified local client artifact. Its JSON request names `tenantId`,
`runtimeId`, optional `artifactVersion`, `validForMinutes`, `maxUses`,
`installAsService`, `silentInstall`, and a caller-generated GUID
`idempotencyKey`. The JSON result includes the management `id`, selected
version and SHA256, expiry, remaining uses, public URL, install command, and
script preview. Repeating the same key and inputs returns the same grant;
changing inputs with that key is rejected. The route requires the
`ClientArtifactsWrite` operator policy.

`GET /api/v1/client-install-links` lists recent grant metadata, optionally
filtered by `tenantId`; `GET /api/v1/client-install-links/{id}` reads metadata;
and `POST /api/v1/client-install-links/{id}/revoke` revokes a grant and its
underlying enrollment code. These routes have the same operator policy.
Management responses should be handled as sensitive because creation returns
the protected script and capability URL.

Anonymous `GET` and `HEAD` on `/clients/install/{token}.sh` or `.ps1` return
raw script content only while the grant is active. Invalid, expired, revoked,
exhausted, extension-mismatched, and query-overridden requests fail. Fetches
do not consume enrollment uses. Clients should treat the URL as a bearer
capability and must not log it. The prior client-script file-response route
retains its existing response type for compatibility.

## Flows (0.1.1 beta)

`GET /api/v1/flows/tenants` returns tenants paired with `flow.read` authority
and their effective edit/publish/execute flags. All other routes are under
`/api/v1/tenants/{tenantId}/flows`; the tenant permission is checked before
definition, version or run disclosure.

| Route | Permission and behavior |
| --- | --- |
| `GET /`, `/template`, `/connectors`, `/{flowId}` | `flow.read`; bounded definitions, named template or currently authorized connector references. |
| `POST /validate`, `/dry-run` | `flow.read`; canonical validation and pure inert DTO preview. |
| `POST /`, `PUT /{flowId}/draft`, `POST /{flowId}/clone`, `PUT /{flowId}/enabled` | `flow.edit`; create or mutate an inert draft using `expectedRevision` for existing definitions. |
| `POST /{flowId}/publish` | `flow.publish`; create an immutable version after current connector authority/capability/revision checks. |
| `GET /{flowId}/versions`, `/{flowId}/runs`, `/{flowId}/runs/{runId}` | `flow.read`; bounded version and execution history. |
| `GET /versions/{versionId}`, `/runs/{runId}` | `flow.read`; direct paired-tenant lookup for monitoring deep links. |

Successful mutations return the canonical `FlowDefinitionDto`; publish
returns `FlowVersionDto`. Writes expose a bounded safe `code` on rejected
requests: 409 for revision conflicts, 429 for capacity, and 422 for unavailable
or unauthorized connector dependencies. An unverified RatelDesk receiver
returns `receiver-idempotency-unverified` and preserves the draft. Flow write
bodies are bounded before JSON binding, including chunked streams. There is
no browser/API execute route. The monitoring ingress and backend worker own
durable execution. See [Flows](FLOWS.md) for schema bounds, authority,
receipts/recovery, VeloxDev version and the receiver dependency.

## Release verification

The protected release and PR workflows validate the generated surface with
the normal build/test matrix, Scalar Web routing, local-first browser journey,
and source/release-image OIDC Compose smoke. For runnable deployment and
credential examples, use [first-run setup](FIRST_RUN_SETUP.md),
[configuration](CONFIGURATION.md), [self-hosting](SELF_HOSTING.md), and
[CLI and MCP](CLI_AND_MCP.md).
