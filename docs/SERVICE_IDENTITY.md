# Tenant service identities and reciprocal Helpdesk links

NetRatel service clients are tenant-owned machine identities. Their creator is recorded for audit; the creator's account, login roles and personal credential rights do not become machine rights. Personal integration credential values remain `Api = 0` and `HttpMcp = 1`.

An instance integration administrator can open **Account → Integration Credentials → Helpdesk M2M** and save public service settings before linking. Empty optional deployment settings leave this setup available without a restart. Select the actual public Web address and separately confirm the actual API address. The API address can also come from the existing validated `ClientArtifacts:PublicBaseUrl`. An API address is never inferred from a request's `Host` header or from a peer's Web address. The installation GUID is persisted and differs from the incident producer GUID.

Explicit deployment values are authoritative and shown as locked fields. These example addresses are reserved documentation values:

```text
ServiceIdentity__Enabled=true
ServiceIdentity__WebBaseUrl=https://netratel.example.test
ServiceIdentity__ApiBaseUrl=https://api.netratel.example.test
ServiceIdentity__Issuer=https://api.netratel.example.test/services
ServiceIdentity__Audience=netratel.services
ServiceIdentity__AllowPrivateHttp=false
ServiceLinks__Enabled=true
ServiceLinks__AllowPrivateHttp=false
```

`ServiceIdentity:Issuer` can be omitted; its effective default is the explicitly selected API base plus `/services`. `ServiceIdentity:InstanceId` can be omitted; NetRatel uses its persisted installation identity, preferring the existing bootstrap installation GUID. Changing a configured installation GUID cannot replace that identity. The gateway remains a distinct optional endpoint selected from `ServiceLinks:GatewayBaseUrl`, `ClientArtifacts:PublicGatewayBaseUrl` or effective branding. The producer is adopted explicitly using the existing identity/source action; NetRatel never generates a replacement incident producer GUID.

Private HTTP requires an explicit deployment opt-in. The central typed options boundary uses `ServiceIdentity:AllowPrivateHttp OR ServiceLinks:AllowPrivateHttp` for both issuer and link validation. Neither flag is editable through the account UI; configuration reload recomputes the permission, including removal of a previous opt-in.

The managed issuer publishes `/.well-known/oauth-authorization-server` and `/.well-known/service-jwks.json`. `/connect/token` accepts bounded `application/x-www-form-urlencoded` `client_credentials` requests with `client_secret_post`. Managed access tokens use protected, persistent RS256 signing keys and a dedicated machine token purpose. Existing agent/OIDC ES256 keys and `/.well-known/jwks.json` retain their existing behavior.

The manual service-client panel approves one exact tenant and current resources/request definitions. It reveals a newly generated secret once; subsequent listings are redacted. Rotation has a finite predecessor overlap, and revocation stops issuance and validation through fresh database checks on every replica. Guided clients use the link coordinator's rotation flow. Deployment clients are shown as read-only complete profiles.

Managed business scopes are limited to:

- `netratel.orchestration.read`: tenant/resource-filtered health, ping and existing catalog reads.
- `netratel.orchestration.invoke`: invocation of explicitly approved existing request definitions and current enabled target resources.

Neither scope grants `netratel.api`, tenant creation, definition creation, input synchronization or general administration. `bostec.service-link.verify` and `bostec.service-link.control` are bounded protocol rights. Pending, prepared and in-doubt credentials have protocol rights only. Revoked links retain finite control recovery and non-secret status/tombstone records under their unchanged enabled approved identity. Explicit issuer/link disablement or identity, gateway, producer or endpoint drift denies authentication, including controls, until the approved configuration is restored or new consent is obtained. Cleanup continues while disabled.

Legacy deployment M2M remains a separate complete-profile path. The historical client `rateldesk-orchestrator` may use its underscore environment alias:

```text
M2M__Authority=https://api.netratel.example.test
M2M__Audience=netratel.api
M2M__AllowedCallerClientIds__0=rateldesk-orchestrator
M2M__AccessTokenLifetimeMinutes=10
M2MClients__rateldesk_orchestrator__AllowedAudiences__0=netratel.api
M2MClients__rateldesk_orchestrator__AllowedScopes__0=netratel.api
```

Supply `M2MClients__rateldesk_orchestrator__Secret` only through the deployment's secret mechanism. Keep one spelling per complete profile; an exact ID plus a conflicting underscore alias is rejected. A reserved metadata-only deployment declaration cannot issue a token or lend its identity to a database client. Do not combine a deployment ID or partial profile with a database client's secret.

Inbound generated secrets are stored as salted verifiers. Durable signing private keys, finite bootstrap escrow and stored outbound credentials depend on the existing protected Data Protection key ring. Preserve that ring and the application database across restart, upgrade and replica replacement. A missing ring fails closed; it must not trigger fresh signing identity or producer creation.
