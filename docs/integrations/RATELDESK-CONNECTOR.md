# RatelDesk incident connector

The connector sends an incident to a saved, tenant-owned RatelDesk destination.
Delivery requires the authenticated `rateldesk.incident-create.v1` receiver,
its registered NetRatel producer namespace, and the exact approved organization
and requester customer. Saving settings or completing OAuth setup alone does
not enable delivery. Release interoperability evidence is recorded separately.

## Setup

Use [System connections pairing](rateldesk-pairing.md) at
`/account/integration-credentials`. Pair either way, select the exact tenant,
organization and customer, enable **Create incidents**, and select **Save**.
Save creates or adopts the real tenant-owned Flow connector and validates its
receiver binding automatically. Select the saved connector in the Flow editor.
There is no separate manual credential form, producer adoption, approval or
mandatory test. The existing installation and Flow producer identities remain
stable; distinct named mappings have their own receiver namespaces.

Credentials stay protected on the server. Every delivery resolves current
mapping authority and the enabled incident capability. Deleted or disabled
mappings cannot be used by cached tokens or queued work. Existing runs retain
their original target and receipt; they are never silently retargeted.

## Transport

The deliberate address policy accepts valid HTTPS, including split DNS and
private reverse proxies, and private HTTP entered through the same pairing
form. TLS verification stays enabled. Fixed routes, connection-time DNS and
reserved-address checks, no redirects, bounded deadlines and response sizes
apply to both pairing and business delivery. No service-link private-HTTP
opt-in is required. Optional deployment destination restrictions still apply.

## Durable delivery and recovery

New Monitoring occurrences pin a versioned human presentation in the same
tenant-scoped transaction as their occurrence, raised event and Flow outbox.
The snapshot contains the tenant and client display identity, known hostname,
the original metric/condition/holds, UTC observed and raised times, and any
disk capacity facts from that exact accepted collection. A disk percentage is
already a percentage: `0.01220703125` displays as `0.01% free (99.99% used)`.
Missing byte counts stay missing. Disk incidents omit irrelevant service state;
service incidents retain it. Grouped offline incidents describe availability
and the pinned affected names/count, even if the parent rule has a disk name.

The stock mapping uses `{alertTitle}` and `{alertDetails}`. New snapshot events
also adopt this presentation when an existing published graph has the exact
historical stock title and description. This forward-only compatibility path
does not edit the published graph or its hash. Custom templates retain their
mapping semantics, including raw `{value}`. Authoring, validation, preview and
runtime also expose `{hostname}`, `{tenantName}`, `{clientUrl}`, `{alertUrl}`,
`{formattedValue}`, `{condition}`, `{recoveryCondition}`, `{raisedAt}`,
`{diskFree}` and `{diskTotal}`; the typed alert catalogue exposes corresponding
fields and exact `alert.diskFreeBytes`/`alert.diskTotalBytes`. Unavailable optional
facts resolve as empty template text or typed null. The receiver adds one
compact technical reference section instead of repeating the measured facts.

Links use the effective public Web base: deployment `ServiceIdentity:WebBaseUrl`,
then saved service public settings, then effective Branding Site URL (deployment
`Branding:SiteUrl` before the administrator value). Path prefixes are preserved.
The routes are `/clients?tenantId=...&clientId=...` and
`/monitoring?tenantId=...&alert=...`; the latter uses the immutable occurrence ID.
Existing sign-in and tenant authorization remain required. No bearer token is
added, and API/Gateway addresses are never used as fallback Web URLs. Missing,
invalid or over-budget public bases leave readable content with explicit
unavailable-link text. Individual links are bounded to 1,024 characters and the
complete escaped snapshot to 4,096 bytes; long display text is shortened without
splitting Unicode scalar values. Exact raw evidence and correlation IDs remain
in Monitoring history.

The RatelDesk payload remains plain text: its existing HTML removal/control
validation runs before JSON serialization. Slack uses literal text with markup,
mentions and link unfurl disabled; Discord escapes Markdown in the new plain
presentation and disables mentions. Rendered titles/descriptions keep the existing
receiver limits. A rename, deletion, later measurement or public-base edit does
not change a pinned presentation. Pre-upgrade occurrences keep their absent
snapshot and historical canonical envelope/suffix; already prepared delivery
bodies and wire/idempotency keys are replayed exactly. No old incident is resent
or reformatted during upgrade.

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

## Connection management

**View** shows the selected mapping. **Test connection** is an optional
read-only authenticated check of its current mapping and capabilities; it
creates no incident or task. **Delete connection** revokes local authority
and disables dependent connector use immediately, even while the peer is
unavailable. Deletion preserves existing receipts and Flow history.

Business execution keeps its tenant/resource policies, durable action identity,
receiver fingerprint and committed receipt checks. A successful pairing or
OAuth exchange alone does not establish incident readiness.
