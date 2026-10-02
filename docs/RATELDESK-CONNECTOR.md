# RatelDesk incident connector

This is a typed NetRatel-to-RatelDesk incident connector. It does not use client
enrollment credentials, the reverse RatelDesk-to-NetRatel M2M integration, an
arbitrary webhook URL, a queue endpoint, or automatically created customers.

The receiver source reviewed for this slice is RatelDesk commit
`9a9a8abd2578dc31d1ec500dd3893478f8fa2749`. Its normal creation endpoint is
`POST /api/v1/incidents/`. The transmitted subset of `CreateIncidentDto` is
`Title`, `Description`, numeric `Priority` (Low=0, Medium=1, High=2, Critical=3),
`CustomerId`, `OrganizationId`, optional `AssignedToId`, and `CategoryIds`.
The credential is an API-purpose `Bearer rdk_...` integration credential owned
and scoped in RatelDesk. A source checkout does not verify a deployed release.

## Current delivery boundary

Automatic incident delivery is unavailable. The reviewed receiver has no
verified source/action-key fingerprint receipt, replay/conflict result, receipt
reconciliation, or atomic incident plus confirmation outbox transaction.
NetRatel reports `receiver-idempotency-unverified` and sends no incident POST
from a flow. Setting Enabled does not certify receiver capability. Connection
test success means only that read-only mapping lookups passed.

The normal-create transport is independently tested for its actual DTO shape
and bounded faults. It is not exposed through a hidden test-incident API or an
automatic action. A possible remote commit followed by timeout, cancellation,
server error, redirect, invalid receipt, or lost/oversized response produces
`DeliveryUnknown`. That transport performs no blind retry. A future explicitly
authorized disposable test action must first persist its action record and must
not turn an uncertain result into a second incident.

Completing #146 requires the separately scoped RatelDesk #116 receiver,
verification against the intended deployed compatible version, and actual
receiver tests proving one incident and one confirmation for concurrent/replayed
logical actions. Those checks have not been claimed by the fixture tests here.

## Configuration and credentials

An operator approves exact HTTPS origins in server configuration:

```json
{
  "RatelDesk": {
    "AllowedOrigins": ["https://helpdesk.example", "https://helpdesk.internal:9443"]
  }
}
```

The default allowlist is empty. Origin paths, URL credentials, query strings,
fragments, HTTP, and unapproved origins are rejected. A self-hosted origin needs
the same explicit operator approval. Events cannot choose destinations. The
HTTP handler disables all redirects and cookies; it does not relax TLS trust.
Operations have a ten-second deadline, 32 KiB request and 128 KiB response
limits, eight operations globally, two per tenant, and one per connector.
Fixed lock stripes can conservatively share a slot. No retry loop is installed.
Rate-limit Retry-After values are bounded to 1–300 seconds.

Each tenant explicitly maps to an existing RatelDesk organization and requester
customer, with optional assignee/categories and severity-to-priority mapping.
Names, hostnames, email domains and provider-directory tenant IDs are never used
to infer these mappings. The read-only connection test uses the actual
`/api/v1/ticketing/organizations?module=incident`, customer and optional assignee
lookup routes plus incident category lookup. It verifies exact IDs and customer
organization membership, without exposing remote names, emails or diagnostics.
Responses exceeding their bounds fail closed.

The `RatelDeskConnectors` table retains encrypted credential material protected
by the existing API DataProtection key ring. Ciphertext is bound to the exact
tenant and connector. Connector responses expose only `HasCredential` and
`CredentialRevision`; they never expose plaintext or ciphertext. Retain the
existing persistent key ring when moving an API deployment.

Configuration and credential revisions are separate. Changing mappings,
destination, priority configuration or enablement advances the configuration
revision. Credential rotation advances only the credential and row revisions;
it does not change an already prepared action's semantic payload or key.
Optimistic row-version checks reject stale configuration or rotation writes.
New connector admission also uses a transaction-scoped PostgreSQL tenant lock
before counting and inserting, so concurrent different-ID creates cannot exceed
the 256-connector tenant limit. Existing overflow returns an explicit capacity
error instead of a truncated list; it does not hide records or delete settings.

## Tenant API

`GET /api/v2/connectors/rateldesk/tenants` returns only tenant IDs and names for
which the current caller can manage connectors. The setup page at
`/flows/connectors?tenantId={tenantId}` uses this list, explicit mapping fields,
password credential entry/rotation, a read-only mapping check and a sanitized
inert preview. It never offers a hidden test-incident operation. Dirty settings
require explicit discard before a tenant change. Pending writes prevent double
submission; cancelled or late responses cannot update another tenant's view.
Failed configuration writes retain their draft and new connector identity.
Credential input is cleared before rotation and on tenant changes or disposal.
The dedicated authenticated Web client disables automatic retries and redirects.
Its entire operation has a 30-second deadline and a streamed 1 MiB response
budget, including connector lists; declared and chunked oversize responses are
rejected before JSON parsing. The authenticated browser fixture covers actual
Routes/MainLayout, light/dark/system preferences, the navigation drawer, narrow
mapping controls, true 200% zoom equivalents, protected password rotation,
dirty/conflict retention and tenant changes without a receiver call.

Under `/api/v2/tenants/{tenantId}/connectors/rateldesk`:

- `GET` lists configuration summaries; `GET /{id}` reads one summary.
- `PUT /{id}` saves configuration with ExpectedRevision (zero creates).
- `POST /{id}/credential` rotates the API credential using ExpectedCredentialRevision.
- `POST /{id}/connection-test` performs read-only receiver mapping checks.
- `POST /{id}/dry-run` returns bounded plain fields, a fingerprint and fake receipt.

All routes check current `integration.manage` for the exact tenant before
configuration access. Ownership and dispatch additionally require current
`secret.use`, an existing enabled identity and current integration credential
grants when applicable. Persisted flows never substitute a global administrator
credential. Cookie-backed configuration, rotation and connection-test requests
use the existing `X-NetRatel-Account-Request: 1` mutation guard. Responses are
marked no-store.

Preparation is read-only: it pins exact customer/organization/assignee/category
IDs, bounded plain title/body, final priority, source occurrence metadata,
connector configuration revision, stable action key and shared semantic
fingerprint. #145 persists this prepared request and owns leases, attempts and
run/action receipts. Dispatch rechecks current actor and connector-owner
permissions, enablement, origin approval and unchanged pinned configuration.
It does not re-resolve mappings or mint a new key on retry. Receiver capability
absence is observable in connector summaries and flow action outcomes.
