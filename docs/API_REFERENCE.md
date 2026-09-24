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

## Release verification

The protected release and PR workflows validate the generated surface with
the normal build/test matrix, Scalar Web routing, local-first browser journey,
and source/release-image OIDC Compose smoke. For runnable deployment and
credential examples, use [first-run setup](FIRST_RUN_SETUP.md),
[configuration](CONFIGURATION.md), [self-hosting](SELF_HOSTING.md), and
[CLI and MCP](CLI_AND_MCP.md).
