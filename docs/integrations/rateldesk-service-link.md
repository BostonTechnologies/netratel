# RatelDesk service credentials and reciprocal linking

NetRatel and RatelDesk use the frozen [bostec.service-link.v1](../contracts/bostec-service-link.v1.md)
contract. Its [schemas](../contracts/bostec-service-link.v1.json) and
[conformance fixtures](../contracts/bostec-service-link.v1.fixtures.json) match
the companion RatelDesk implementation. Incident delivery additionally requires
[rateldesk.incident-create.v1](../contracts/rateldesk-incident-create.v1.md).
A successful link or token request alone does not establish incident delivery
readiness.

## Configure the local public identity

Sign in using the existing Local, OIDC or Hybrid administrator account. Open
**Account → Integration credentials → Create → Helpdesk M2M (RatelDesk)**.
The public service settings panel configures the instance-wide Web and API
addresses, issuer, audience and enabled state. Supply the real canonical
addresses separately, including any supported path base. For a shared public
origin, explicitly confirm the same address for both roles. The native Gateway
address is separate and is never an OAuth token endpoint.

Settings saved in the Web interface take effect immediately. Explicit deployment
settings take precedence and lock their fields; empty optional values leave
those fields available for Web configuration. Changing an approved public
identity disables existing link authority until its original identity is
restored or administrators complete new consent. A product version update alone
does not change that identity.

The typed `ServiceIdentity` and `ServiceLinks` sections support deployment
configuration. Their public Web/API addresses must agree. An explicitly enabled
invalid configuration fails validation with a nonsecret error. An unused
integration does not prevent first-run setup. Private HTTP requires an explicit
deployment opt-in and is intended for isolated fixtures or private deployments;
peer input cannot enable it.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ServiceIdentity:Audience` | `netratel.services` | Dedicated service resource audience |
| `ServiceIdentity:AccessTokenLifetimeSeconds` | 300 | Access token lifetime, bounded to 60–900 seconds |
| `ServiceIdentity:CredentialMaximumAgeDays` | 90 | Finite secret lifetime |
| `ServiceIdentity:ManualRotationOverlapSeconds` | 600 | Bounded predecessor overlap |
| `ServiceLinks:AutomaticRotationEnabled` | true | Coordinated rotation for active managed links |
| `ServiceLinks:RotationAgeDays` | 60 | Begin rotation before secret expiry |
| `ServiceLinks:RotationOverlapSeconds` | 600 | Finite overlap within issuer policy |
| `ServiceLinks:GatewayBaseUrl` | unset | Optional descriptive native gateway address |

The installation identity is persisted independently of the incident producer.
Preserve the existing Flow producer identity when approving its source mapping;
do not create a replacement GUID for rotation or reconnect. A missing producer
mapping leaves incident linking unavailable with an actionable status.

## Connect from either application

In NetRatel, choose **Connect RatelDesk**, enter its Web base URL, and select the
approved tenant, resources, request definitions and orchestration scopes. Continue
discovers the peer's advertised canonical identities and grants no business
authority. Sign in to RatelDesk using its own administrator session, review both
directions and select its organization/customer boundary. Return to NetRatel and
approve the final narrowed grants. Either administrator can decline.

The same ceremony can start in RatelDesk under **Account → Integration
credentials → NetRatel M2M → Link NetRatel**. NetRatel then acts as the responder
and requests its own local approval. No administrator password is transferred
between applications and no service secret is placed in a browser redirect.

Both applications obtain real OAuth tokens and authenticated verification
receipts before enabling business access. **Completing setup** or **Recovery
pending** describes partial progress; use Resume to reconcile the recorded
attempt. If the responder was signed out, sign in through its normal login and
use the initiator’s explicit Continue action to reopen the original approval.
Login redirects never carry the ceremony proof. A failed or expired bootstrap
cannot silently become an active grant.
Connection Test uses authenticated read-only operations; it creates no incident,
job or Flow.

The RatelDesk-to-NetRatel direction authorizes only the selected orchestration
catalog and ingest operations. The reverse direction uses RatelDesk's separate
incident create, receipt read and target validation scopes. Invocation also
requires its narrow task callback scope. The business callback goes to the
pinned API route `/api/v1/orchestration/provider/callback`, using the recorded
request/task/execution and a RatelDesk-issued token. The browser callback has a
different purpose and URL.

## Manual clients and lifecycle

Expand **Manual service credentials (advanced)** to create only an inbound
service client. Confirm its peer installation, remote tenant, local tenant and
exact resource grants. The generated client ID and secret are revealed once
with the effective issuer, token endpoint, audience and scopes. Save the secret
in the deployment's secret store; listing and ordinary reads cannot retrieve it.
Configure the separate outbound direction explicitly or use guided linking.

Managed tokens use dedicated RS256 signing keys and `token_use=netratel_service`.
They do not become personal API/MCP credentials, browser identities, agent
tokens or broad administrator callers. Public OAuth metadata is at
`/.well-known/oauth-authorization-server`; managed public keys are at
`/.well-known/service-jwks.json`. The existing agent JWKS route is retained.

Rotate or revoke an owned managed client from the same panel. Guided links
coordinate successor verification, activation, caller switch and finite
predecessor retirement. Secret-only rotation preserves the client, link,
approved grant, producer namespace and already captured logical actions. Current
durable authority is checked before using cached tokens and at resource access,
including on another API replica.

Unlink stops local business authority immediately. If the peer is unavailable,
remote confirmation remains pending and recovery uses the original durable
revocation operation. Finite control-only recovery and nonsecret lifecycle
evidence are separate from business permission. Preserve the database and shared
Data Protection key ring together for restart, replica and backup recovery.

## Existing deployment M2M compatibility

Existing `M2M__Authority`, `M2M__Audience`,
`M2M__AllowedCallerClientIds__0`, `M2M__AccessTokenLifetimeMinutes` and
`M2MClients__...` inputs keep their legacy ES256 responsibility. The centralized
resolver maps an environment key such as `rateldesk_orchestrator` to its configured
client ID `rateldesk-orchestrator`, rejects ambiguous aliases and selects a
complete deployment profile. Deployment-owned clients are read-only. A database
client cannot shadow a deployment identity or borrow its secret or endpoints.

Existing self-issued external-service callbacks remain a separate legacy trust
path. Personal credential purposes retain `Api=0` and `HttpMcp=1`; the
`helpdesk-m2m` selection creates a tenant-owned service registration instead.

Real two-product acceptance and release receipts are tracked by NetRatel
[#164](https://github.com/BostonTechnologies/netratel/issues/164), connector
[#146](https://github.com/BostonTechnologies/netratel/issues/146), and epic
[#141](https://github.com/BostonTechnologies/netratel/issues/141). Companion work
is recorded in RatelDesk [#119](https://github.com/BostonTechnologies/rateldesk/issues/119)
and [#116](https://github.com/BostonTechnologies/rateldesk/issues/116). This setup
guide is not an execution receipt; runtime capability and the actual published
product pair must pass the integration gates before delivery is reported ready.

The six complete two-product browser setup, consent and recovery journeys are
pending owner acceptance of the deployed beta, rather than prerequisites for
merging the foundation or publishing `v0.1.1-beta.1`. The six-case/24-screenshot
harness remains available for explicitly requested diagnosis. Its preserved
[failed run](https://github.com/BostonTechnologies/netratel/actions/runs/37484815621)
failed at the NetRatel setup-to-login transition before reciprocal-link commands;
the cause remains unresolved. Required automated protocol, authorization,
database, component, ordinary browser, native, packaging, upgrade and publication
checks remain required. Keep the epic, connector and companion acceptance items
open wherever owner results are still outstanding.
