# RatelDesk incident connector

The connector sends an incident to a saved, tenant-owned RatelDesk destination.
Delivery requires the authenticated `rateldesk.incident-create.v1` receiver,
its registered NetRatel producer namespace, and the exact approved organization
and requester customer. Saving settings or completing OAuth setup alone does
not enable delivery. Release interoperability evidence is recorded separately.

## Setup

1. Open `/flows/connectors` and choose a tenant for which you have current
   connector-management access. Load approved connections and producer. This
   displays the existing persisted Flow producer and the installation identity
   separately; their GUIDs may differ.
2. For reciprocal connections, approve the existing Flow producer before
   incident pairing. Adoption also requires current instance integration
   management and an interactive human account. It cannot replace an existing
   different producer or change a previous attempt. Review reciprocal
   connections through the shared connection panel and select an approved
   incident connection for the same tenant.
3. Alternatively, select registered API credential mode. A RatelDesk human
   administrator registers the displayed Flow producer against the dedicated
   API-purpose credential and the explicit organization/customer. Store its
   `rdk_` bearer through the protected credential field. It is never an OAuth
   client secret. A replacement API credential needs an approved binding to the
   existing RatelDesk namespace; revocation of the predecessor is separate.
4. Save the exact API base, including any configured path base, and mapping.
   Reciprocal selection supplies its approved base and organization/customer;
   assignment, categories, and severity-to-priority remain explicit.
5. Run the authenticated receiver check. It reads capabilities and validates
   the exact source and target without creating an incident. A failed or
   interrupted new check invalidates the preceding readiness observation. The
   catalog also rechecks current local owner, source, profile and deployment
   policy and treats observations older than two minutes as unavailable.

The connection supplies managed credentials from the existing approved profile.
Rotate or revoke them in reciprocal connection management. API credential
rotation updates only the protected credential revision; neither mode copies
secrets into a Flow graph, prepared action, receipt, browser DTO, or audit.
Changing mode, peer, mapping or other semantics advances the connector revision
and cannot retarget queued work. A mode change discards the prior manual secret.

## Deployment restrictions and transport bounds

Optional `RatelDesk:AllowedOrigins` retains exact root HTTPS origin restrictions.
`RatelDesk:AllowedApiBases` additionally restricts exact API/path bases.
`RestrictToConfiguredPeers=true` denies traffic when both restrictions are empty.
Otherwise an empty optional restriction permits the current durable,
owner-approved connector or approved link, rather than discovery alone.
Manual private HTTP requires `RatelDesk:AllowPrivateHttp`; managed mode follows
existing service-link/service-identity private HTTP policy. Browser input and
remote metadata cannot change those permissions or TLS trust.

```json
{
  "RatelDesk": {
    "AllowedOrigins": ["https://helpdesk.example"],
    "AllowedApiBases": ["https://helpdesk.example/support"],
    "RestrictToConfiguredPeers": true
  }
}
```

Every operation validates the captured API identity and fixed route. DNS and
all resolved IPv4/IPv6 addresses are checked before each new owning socket;
policy is rechecked after DNS and connection. Redirects, proxies, cookies,
automatic transport retries and socket pooling are disabled. TLS retains its
normal hostname and certificate validation. Bounds remain ten seconds per
HTTP operation within the original twenty-second Flow execution budget,
32 KiB requests, 128 KiB responses, eight global/two tenant/one connector
operations, and bounded 1–300 second Retry-After scheduling. No HTTP occurs
inside a Flow database transaction.

## Durable delivery and recovery

Read-only preparation captures receiver/source/namespace, semantic connection
and mapping revisions, fixed endpoints, receiver guarantees, exact wire body,
receiver fingerprint, and wire key. Credentials are freshly resolved for this
captured target on each operation; secret-only rotation cannot change the
snapshot. Target validation is not required to read an already accepted
receipt after category deactivation or assignee changes.

The historical Flow local key remains unchanged. New wire keys use a
SHA-256 domain-separated projection of source, occurrence, event, immutable
Flow version and node GUIDs. Previously durable conforming wire keys remain
exact. A new key is never used to escape uncertainty or fingerprint conflict.
Frozen receiver fingerprint normalization is separate from the preserved V1
Flow semantic fingerprint. V1 prepared records and their immutable hashes are
not rewritten; new receiver evidence is an additive V2 sidecar saved atomically
with initial preparation under the existing lease fence.

Before HTTP, the existing Flow/Monitoring/current-authority guards admit the
operation and persist `MayHaveCommitted`. A verified 201 create, 200 replay or
200 lookup must match the complete immutable receipt, top-level IDs, captured
namespace/key/fingerprint/map and relative Location. Missing, malformed,
redirected, oversized, canceled or transport-failed responses cannot erase a
possible remote commit. Recovered work looks up the same key first. Only a
verified safe same-key/body replay with current authority and the original
horizon can POST again. A 404 lookup does not prove that an in-flight commit
cannot succeed; a 410 never permits a new creation.

At most five POST attempts are admitted. An ambiguous fifth attempt can receive
one durably consumed read-only reconciliation turn, without a sixth POST or a
new action identity. Original run budgets and the 24-hour automatic age survive
restart. Exhaustion with an unconfirmed send remains `DeliveryUnknown`, and
uncertain history is retained rather than pruned as ordinary failure. Receipt
persistence rechecks current tenant authority and the exact lease; a stale
worker cannot finish another worker's action. Flow disablement or suppression
still prevents a new POST while an authorized read-only lookup may recover an
original receipt. External navigation remains absent until a real Web route is
verified; the accepted API Location and original receipt are retained privately.

## Tenant API and Web behavior

Under `/api/v2/tenants/{tenantId}/connectors/rateldesk`:

- `GET` and `GET /{id}` return current configuration/readiness summaries.
- `GET /setup` resolves the existing Flow producer and eligible tenant links.
- `POST /setup/source` takes only `expectedIdentityRevision` and adopts that
  existing producer through the foundation human-administrator operation.
- `PUT /{id}` saves configuration and explicit authentication reference using
  `expectedRevision`; zero creates a new connector.
- `POST /{id}/credential` rotates only API-purpose credentials using
  `expectedCredentialRevision`; managed rotation uses connection management.
- `POST /{id}/connection-test` performs authenticated capability/target reads.
- `POST /{id}/dry-run` returns an inert sanitized preview and sends no incident.

Every route requires current connector-management permission for the exact
tenant before reading configuration. Adoption additionally requires interactive
account and instance integration-management authority. Dispatch retains the
original configuring principal/credential and current SecretUse checks; service
transport authority never supplies a human user FK. Cookie-backed mutations
retain `X-NetRatel-Account-Request: 1`; responses use no-store.

The Web client retains its 30-second operation and streamed 1 MiB response bound.
Dirty drafts, pending-write guards, stable new connector identity, late-response
fences, tenant changes and password clearing remain. The 256-record tenant
admission uses its existing transaction lock and optimistic row CAS. The UI
shows connection status and receiver delivery readiness separately; no hidden
test-incident action is exposed.
