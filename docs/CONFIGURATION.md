# Configuration and authentication

NetRatel reads standard .NET configuration providers. Use environment
variables, a deployment secret mechanism, or mounted configuration files for
instance-specific values. The repository examples use reserved placeholders and
are not a usable production configuration.

The running [API reference](API_REFERENCE.md) identifies the credential scheme
accepted by each operation; it is a contract viewer, not a secret store.

## Required persistent state

- `Database__Provider` is explicit: `PostgreSql` (the compatibility default)
  or `Sqlite`. Existing `ConnectionStrings__NetRatelDb` takes deterministic
  precedence over the legacy `ConnectionStrings__Default` alias.
- PostgreSQL remains the supported multi-instance provider. SQLite requires an
  absolute durable `Data Source` path and `Database__InstanceCount=1`; it is
  not a shared-volume or cross-host mode. See [SQLite](SQLITE.md).
- `DataProtection__KeysDirectory` is a persistent writable path for API key
  material. Web uses `NetRatel_KEYS_DIR` when supplied, otherwise its
  `DataProtection:KeysDirectory` value.
- Client artifacts and application storage must be mounted outside an
  ephemeral container filesystem when those capabilities are enabled.
- `AgentAuth:PrivateKeyPath` points to a deployment-supplied ES256 private key
  used only for native-agent token issuance. Mount it read-only and keep it out
  of the repository and ordinary application data volume. Generate a distinct
  key for each instance through your approved secret-management process.

Back up the selected provider's data and persistent key/artifact volumes
together. Replacing a Data Protection key ring invalidates cookies and
protected state.

## Bootstrap lifecycle

Before an instance is ready, the API keeps its lifecycle descriptor, journal,
key-material proof, and bootstrap proof under `Bootstrap:StateDirectory`
(default `/var/netratel/bootstrap`). Mount that directory on persistent
API-owned storage with owner-only permissions; it is part of the instance
backup and recovery set, not browser or Web storage.

On a fresh instance the API writes a high-entropy one-time proof to the private
`setup-proof` file in that directory. Read it only through an operator's local
or deployment-console access and submit it in the setup request body; never
put it in a URL, tracked configuration, screenshot, or browser storage. It
expires after `Bootstrap:SetupProofLifetime` (one hour by default) and a later
API restart creates a replacement. A deployment may instead mount an explicit
`Bootstrap:SetupProofPath`; replacing that secret with a different value and
restarting the API renews it. The API does not generate a replacement into a
deployment-managed secret mount.

`Bootstrap:AllowedOrigins` is an allow-list for browser-originating setup
requests. Leave it explicit for every public browser origin. Direct operator
and CLI-style requests have no `Origin` header; forwarded headers are not
trusted unless the host has configured ASP.NET Core trusted proxies.

Before ready, the API exposes only deliberate bootstrap status and setup
operations; it cannot expose business APIs or let the browser alter
deployment-owned provider configuration. The guided setup transaction creates
the initial local administrator and tenant only after a claimed proof and
selected-store validation. See [first-run setup](FIRST_RUN_SETUP.md). An
existing PostgreSQL/OIDC deployment is adopted only from durable NetRatel
continuity evidence; an unavailable configured store or missing bootstrap key
material intentionally enters recovery rather than fresh setup.

## Interactive browser authentication

`Authentication:Mode` may be `Local`, `Oidc`, `Hybrid`, or `Auto`. In `Auto`
(the default when unset), a deployment with a configured OIDC authority remains
OIDC-only, while an installation without OIDC selects local accounts. Set
`Hybrid` explicitly to offer both mechanisms. `Authentication:Local` controls
only the local browser-cookie name and the explicit
`AllowInsecureLocalhost` loopback-only source-evaluation switch. It is not a
general HTTP production mode: public local cookies remain secure, HTTP-only,
and same-site lax. Native local sign-in presents password first and MFA only
for an enrolled account; it never enables self-registration or a default
administrator.

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

The API applies the same enabled/disabled contract. It only selects the
machine-token scheme when both configured issuer and audience match; human and
machine audiences can therefore share an issuer. When disabled, retained
machine settings do not authenticate a machine endpoint or add session roles.

Machine-token credentials require a dedicated audience distinct from the human
OIDC audience/client ID. The same issuer may serve both audiences; multi-audience
machine credentials are rejected to avoid ambiguous role augmentation. The
`Authentication:OidcAiAgent` section remains a compatibility alias only when
the canonical `Authentication:MachineToken` section is absent. A retained alias
cannot override a canonical disabled configuration.

## Reverse-proxy trust

The Web service ignores `X-Forwarded-For`, `X-Forwarded-Host`, and
`X-Forwarded-Proto` unless the direct peer is loopback or is explicitly listed
in `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownIPNetworks`.
Configure the public hostnames expected from that proxy in
`ForwardedHeaders:AllowedHosts`. For example, a deployment with an internal
proxy range can use:

```json
{
  "ForwardedHeaders": {
    "KnownProxies": ["203.0.113.10"],
    "KnownIPNetworks": ["2001:db8:1234::/48"],
    "AllowedHosts": ["netratel.example.com"]
  }
}
```

Do not add broad private-network ranges unless every sender in that range is a
trusted proxy. Direct local evaluation needs no configuration beyond the
loopback default.

## Health endpoints

`/health/live` and `/health/ready` are protected by the `HealthRead` policy.
Use a deployment identity with that policy for authenticated monitoring rather
than exposing an unauthenticated production probe.
