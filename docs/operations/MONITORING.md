# Monitoring

Monitoring evaluates authenticated CPU, disk and explicitly selected service evidence. A rule can additionally alert when its selected clients go offline. Rules, static groups, alert occurrences, operator commands and suppression belong to one application tenant. Memory remains available as telemetry; it is not a phase-one rule kind.

A series is identified by tenant, rule, agent and stable resource key. Disk series use normalized volume paths. Windows service names compare without case; systemd unit names compare exactly. Static groups select clients and do not create a second alarm identity. Overlapping client/group membership deduplicates. An all-client rule also covers future eligible registrations and requires `monitoring.targets.all` authority.

## Evidence and episodes

The existing telemetry stream carries metrics and Services chunks through one sequence cursor, one outbound writer and shared acknowledgements. The 16 KiB gRPC limit stays unchanged. Services frames must remain below 12 KiB, with bounded inventory assembly.

After existing authentication, presence, connection, epoch and sequence admission, the gateway awaits monitoring persistence before acknowledging an input or publishing its compatibility display update. The server's telemetry registration ID becomes the evidence stream ID; it is never accepted from the client. Replacing a telemetry stream within the same presence connection starts a new evidence boundary even when its sequence remains increasing. Persistence failure closes the stream; it cannot acknowledge a missing monitoring transaction and continue an old hold window.

CPU values are percentages. Disk free-space thresholds require explicit units and are evaluated in bytes. Complete Monitoring disk evidence requires paired exact total/free byte fields and valid collection identity, time and Complete quality, together with current authenticated evidence. Legacy rounded GiB telemetry remains compatible for transport and display; missing collection proof or exact bytes leaves Monitoring Unknown. Partial, stale or ambiguous evidence and a missing selected volume are Unknown rather than zero free space.

Service monitoring accepts complete, current-watch-revision evidence for the exact selected service. Missing can breach only when the collector supplies authoritative missing evidence. Partial, unsupported, stale, invalid or out-of-order evidence cannot imply stopped, zero usage or recovery. Inventory discovery and automatic start mode do not select watches.

Fresh accepted breaches move Healthy → Pending → Firing. The full breach hold requires continuing qualifying observations. Recovery uses its own threshold/hysteresis and fresh hold, moving Firing → Recovering → Resolved. Timers cannot complete a breach or recovery without a new observation. Unknown interrupts pending/recovery continuity and retains an existing unresolved occurrence. Reconnect or restart does not manufacture an occurrence or uninterrupted time over a gap.

An acknowledgement marks the same occurrence and does not rearm it. Manual clear requires a reason, ends that occurrence and fences the cleared evidence; a new occurrence needs new observations and a new full hold. Disabling a rule or removing a target suspends its applicability rather than claiming recovery. Semantic condition/target/hold/freshness edits require an explicit reset policy and evaluation revision. Rename/severity/action edits do not replay or replace a pinned occurrence.

Suppression is an audited tenant/rule/client/group/resource overlay with a reason and optional expiry. Evaluation continues. Expiry may release at most one currently fresh, previously undelivered active occurrence; it cannot replay historical resolved episodes or an already delivered occurrence.

## Disk inventory and resource policy

Updated Linux clients read `/proc/self/mountinfo` in their own mount namespace. Capacity inventory carries optional filesystem, source/device, mount-root, read-only and stable backing-capacity metadata. Known read-only SquashFS/EROFS package images and appropriate pseudo filesystems are excluded from capacity alerts by default, with an explicit reason. Real ext4/XFS data volumes becoming read-only remain eligible, as do writable network/backup volumes and independent ZFS datasets. Immutable image roots remain visible as excluded inventory; a container's meaningful overlay `/` remains eligible.

Deduplication requires authoritative backing evidence. Views with the same device, filesystem, source and filesystem root share one deterministic representative. Overlay views require an existing upper directory and the most specific containing mount in that namespace. Neither equal byte counters, a shared pool name nor a mount ID proves duplicate capacity. Subdirectory binds with different filesystem roots remain eligible when independent quota behavior cannot be ruled out from mountinfo. Missing metadata is unclassified and never proves exclusion. If a filesystem policy cannot be decided without that metadata, evidence remains Unknown and preview says Unconfirmed; a complete new client collection is required.

The rule editor's **Disk resource policy** accepts include/exclude mount/path and filesystem patterns. The same optional `condition.diskPolicy` object is used by saved rules, runtime and API/MCP target preview:

```json
{
  "includeMounts": ["/", "/data/*", "/backup"],
  "excludeMounts": ["/data/cache/*"],
  "includeFileSystems": [],
  "excludeFileSystems": ["squashfs"]
}
```

Patterns match the complete string: `*` matches zero or more characters, including separators, and `?` matches one character. Other characters are literal; there is no regular expression, shell expansion or bracket grammar. At most 32 patterns across the four lists, each up to 256 characters, are accepted. The editor trims each nonempty line. Linux paths use ordinal case-sensitive matching; Windows paths and filesystem names use ordinal case-insensitive matching. Explicit excludes win. If either include list is populated, a match in either is required, and an explicit include can override default exclusions. An existing exact normalized volume must also match. An include cannot turn unsupported or partial telemetry into complete evidence.

**Preview cached disk resources** shows per-resource eligibility/reason, filesystem, collection time, freshness, unclassified older telemetry and inventory truncation. It reads the authorized target clients' existing cache and never collects telemetry. Preview uses the selected freshness budget, defaults to two minutes when omitted through API/MCP, returns at most 16 resources per client and 160 total, and reports truncation. Re-run after editing the policy. The client prioritizes eligible resources before its 64-scope cap and the complete 16,383-byte envelope limit; at most 16 excluded resources are retained. Truncation and absent resources never imply recovery.

Existing clients and rules keep their enrollment and disk resource identities. Normal client upgrade is required to report mount classification; old clients remain readable and are labelled unclassified. Absent/empty policy uses the supported defaults without changing the historical evaluation fingerprint. An explicit policy edit changes the evaluation revision and requires **SuspendOccurrenceAndRequireNewWindow**, the usual reason and reset confirmation. Newly proven default exclusions suspend existing occurrences as **NotApplicable** with a `disk-policy:` reason and a non-recovery target-removal disposition. Partial, missing, changed-in-place, reordered or stale collection metadata cannot clear an unresolved breach. Historical events and prepared incident bytes stay pinned.

## Alert if Offline

In `/monitoring`, open a rule's side panel and enable **Alert if Offline**. The same direct-client, static-group or all-eligible selection applies to this additional condition. It is off by default for existing rules. Set **Offline grace (seconds)** between 1 and 3,600; the default is 60 seconds. This grace is the offline breach hold. Recovery uses the rule's existing recovery hold.

The server checks committed connection ownership and authentication expiry in bounded passes starting on a five-second interval. Large tenant sets resume from a continuation cursor; a pass has a 20-second budget. It can detect an outage without new telemetry or an open browser. Inactive, revoked, deleted, superseded and pending-offboarding clients are excluded. Grace and recovery require successive current observations; a worker restart or an excessive observation gap cannot manufacture continuity.

Each rule has one offline occurrence for its complete deduplicated target scope. If ten clients in a site group go offline, the rule raises one occurrence and enqueues its selected flow once. The affected membership continues to update as more clients disconnect or return. The occurrence resolves after the remaining offline clients recover and the recovery hold completes. A later outage can then raise a new occurrence. Distinct rules remain independent: selecting the same client in two rules can produce one occurrence for each rule.

RatelDesk, Discord and Slack receive the same bounded outage summary. Up to ten client names are listed when the list fits within 512 characters. Larger counts or longer lists use count-only text such as **18 clients are offline**. Full current membership remains available to the monitoring UI and dashboard counts. A client bypass excludes that client from outbound names and counts while retaining its offline state in the dashboard. If every affected client is bypassed, the occurrence is suppressed. Expiry can release one still-current, undelivered occurrence after the next presence check.

The aggregate series uses the reserved `clients:offline` resource and an empty agent ID; it does not pretend that a disconnected client has a live telemetry owner. Its dispatch path rechecks current rule applicability, membership, suppression, occurrence and authority. Existing CPU, disk and service series keep their per-client identity. Enabling this option without selecting a published flow still raises the alert in the UX; choosing a flow adds the configured external actions. Acknowledgement, manual clear and rule changes retain the normal episode semantics described above.

## Persistence and display

A sequential per-client actor evaluates bounded state. PostgreSQL atomically fences configuration/state/evidence revisions and commits state, occurrence transition, immutable event, delivery intent and audit before any fanout. Unique series/open-occurrence/event/action identities and fenced delivery leases reject stale or concurrent writers. This is a single-node actor runtime; database concurrency protection does not claim multi-node actor high availability.

Monitoring events have durable tenant-scoped reads and an atomic display mirror in existing notification storage. The reserved `NetRatel.Monitoring.` namespace is excluded from every legacy global notification query, mutation, retry processor and stream. Notification read/deletion is separate from alert acknowledgement or resolution.

Rules without a validated immutable published flow remain display-only. The fallback published-flow provider returns an empty list and rejects selection. A real automation provider must validate exact flow authority; future occurrences pin the selected version and server-attributed configuring principal/credential. Existing occurrences retain their pinned definition. A local delivery key does not guarantee exactly-once external side effects.

A handed-off intent retains its real persisted flow run ID. Once the intent exceeds 15 minutes without a verified terminal outcome, the next claim records `DeliveryUnknown` with that same ID and removes it from dispatch claims. Bounded receipt polling continues to look up the existing run and can reconcile a verified terminal outcome, including after manual clear, disconnection or a newer occurrence. This lookup cannot enqueue another run. An interrupted handoff without a recorded run ID also remains explicit Unknown and uses exact event/version lookup to recover an already persisted outcome.

## Authorization and API

The stable permissions are `monitoring.read`, `monitoring.manage`, `monitoring.ack`, `monitoring.clear`, `monitoring.bypass` and `monitoring.targets.all`. Each operation authorizes its exact tenant/resource pair before disclosure or mutation. New dedicated MonitoringReader, MonitoringManager and MonitoringResponder role definitions do not widen existing persisted roles during upgrade. Operator identity comes from the verified stable NetRatel principal, and audited writes require a reason.

`GET /api/v2/monitoring/tenants` exposes only approved monitoring tenants. Tenant operations use `/api/v2/tenants/{tenantId}/monitoring`:

- Read `permissions`, `configuration`, `summary`, paged `series`/`events`, `clients` and `published-flows`.
- Preview exact deduplicated targets with `POST targets/preview`. Bounded detail includes honest cached support/unknown notes, total count and an explicit truncation flag. Preview does not collect from clients.
- Save/delete `rules/{ruleId}`, `groups/{groupId}` or `bypasses/{bypassId}` with an expected configuration revision and reason.
- Read `agents/{agentId}/series` or acknowledge/clear through `POST agents/{agentId}/rules/{ruleId}/ack` and `/clear`. The resource key is a query parameter so volume/service names are not path segments.

Shared HTTP DTOs live in `Shared/Contracts/Monitoring/MonitoringApiContracts.cs`. A durable save returns its saved revision even if service-watch reconciliation is pending; `WatchPolicyUpdatePending` indicates that the background renewal must finish, rather than asking the operator to repeat the save.

## Service policies and bounds

The watch source unions enabled, tenant-owned resolved service rules for the client. It rejects a selection above 64 names or the encoded policy budget instead of silently selecting a subset. Removing/disabling the last service rule clears the selection. Policy selection revisions are persisted under bounded striped coordination; renewal can retain the selection revision while extending expiry. Configuration changes reconcile currently connected registrations, and bounded background renewal operates independently of viewers. Every command rechecks the captured server registration and uses the existing outbound writer.

A configuration save attempts at most 64 registrations within five seconds; unfinished activation is reported by `WatchPolicyUpdatePending`. Renewal runs every 30 seconds through keyset batches of 128 active Services registrations, with eight concurrent attempts, one second per client and a 15-second batch deadline. The continuation cursor advances through every registration, including failed attempts, instead of repeatedly favoring the first clients. Eligibility checks use an indexed point query during renewal. Selection policies expire after one hour if renewal cannot finish.

API validation budgets freshness for the default cadence and jitter: at least 10 seconds for CPU, 65 seconds for disks and 35 seconds for 30-second service watches. Slower configured collectors can still become Unknown; freshness is never inferred over a gap.

Monitoring request bodies, configuration aggregates and response pages are capped at 4 MiB. Paged durable reads use continuation cursors when their byte budget fills; full oversized responses and writes fail visibly rather than truncate. Other principal limits include 256 rules and 128 groups per tenant, 4,096 eligible target clients, 1,024 series per client, 16,384 series per tenant, 200 rows per read, 50 outbox claims, eight delivery attempts and 90-day history retention. Capacity limits can leave evidence Unknown and must never fabricate recovery.

Acceptance includes TimeProvider-based evaluator tests, actual gRPC acknowledgement/failure barriers, exact tenant/resource API negatives, legacy notification disclosure/mutation guards and real PostgreSQL rollback/concurrency/restart/lease tests. CI does not enroll or manage live OS clients.

## Expanded alert evidence

Open **Alert details** in the leading column of an alert row for a full-width view of its target and
rule, observed/received evidence, automation outcome, and incident/acknowledgement.
**Flow run and receipt** and **Published flow** open the tenant-qualified records.
**Open RatelDesk ticket** appears only for a validated incident receipt containing
a safe ticket URL. A missing link is shown as missing rather than reconstructed
from an internal identifier. Flow success remains distinct from confirmed
incident evidence; unconfirmed and absent results are labelled explicitly.

## Full-result queries and continuation

The series and History endpoints accept one AND-combined query: `view`, `clientId`,
`severity`, `text`, `fromUtc`, `toExclusiveUtc`, `sort` and `maximumCount`.
Filters run in PostgreSQL over the authorized tenant before ordering and paging.
Omitting filters preserves the API's existing series view, including Healthy records.
The Web page requests its selected Active/Firing view explicitly. Tenant summary
badges remain tenant totals; `matchingCount` is the count for the complete selected
query, independently of the returned page and its continuation.

Series `view` supports All, Active, Firing, Pending, Unknown, Acknowledged,
Suppressed and Offline. Firing is an occurrence lifecycle: Unknown/stale evidence
cannot remove an unresolved Firing occurrence from that selection. Unknown uses
both observed and received times, rejects future times and applies the current
rule's freshness budget (one minute for offline summaries). A client selection
also matches an offline aggregate containing that client. Severity uses the pinned
occurrence rule when one exists, otherwise the current rule. Text searches current
friendly computer/directory names, pinned identity, rule and resource text.

For series, date bounds apply to occurrence **Raised date**; rows without a raised
time do not match an active date bound. History uses immutable **Event/audit time**.
The lower bound is inclusive and `toExclusiveUtc` is exclusive. Convert a selected
inclusive end date to the following day's start in the displayed operator timezone
before converting to UTC; this preserves daylight-saving day boundaries. History
retains the ordered union of alert events and non-series configuration/bypass
audits. Client or severity filters exclude audits because those dimensions do
not apply. Other History filters run before union paging. New offline events pin
the exact affected member IDs at that event; older summaries lacking these IDs
cannot match an exact client filter. Historical names and memberships are never
reconstructed from current inventory.

Series order is rule UUID, client UUID, resource key; History order is event/audit
time descending, then immutable item UUID descending. Return `nextCursor` unchanged
with the same tenant, endpoint, filters, sort and page size. Encode it once as a
URL query value; do not decode/rebuild its content. A byte budget can return fewer
rows than requested while still returning a continuation. The next cursor always
identifies the last row actually returned. An invalid continuation is an explicit
400 error, distinct from a successful empty page. `monitoring_cursor_context_mismatch`
means restart the changed query; `monitoring_cursor_restart_required` means restart
pagination after upgrading from a previous cursor version.

The v1 opaque base64url cursor is bounded to **1,468 characters** across API,
managed-operation admission, CLI and both MCP transports. The maximum binary
series token is 1,100 bytes: 4-byte magic, version/kind (2), tenant (4), normalized
query SHA256 (32), rule/client UUIDs (32), resource length (2) and all 512 UTF16
code units (1,024). Raw UTF16 preserves escaped/Unicode resource keys without JSON
escaping expansion; base64's rounded-up bound is 1,468. History tokens are smaller
and use the same bound. Tokens bind version, endpoint kind, tenant and normalized
query including selected page size. Pre-upgrade pagination must restart; no old
record or immutable event is rewritten.

The existing 4 MiB response bound and 200-row API maximum remain. A page reserves
512 KiB for client identity metadata. When a large offline membership exhausts that
metadata budget, `clientIdentitiesTruncated` is true; the alert, exact member IDs
and continuation remain intact, with missing directory details shown honestly.
Use the authorized client directory search for additional identity details.

## Finding alerts and paging

Monitoring opens on **Firing** with **20 rows per page**. State, computer, severity,
text and date filters combine across the full authorized result before paging;
a healthy first page cannot hide later firing alerts. **All states** includes
active occurrences, pending windows and current Unknown evidence. Firing lifecycle
and current evidence quality are separate: a firing occurrence with stale evidence
still appears under Firing. The badges are explicitly **tenant totals**.

The compact **Filters** disclosure searches the tenant's computer directory by
friendly name or hostname, including computers outside the current results page.
Selections use stable computer IDs and retain an explicit unavailable-metadata
fallback for deleted or inaccessible names. Search covers computer, rule and
resource text. Active chips show the selection and **Clear filters** retains the
authorized tenant and selected page size while clearing client, search, severity,
dates and an unrelated selected alert.

Active date bounds use the occurrence's **Raised date**, in the displayed operator
timezone. Rows without a raised timestamp are excluded while date bounds are set.
**To** includes the selected local calendar day; the query sends the following
local day's start as its exclusive UTC bound, including daylight-saving changes.
History uses **event / audit time**. A computer or severity selection excludes
configuration/audit rows that do not have that dimension. Invalid date order is
shown as a filter error without issuing a new query.

The results body scrolls within a measured viewport. Column headings and the actual
pager remain available on desktop and narrow screens; primary columns can scroll
horizontally. The first-column disclosure supports keyboard activation and reports
its expanded state. A collapsed alert creates one main row; an expanded alert adds
one full-width grey detail surface. Paging/filtering clears expansion, refresh
retains only the same tenant/series/occurrence identity, and a replacement occurrence
does not inherit an old expansion. Exact occurrence links retrieve the persisted
occurrence independently of the visible page, including closed alerts.

**20, 50 and 100** are real server page sizes. **First / Previous / Next** use bounded
opaque cursor history; there is no invented last-page or random-access total.
Displayed ranges count alert/history items, including a short page caused by the
response-byte budget, and do not count expanded detail rows. Tenant/query/page-size
changes reset pagination. A failed read is an explicit error and keeps the last
successful results labelled as such. Filter, page-size, view and timezone context
are preserved in the URL for refresh and browser back/forward navigation.
