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

## Release verification

The protected release and PR workflows validate the generated surface with
the normal build/test matrix, Scalar Web routing, local-first browser journey,
and source/release-image OIDC Compose smoke. For runnable deployment and
credential examples, use [first-run setup](FIRST_RUN_SETUP.md),
[configuration](CONFIGURATION.md), [self-hosting](SELF_HOSTING.md), and
[CLI and MCP](CLI_AND_MCP.md).
