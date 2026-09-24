# NetRatel 0.1.0-rc.5

`0.1.0-rc.5` is a prerelease for evaluation and integration testing. It is not
a stable release channel and must not be used for unattended fleet updates.

## Local-first operation

- A fresh single-node installation can use SQLite and guided local-account
  setup with no external identity provider or SMTP service.
- PostgreSQL remains supported for durable multi-instance deployments. Existing
  PostgreSQL/OIDC installations retain their established identity mappings,
  native Client connectivity, signing material and operational configuration.
- OIDC is optional for Web login: configure it only for an OIDC or hybrid
  deployment. External-OIDC HTTP MCP remains available separately.
- Account administration provides scoped roles, local-account lifecycle and
  recovery controls, and purpose-separated integration credentials.
- Global search applies the caller's effective tenant scope before querying;
  scoped local accounts cannot discover other tenants' data.

## Automation, interface and administration

- CLI and stdio MCP use API-purpose scoped integration credentials. HTTP MCP
  uses a distinct purpose and, in local credential mode, exchanges its ingress
  credential for a short-lived caller-bound API execution credential.
- Deployment branding resolves deployment configuration, administrator
  overrides and the approved NetRatel defaults without changing protocol,
  package or Client identities.
- Theme selection applies its palette before app-controlled visible content.
- `/api/docs/` exposes the release OpenAPI document with stable taxonomy,
  operation-level security descriptions and same-origin Scalar reference UI.

## Artifacts and verification

This prerelease includes a Linux x64 Compose bundle, Linux x64
framework-dependent CLI and stdio MCP archives, a downloadable CLI NuGet
package, and self-contained native Client archives for Linux x64, Windows x64
and macOS arm64. Each release asset has a checksum; the workflow also supplies
SBOMs and build provenance attestations. Verify the exact release `SHA256SUMS`
before extraction and use only the immutable image digests in the promoted
Compose bundle.

See `docs/SELF_HOSTING.md` for fresh and established-OIDC operator journeys,
`docs/CLI_AND_MCP.md` for credential purpose and use, and `docs/RELEASES.md`
for provenance verification and the supported release boundaries.
