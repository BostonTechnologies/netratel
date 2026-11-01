# Software inventory

Software Inventory collects installed package metadata on Windows and Linux. Open
the **Software Inventory** action on a `/clients` card for a reusable full-screen
client view, or use **Estate → Software Inventory** for tenant-wide asset
intelligence. Every query selects one authorized tenant.

The estate overview shows inventoried devices, distinct software products,
installed entries, never-scanned devices, active scans and failed/incomplete
attempts. **Software** groups versions by product and offers a drill-down into
matching installations. **Installations** lists individual installed entries.
**Devices** includes clients that have never completed a scan and opens the same
client dialog. **Changes** lists additions, removals and version changes.
Client labels prefer a friendly name, then the reported hostname. Agent IDs remain
available in package/scan details and API responses rather than as primary names.
Counts distinguish installed entries from distinct devices: two user scopes on
one device count as two entries and one device.

## Collection and freshness

An enrolled agent starts a full scan on its first authenticated connection after
upgrade. Successful first collection is idempotent across reconnects. Existing
agents need an inventory-capable client; an older client can retain a queued
request until it upgrades and connects. Collection does not depend on interactive
users opening the UI. Agent polling uses the API HTTPS origin and its current
enrolled-agent credential, alongside the existing presence connection.

The default tenant policy is enabled, every seven days. The **Software Inventory**
tab beside **Auto Update** in tenant settings changes the interval in hours,
days, weeks or calendar months. The minimum is one hour, the maximum is one year
(52 weeks or 12 months). Stable positive jitter adds up to 10% to the interval.
Healthy agents check for work every 5–7 seconds; transport failures back off to
20–29 seconds. Polling does not itself cause collection: the server issues work
only when due or explicitly requested. There are no client-level overrides.
Disabling scheduling still permits an explicit authorized refresh.

Opening a client dialog loads the saved snapshot independently of scan status and
refresh requests. It does not wait for a new collection before displaying saved
rows. If the last successful scan is at least 30 minutes old, a user with
`client.manage` also requests fresh inventory. **Refresh inventory** forces a
request regardless of age. Concurrent requests share the current collection ID;
the button shows the active attempt rather than issuing duplicate scans.

A first-ever inventory still requires the client to accept work, collect packages
and upload them. It is not instant cached data. The dialog distinguishes this
state from an empty filtered result, shows received-chunk progress, and updates
automatically when the complete snapshot is committed. A minute without new
chunks displays a stalled-progress explanation instead of an endless animation.
Pending means queued for the client; Collecting means it accepted work. Offline
queued requests remain durable. Complete, Partial and Error are terminal states.
Transient status failures reconnect automatically without clearing saved rows.

Agent collection has an overall two-minute cancellation deadline and individual
HTTP requests have a 30-second deadline. A collection accepted by the client has
a five-minute server deadline; the next scheduled retry is an hour later while
scheduling is enabled. Explicit **Retry scan** can start a new attempt without
waiting for that retry interval. Status reads report an elapsed deadline without
scheduling work or changing inventory; the next agent write or explicit refresh
persists the terminal state under the agent lock. Partial failures also retry
after an hour while scheduling is enabled.

The 30-minute cache indication is distinct from the scheduled cadence. A device
outside that cache window is not necessarily overdue for its weekly scan. A
recent successful receipt is not proof that a device is still online.

Only a successful full scan replaces installed entries and produces additions,
removals and version changes. Partial scans preserve the previous complete
inventory and expose safe error codes. Version changes under the same stable
package identity are recorded as Upgraded, including version downgrades. A changed
package identifier is an addition and removal. Identity includes package source,
identifier, architecture and installation scope, with a normalized name/publisher
fallback when no package identifier exists. Detection time and last successful
server receipt time are separate fields.

## Package sources

| Platform/source | Read-only collection |
| --- | --- |
| Windows registry | HKLM and HKCU uninstall keys, both registry views, plus loaded user SID hives. Only display name/version, publisher, install location/date and key identity are read. Unloaded user hives are not mounted. |
| Windows AppX/MSIX | `Get-AppxPackage -AllUsers` when Windows PowerShell and the AppX cmdlet are available. Records family identity, architecture, publisher and install location. Access failures make the scan partial. |
| dpkg / apt | `dpkg-query` installed-state records; apt uses this database. Residual config-only packages are excluded. |
| rpm / dnf / zypper | `rpm -qa`; dnf and zypper use this database. |
| pacman | `pacman -Q`. |
| snap | `snap list`. |
| flatpak | `flatpak list` for system scope and the agent service account's user scope. Other users' private stores are not impersonated. |

Missing package-manager executables are skipped. Failed queries, malformed rows,
truncation and access failures make a scan partial; they never establish removals.
The collector uses argument vectors, bounded output and deadlines. It never
invokes `Win32_Product`, installs packages or reads license keys. macOS reports
unsupported and does not submit an authoritative empty scan.

## Persistence and limits

Each collection has a server-issued ID bound to its authenticated tenant and
agent. Uploads contain gzip-compressed JSON in base64, up to 100 packages per
chunk, 1 MiB expanded per chunk and 1,000 chunks (up to 100,000 packages).
Packing respects both package count and serialized byte size, including escaped
Unicode metadata. An inventory exceeding the total chunk capacity is partial
and cannot establish removals. Duplicate chunks require identical hashes and are
acknowledged without applying changes again. Chunks can arrive out of order.
A collection becomes authoritative in one database transaction only after all
chunks arrive. A superseded collection cannot replace a newer snapshot.

The agent writes a private atomic `software-inventory/<tenant>-<agent>/pending.json`
outbox beneath its configured update state directory (or the installation's
common application data state directory). It retains the payload until matching
acknowledgements, replaying safely after a process or network interruption. Server
staging is durable across API restarts; completed payloads are discarded while
acknowledgement hashes and scan/change history remain. Protect and back up the
state directory as installation metadata.

Collection timestamps are compared at PostgreSQL microsecond precision. The
original wire payload remains unchanged for hashing, so existing full-precision
outboxes can still receive identical duplicate acknowledgements. This prevents
later chunks from being rejected solely because a timestamp was round-tripped
through a new database context. Real metadata changes and conflicting duplicate
payloads remain invalid. An already-expired attempt is retired through the normal
acknowledgement protocol; use Retry scan to request fresh work.

Server queries page 1–100 rows with a zero-based page index. Installation filters
include name, version, publisher, OS, source, client ID, last-attempt status and
30-minute freshness. The UI provides a searchable client-name/hostname picker,
not a UUID input. General search includes software and client identity metadata.
Tenant, client, name/version, publisher and source/OS indexes support queries;
prevalence uses a database aggregate. CSV streams one tenant-bound query and
quotes cells while neutralizing spreadsheet formula prefixes. **Export
installations** uses the selected filters and exports all matching installation
entries, independent of the currently displayed page or software grouping.

## API and permissions

| Permission | Operations |
| --- | --- |
| `telemetry.read` | Read tenant policy, status, snapshot, changes, installations, aggregate, coverage summary, device coverage, export and authorized tenant discovery. |
| `client.manage` | Request a scan for a client in the same tenant. |
| `tenant.admin` | Update the tenant policy. |
| Enrolled-agent identity | Claim work and submit chunks only for the tenant/agent in the authenticated credential; disabled, revoked or deleted agents are rejected. |

Inventory access, policy changes, exports and refresh requests are audited. MCP
also requires the existing delegated operator identity, OAuth scope and current
operation policy; a gateway service identity alone is insufficient.

Tenant base: `/api/v2/tenants/{tenantId}/software-inventory`.

- `GET /policy`, `PUT /policy` with `{ "enabled": true, "interval": 7, "unit": "days" }`.
- `GET /installations`, `/aggregate`, `/changes`, `/export.csv`.
- Installation/aggregate filters: `page`, `pageSize`, `search`, `name`, `version`,
  `publisher`, `os`, `source`, `agentId`, `status`, `sort`, `descending`.
- `GET /summary` returns tenant-wide coverage and software counts, independent of
  the current table filters. `stale` counts complete snapshots older than the
  30-minute interactive cache, not overdue scheduled scans.
- `GET /clients` includes never-scanned clients; supported filters are `page`,
  `pageSize`, `search` (client identity) and `agentId`.
- Pages contain `items`, `total`, `page`, `pageSize`, and distinct `devices` for
  installation/aggregate queries. Change entries include a human-readable `client`.

Client base: `/api/v2/agents/{tenantId}/{agentId}/software-inventory`.

- `GET /` returns `lastSuccessfulScanUtc`, `nextDueUtc`, `latestScan`, `policy`
  and the current user's `canRefresh` permission. Scan details include
  `startedAtUtc` when the client accepted work.
- `GET /snapshot`, `/changes`, `/scans/{collectionId}`.
- `POST /refresh` with `{ "force": true }` returns status and the queued or
  coalesced `latestScan.collectionId`. `force:false` respects the 30-minute cache.

Agent-only endpoints: `POST /api/v2/agent/software-inventory/work` and `/chunks`.
Their bodies do not accept a tenant or agent override.

## MCP and common client

HTTP and stdio expose the same closed `netratel_software` schemas: `policy`,
`set_policy`, `status`, `scan_status`, `snapshot`, `refresh`, `changes`,
`estate_changes`, `installations` and `aggregate`. Read operations never request
collection. Refresh and policy changes first return an authorized preview and
require `confirm:true` to apply. The shared `SoftwareInventoryClient` uses the
same managed API admission. Human-readable installation/change names and the
corrected ingestion behavior apply to these existing operations as well. The
new overview and device-coverage reads are Web/REST endpoints, not additional MCP
operation names.

Example MCP arguments for a cached client snapshot:

```json
{"operation":"snapshot","request":{"tenantId":7,"agentId":"11111111-1111-4111-8111-111111111111","data":{"page":0,"pageSize":50,"search":"Example"}}}
```

Explicit refresh, followed by `status` or `scan_status` for the returned ID:

```json
{"operation":"refresh","request":{"tenantId":7,"agentId":"11111111-1111-4111-8111-111111111111","data":{"force":true}},"confirm":true}
```

Refresh uses `netratel.mcp.execute`; inventory reads use
`netratel.mcp.observe`; policy updates use `netratel.mcp.admin`, alongside their
application permissions and operation policies. Query fields live inside `data`.

## Deployment and acceptance

Run the normal one-shot migrations service before updated API replicas, following
[release installation](../../release/INSTALL.md) and
[self-hosting](../deployment/SELF_HOSTING.md). Migration `AddSoftwareInventory`
adds isolated inventory tables and indexes without changing existing agent
identities. Existing installations have no software snapshot until an
inventory-capable agent connects. Missing tenant policy rows use the seven-day
enabled default. Keep the migration when rolling application binaries back;
removing it deletes inventory and change history.

The chunk-precision fix requires the updated API; it does not rewrite existing
agent outboxes or require a schema migration. The responsive workspace requires
the updated Web application. Shorter discovery cadence and collection deadlines
require the updated native agent. Merging source alone does not update a running
installation.

Focused automated coverage exercises collectors, chunk boundaries/replay,
reconciliation, cache/cadence, tenant authorization, PostgreSQL migration/query
translation/concurrent refresh, multi-chunk uploads with a fresh database context
per request, UI rendering while status/refresh requests are blocked, and shared
MCP transport schemas. Native Windows and distribution-specific package-manager
acceptance, offline process-restart delivery, and deployment smoke remain
owner-run checks. During owner acceptance, verify a first scan completes, cached
rows are visible during refresh, complete snapshots update without reopening,
client names are readable and a failed attempt preserves the previous snapshot.
