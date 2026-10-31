# Software inventory

Software Inventory collects installed package metadata on Windows and Linux. Open
the **Software Inventory** action on a `/clients` card for a full-screen client
view, or use **Estate → Software Inventory** for tenant-wide installations,
prevalence, CSV export and change history. Every query selects one authorized
tenant. Counts distinguish installed entries from distinct devices; two user
scopes on one device count as two entries and one device.

## Collection and freshness

An enrolled agent starts a full scan on its first authenticated connection after
upgrade. Successful first collection is idempotent across reconnects. Existing
agents need the updated client; an old client can retain a queued request until
it upgrades and connects. Collection does not depend on interactive users opening
the UI. Agent polling uses the API HTTPS origin and its current enrolled-agent
credential, alongside the existing presence connection.

The default tenant policy is enabled, every seven days. The **Software Inventory**
tab beside **Auto Update** in tenant settings changes the interval in hours,
days, weeks or calendar months. The minimum is one hour, the maximum is one year
(52 weeks or 12 months). Stable positive jitter adds up to 10% to the interval.
Agents receive the current policy on each work poll, normally every 20–29 seconds.
There are no client-level overrides. Disabling scheduling still permits an
explicit authorized refresh.

Opening a client dialog reads the cached snapshot. If its last successful scan is
at least 30 minutes old, a user with `client.manage` also requests fresh inventory.
The **Refresh inventory** button forces a request regardless of age. Concurrent
requests share the current collection ID. The UI polls its asynchronous status;
Pending means queued for the client, Collecting means the client has accepted
work, and Complete, Partial or Error are terminal. Offline requests remain durable.
A collection accepted by the client expires after 24 hours without completion;
the next scheduled retry is an hour later. Partial failures also retry after an
hour while scheduling is enabled.

Only a successful full scan replaces installed entries and produces additions,
removals and version changes. Partial scans preserve the previous complete
inventory and expose safe error codes. Version changes under the same stable
package identity are recorded as Upgraded, including version downgrades. A changed
package identifier is an addition and removal. Identity includes package source,
identifier, architecture and installation scope, with a normalized name/publisher
fallback when no package identifier exists. Detection time and last successful
server receipt time are separate fields. A recent receipt is not proof a device
is still online.

## Package sources

| Platform/source | Read-only collection |
| --- | --- |
| Windows registry | HKLM and HKCU uninstall keys, both registry views, plus loaded user SID hives. Only display name/version, publisher, install location/date and key identity are read. Unloaded user hives are not mounted. |
| Windows AppX/MSIX | `Get-AppxPackage -AllUsers` when Windows PowerShell is available. Records family identity, architecture, publisher and install location. Access failures make the scan partial. |
| dpkg / apt | `dpkg-query` installed-state records; apt uses this database. Residual config-only packages are excluded. |
| rpm / dnf / zypper | `rpm -qa`; dnf and zypper use this database. |
| pacman | `pacman -Q`. |
| snap | `snap list`. |
| flatpak | `flatpak list` for system scope and the agent service account's user scope. Other users' private stores are not impersonated. |

Missing package-manager executables are skipped. Failed queries, malformed rows,
truncation and access failures make a scan partial; they never establish removals.
The collector uses argument vectors, bounded output and two-minute command
deadlines. It never invokes `Win32_Product`, installs packages or reads license
keys. macOS reports unsupported and does not submit an authoritative empty scan.

## Persistence and limits

Each collection has a server-issued ID bound to its authenticated tenant and
agent. Uploads contain gzip-compressed JSON in base64, up to 100 packages per
chunk, 1 MiB expanded per chunk and 1,000 chunks (100,000 packages). Duplicate
chunks require identical hashes and are acknowledged without applying changes
again. Chunks can arrive out of order. A collection becomes authoritative in one
database transaction only after all chunks arrive. A superseded collection cannot
replace a newer snapshot.

The agent writes a private atomic `software-inventory/<tenant>-<agent>/pending.json`
outbox beneath its configured update state directory (or the installation's
common application data state directory). It retains the payload until matching
acknowledgements, replaying safely after a process or network interruption. Server
staging is durable across API restarts; completed payloads are discarded while
acknowledgement hashes and scan/change history remain. Protect and back up the
state directory as installation metadata.

Server queries page 1–100 rows with a zero-based page index. Filters include name,
version, publisher, OS, source, client UUID, last-attempt status and 30-minute
freshness. Tenant, client, name/version, publisher and source/OS indexes support
these queries; prevalence uses a database aggregate. CSV streams one tenant-bound
query and quotes cells while neutralizing spreadsheet formula prefixes. Export
uses the selected filters and exports all matching entries, independent of the
current displayed page.

## API and permissions

| Permission | Operations |
| --- | --- |
| `telemetry.read` | Read tenant policy, status, snapshot, changes, installations, aggregate, export and authorized tenant discovery. |
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
- Pages contain `items`, `total`, `page`, `pageSize`, and distinct `devices` for
  installation/aggregate queries.

Client base: `/api/v2/agents/{tenantId}/{agentId}/software-inventory`.

- `GET /` returns `lastSuccessfulScanUtc`, `nextDueUtc`, `latestScan`, `policy`
  and the current user's `canRefresh` permission.
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
same managed API admission.

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
identities. Existing installations have no software snapshot until the updated
agent connects. Missing tenant policy rows use the seven-day enabled default.
Keep the migration when rolling application binaries back; removing it deletes
inventory and change history.

Focused automated coverage exercises collectors, chunk boundaries/replay,
reconciliation, cache/cadence, tenant authorization, PostgreSQL migration/query
translation/concurrent refresh, UI cache/refresh behavior and shared MCP transport
schemas. Native Windows and distribution-specific package-manager acceptance,
offline process-restart delivery, and deployment smoke remain owner-run checks.
Validate these against the published client artifacts and inspect logs before
recording functional release acceptance.
