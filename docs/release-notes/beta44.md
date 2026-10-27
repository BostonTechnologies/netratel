# NetRatel 0.1.1-beta.44

This beta addresses operator-policy replacement, SSO investigation authority,
browser administration routing and login setup discoverability.

- **Policy edits (#387):** normalize expiry/review timestamps to UTC before
  PostgreSQL persistence, including replacements that preserve an instant with
  a different timezone offset. Return the persisted version/readback; stale
  expected versions still return conflict. A real PostgreSQL regression covers
  an active Production tenant policy with all operation families and added
  script/temp-root bounds.
- **SSO investigations (#382):** retain bounded authority facts from validated
  OIDC tokens for saved worker execution, with token expiry, monotonic claim
  refresh, current group configuration and credential attenuation. External
  identities need no local role assignment when using this supported SSO
  path. See [authentication](../deployment/CONFIGURATION.md#interactive-browser-authentication)
  for token requirements and revocation semantics. Empty/non-JSON 403 responses
  retain a useful permission message and HTTP status.
- **Scalar administration (#357):** forward the explicit account-backed policy,
  audit and target-profile routes through Web to API. The selective gateway
  allowlist and API account authorization remain in force.
- **Login setup (#383):** prominent Local/SSO/Hybrid navigation, exact API/Web
  environment settings, a recreate-and-verify recipe, and PostgreSQL-only
  deployment guidance.
- **Older Linux helpers (#361):** document the one-time verified repair for
  installed helpers that require `unzip`. Extend the minimal-host activation
  regression to repair the installed helper before an accepted update. Existing
  installations need that repair before their first unattended upgrade;
  an incoming package cannot replace a helper before it is invoked. Follow
  [Linux updater recovery](../client/CLIENT.md#repair-an-older-installed-linux-updater).
- **Owner mode (#356):** the supported `owner_apply` path remains available
  without a separate reviewer screen. Its existing exact-hash confirmation,
  owner/current-authority checks and durable `owner_direct` receipts are retained.
  See [HTTP MCP setup](../mcp-http/setup.md).
- **Dependencies (#351):** the full-solution transitive NuGet scan reports zero
  vulnerable packages across 23 projects. GitHub Dependabot alert reconciliation
  needs an account with security-alert access; the current integration denies
  that endpoint.

Run the supplied migrations before starting API/Web: this release adds nullable
external-authority fields to the identity schema. Sign in with a fresh OIDC
access token when verifying an investigation.
