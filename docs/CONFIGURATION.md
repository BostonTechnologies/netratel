# Configuration and authentication

NetRatel reads standard .NET configuration providers. Use environment
variables, a deployment secret mechanism, or mounted configuration files for
instance-specific values. The repository examples use reserved placeholders and
are not a usable production configuration.

## Required persistent state

- `ConnectionStrings__NetRatelDb` points to PostgreSQL.
- `DataProtection__KeysDirectory` is a persistent writable path for API key
  material. Web uses `NetRatel_KEYS_DIR` when supplied, otherwise its
  `DataProtection:KeysDirectory` value.
- Client artifacts and application storage must be mounted outside an
  ephemeral container filesystem when those capabilities are enabled.
- `AgentAuth:PrivateKeyPath` points to a deployment-supplied ES256 private key
  used only for native-agent token issuance. Mount it read-only and keep it out
  of the repository and ordinary application data volume. Generate a distinct
  key for each instance through your approved secret-management process.

Back up PostgreSQL and persistent key/artifact volumes together. Replacing a
Data Protection key ring invalidates cookies and protected state.

## Interactive browser authentication

Configure `Authentication:Oidc` with your OIDC authority, client identifier,
API scope, and callback paths. The user-facing application requires an
operator identity; it does not ship a default administrator, password, or
anonymous production mode.

The API independently validates browser access tokens from
`Authentication:Oidc`. Set its HTTPS `Authority`, API `Audience`, optional
`Audiences`/`ValidIssuers`, and the operator group under
`Authorization:Oidc:AdminGroupId`. `NameClaimType` and `RoleClaimType` default
to `preferred_username` and `roles`. Provide `OIDC_CLIENT_SECRET` through your
secret mechanism; never place it in an appsettings file. `TokenEndpoint` is
required when your provider does not publish its refresh-token endpoint at the
usual Azure-compatible authority path.

`Authentication:Azure`, `AzureAd`, `Authorization:Azure:AdminGroupId`, and
`AZURE_CLIENT_SECRET` remain temporary compatibility aliases for existing
deployments. New deployments must use the `Oidc` settings above.

OIDC discovery, issuer, audience, signing key, and lifetime validation remain
enabled. Identity-provider-specific administration APIs are not required for
ordinary browser sign-in.

## Machine-token bridge

`Authentication:MachineToken` is optional and disabled by default. When
enabled it validates an external OIDC token before creating its narrowly
scoped Web session. Configure HTTPS authority, audience, expected groups,
session roles, and permitted signing algorithms. This is distinct from API M2M
credentials and native-agent enrollment; do not reuse credentials between
those trust paths.

## Health endpoints

`/health/live` and `/health/ready` are protected by the `HealthRead` policy.
Use a deployment identity with that policy for authenticated monitoring rather
than exposing an unauthenticated production probe.
