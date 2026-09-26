# Configuration and authentication

NetRatel reads standard .NET configuration providers. Use environment
variables, a deployment secret mechanism, or mounted configuration files for
instance-specific values. The repository examples use reserved placeholders and
are not a usable production configuration.

The running [API reference](API_REFERENCE.md) identifies the credential scheme
accepted by each operation; it is a contract viewer, not a secret store.

## Native Client configuration precedence

The Client reads configuration in this order, from lowest to highest
precedence:

1. Packaged `appsettings.json` and optional `appsettings.{Environment}.json`.
2. The installed `clientsettings.json` written by an older supported package.
3. Explicit deployment values from `NetRatelCLIENT__...` service variables or
   ordinary `Client__...` environment variables.
4. Explicit command-line values such as `--api`, `--tenant`, and
   `--enrollment-code`.

The packaged files are defaults, not deployment choices. An ordinary restart
therefore keeps a valid installed URL, tenant, identity, and non-default
behavior when a new package contains placeholder defaults. Service/environment
values remain authoritative when an operator intentionally changes the
deployment, and command-line values remain the highest-precedence one-shot
selection. Enrollment credentials and the installation identity are persisted
separately; changing a package or repairing its updater does not silently
reset them.

Windows service, systemd, and launchd templates place the explicit API and
gateway values in the service environment so an updater can activate a package
with fresh defaults without repointing an existing installation. If an
operator intentionally changes the instance URL, verify the tenant and
issuer/origin contract before restarting the service and retain the existing
identity unless a deliberate identity reset is required.

The API image also installs `libgssapi-krb5-2` before dropping to its non-root
runtime user. The release image gate first loads `libgssapi_krb5.so.2` as UID
1654 and then runs the API assembly's negotiated-authentication smoke path.
The latter may report `UnknownCredentials` in a credentialless disposable
container: that result means the native GSSAPI path was reached; missing
libraries, unsupported negotiation, and unexpected status codes fail the gate.

## Required persistent state

- PostgreSQL is the only supported application and identity database.
  `Database__Provider` may be omitted or set to `PostgreSql`/`postgres`.
  Explicit `Sqlite` and unknown values fail before application state is
  created. `ConnectionStrings__NetRatelDb` takes deterministic precedence
  over the compatibility `ConnectionStrings__Default` alias.
- `DataProtection__KeysDirectory` is a persistent writable path for API key
  material. Web uses `NetRatel_KEYS_DIR` when supplied, otherwise its
  `DataProtection:KeysDirectory` value.
- Client artifacts and application storage must be mounted outside an
  ephemeral container filesystem when those capabilities are enabled.
- `AgentAuth:PrivateKeyPath` points to a deployment-supplied ES256 private key
  used only for native-agent token issuance. Mount it read-only and keep it out
  of the repository and ordinary application data volume. Generate a distinct
  key for each instance through your approved secret-management process.

Back up PostgreSQL and persistent key/artifact volumes
together. Replacing a Data Protection key ring invalidates cookies and
protected state.

## Public client install links

The API derives both the public install-link Web URL and the client API base
from the effective administrator `Site URL` under `/admin/branding`. Set that
value to the canonical public HTTPS origin reachable by a new client. Include a
supported external base path when the reverse proxy maps it to the corresponding
application routes. The API rejects a missing, HTTP, localhost, private DNS
suffix, credential-bearing, query-bearing, or fragment-bearing Site URL before
it creates an install grant. No separate public URL environment variables are
required.

The API stores each generated script and capability token with ASP.NET Data
Protection in PostgreSQL; the corresponding key ring in
`DataProtection__KeysDirectory` must be persistent and shared by all API
replicas. Keep the key ring and database in the same backup and restore set.
If a key is lost, public fetch fails closed and an operator must revoke the
affected grants and issue new links. Never put the key ring in a public webroot.

The Web application proxies `/clients/install/*` to the API so the displayed
URL returns a raw `.sh` or `.ps1` file without a browser session. Configure
your external proxy to forward that path to Web or API, avoid logging the
capability path or query string, and disable intermediary caching. The
application sends `Cache-Control: no-store`, `nosniff`, no-referrer, and
no-index headers. Externally managed proxy logs are outside application
logging control and require their own redaction rule. The supplied public
HTTPS Compose ingress suppresses access and error logs for this path; the
API also redacts its own request log and omits these URL-bearing HTTP spans.

Generated links contain immutable protected script snapshots. Updating the
installer template or publishing a replacement package does not rewrite an
issued script or archive. Revoke a link from the Clients management page (or
`POST /api/v1/client-install-links/{id}/revoke`) and generate a new link after
confirming the artifact, tenant, public origin, expiry, and service options.
Older links remain supported until they expire, are revoked, or exhaust their
enrollment allowance; an old package can still be recovered by starting a new
link generation rather than editing the protected URL or manually replacing
the downloaded script.

An operator can generate a link from **Clients → Artifacts → Generate script**,
inspect and copy its script and command, explicitly download the same script,
or revoke it. Management endpoints are under `/api/v1/client-install-links`;
the previous script file-response API remains available to existing callers.
Each link binds one tenant, runtime, verified local artifact version and SHA256,
install options, absolute expiry, and enrollment-use limit. Anyone holding an
active URL can spend its remaining authorized enrollment allowance. A script
fetch, HEAD request, preview, copy, or download does not spend a use; a
successful device enrollment does. Expiry, revocation, tenant deletion, and
exhaustion are checked on every public request. Revocation also disables the
underlying enrollment code, including copies of the script downloaded earlier.

Inspect the script before executing. Example commands use a placeholder
origin; copy the actual command from the generated result:

```bash
bash -o pipefail -c "curl -fsSL 'https://netratel.example/clients/install/<token>.sh' | bash"
```

```powershell
Invoke-WebRequest -UseBasicParsing -ErrorAction Stop 'https://netratel.example/clients/install/<token>.ps1' | Invoke-Expression
```

The shell command propagates a failed HTTP fetch. Installing a Linux systemd
service or macOS launch daemon requires root; installing a Windows service
requires an elevated PowerShell session. The macOS installer uses `launchd`
and `shasum`, while the Linux installer uses `systemd` and `sha256sum`.

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

`Authentication:Mode` may be `Local`, `Oidc`, `Hybrid`, or `Auto`. The source
and image Compose recipes expose it as `NETRATEL_AUTHENTICATION_MODE`. In `Auto`
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

## Integration credential expiry

The Integration editor treats the selected calendar date as a **UTC date**. A
credential selected for 29 February remains valid through
`2028-02-29T23:59:59.9999999Z`, regardless of the browser's locale or local
time zone. Select a later UTC date if the credential must survive into the next
day. The editor accepts dates from tomorrow through the next year and shows
the UTC interpretation before creation.

## Health endpoints

`/health/live` and `/health/ready` are protected by the `HealthRead` policy.
Use a deployment identity with that policy for authenticated monitoring rather
than exposing an unauthenticated production probe.
