# Services inventory and refresh

Linux inventory unions installed `.service` unit files with loaded service units,
including stopped/unloaded services and transient services. Bare template
definitions (`worker@.service`) are excluded: they are definitions, not runtime
services. Concrete instances (`worker@blue.service`) remain in inventory.
Loaded-unit discovery also excludes unresolved `not-found` dependency
placeholders. An unresolved dependency is not an installed or loadable service;
it must not by itself turn otherwise complete inventory into a partial result.
`systemctl` runs with argument vectors, C locale, full unit names and bounded
output under the existing collection deadline. Valid property blocks survive
another unit's failed query; unresolved units remain Unknown. A truncated trailing
block is discarded. No per-unit retry loop is added. Aliases retain their original
inventory names. Load, active, substate and enablement remain separate.

Collection results pass through the existing serializer and capability-negotiated
gRPC telemetry channel to the Services actor. Serialization distinguishes capacity
limits from invalid and duplicate observations. The actor assembles ordered chunks
under the existing epoch/connection/sequence checks before saving a snapshot.

Only final Complete inventory replaces `LastCompleteInventory`. Final Partial
inventory with rows is retained separately as `LastPartialInventory`, with its
collection identity, observation/receipt times, status and safe reason. A later
failure or interrupted assembly preserves both caches. A new Complete clears the
partial fallback. Additive optional JSON fields require no database migration;
whole-record and SSE bounds allow two bounded inventories. Policy-only and
timeout-only writes cannot change partial physical evidence without the committed
connection owner. Partial inventory never supplies authoritative Missing evidence
to monitoring; selected-watch evidence retains its existing rules.

An open dialog renders complete cached inventory first, or final partial rows when
no complete inventory exists. It requests one fresh inventory through the existing
authorized refresh API on opening an online supported client. The client coalesces
with an in-flight inventory; the backend throttles recently admitted requests.
Search, filters and rerenders do not request collection. Live updates use the
existing SSE projection stream. “Live” describes that update connection; inventory
age uses the client's observation time, not API receipt or browser refresh time.

Updated clients negotiate `client-services-refresh-v1` alongside the existing
services capability. Each admitted refresh is saved before dispatch with its
request ID, tenant/client, presence connection/epoch, telemetry registration and
70-second deadline. Its request ID is carried on every chunk of the selected
inventory. A request can join an in-flight inventory; that collection explicitly
echoes the joined ID. Requests arriving after collection completes wait for the
next collection within the existing rate limit. A watch, an unrelated collection,
a different registration or an expired request cannot complete it. Reconnect
clears native pending IDs.

While the telemetry publisher waits for a metrics or inventory acknowledgement,
incoming Services policies share one bounded pending slot. An ordinary renewal
updates that slot's policy without erasing its selected refresh request ID.
After acknowledgement credit returns, the publisher applies the pending policy
and retains that request for the corresponding inventory. The slot belongs to
the current presence session; reconnect does not carry a request into another
connection.

The existing authorized inventory API and native `netratel_services/inventory`
readback include `supportsRefreshCorrelation` and `refreshRequests`. Each request
has a terminal `outcome`, optional `collectionId`, completion time and fixed error
code. `GET /api/v2/agents/{tenantId}/{agentId}/services/refresh/{requestId}` reads one
exact request under the same target and `telemetry.read` checks. The projection
retains the latest 128 requests per client across process restarts; export the
readback into the operational record when longer retention is needed. A missing ID
returns 404 and must not be replaced by the latest collection.

Complete, Partial, Error and Unsupported terminal collections are bound only to
the exact admitted request on its original registration. Assembly failures retain
the request's error outcome. An authoritative offline presence or replaced registration is recorded as
Offline on the next authorized read; elapsed deadlines are durably recorded as
TimedOut on the next projection access. Neither result claims collection success.
Throttled calls return the already tracked request ID without scheduling another
collection. A pending request is coalesced until its terminal outcome or deadline;
after completion, the existing 15-second admission spacing still applies.

Older clients remain compatible. Their refresh response has
`correlationSupported: false`; a subsequent terminal collection is recorded as
Unattributed, never Complete for that request. Their viewer keeps the explicit
**subsequent inventory outcome** wording and cursor-based wait. On upgraded
clients the viewer waits for the exact persisted request outcome and displays its
ID. Both paths retain the bounded 70-second display deadline. Closing or changing
client or tenant cancels requests/subscriptions and fences late responses.

The API issues watched and empty-fallback Services policies with a 59-minute
lifetime. This leaves one minute below the client's unchanged one-hour maximum
for bounded clock lag and transport time. It does not relax the client's policy
validation or require the operator to advance the guest clock.

Rejected converted policies emit a fixed reason category with the normalized
refresh GUID, policy revision, expiry, client UTC, connection ID and epoch.
Reasons distinguish expiry, excessive lifetime, stale revision and a conflicting
or replayed policy. Invalid wire conversion emits only its fixed rejection
category. These diagnostics omit service names, tokens and raw policy payloads;
they help identify the rejected stage without changing admission rules.

## Verify a refresh

Use the request ID returned by the refresh call to read its terminal outcome.
A completed refresh has `outcome: Complete`, a nonempty `collectionId`, and a
collection whose `refreshRequestId` matches under the same tenant, client and
registration. A throttled or coalesced response can return an existing request
ID; background inventory does not complete a different request.

For a failed refresh, inspect the returned error code and bounded policy
rejection diagnostics alongside the client's connection and normal time-service
status. Keep the clock synchronized; changing it does not repair a policy or
inventory problem.

The dialog owns the tenant and client captured at opening. Replacing directory
rows, changing heartbeat data, reconnecting the inventory stream, or disposing the
launcher does not close it. Explicit Close, Escape, and navigation dismiss the
dialog. The directory also keeps its rows mounted while a background refresh is
in progress.

## Start, stop and restart

The API and native Windows/systemd clients support typed service actions through
the existing Akka command gateway. Native command sessions must advertise
`service-control-v1`; older or unsupported clients remain read-only. The API
returns `SupportsServiceControl` and the current caller's `CanControlServices`
with the inventory. Inventory and refresh require `telemetry.read`. Changing a
service or reading an operation requires `client.manage` for its tenant and an
existing authorized client. MCP and CLI apply their credential policy before
using these same handlers.

Read the inventory first and use its exact `Name` and `Platform`, never the display
name. Submit `POST /api/v2/agents/{tenantId}/{agentId}/services/control` with a new
`operationId`, `serviceName`, `platform` (`1` Windows or `2` Linux systemd), `action`
(`Start`, `Stop`, or `Restart`), and an optional reason. Poll
`GET /api/v2/agents/{tenantId}/{agentId}/services/operations/{operationId}` for its
result. The UI confirms the named target before admission and shows the operation
ID while tracking it. Directory refreshes and inventory events preserve the
selected search and action state.

The server saves intent and audit before dispatch, validates that the exact
service appears in this client's inventory, and serializes active service
operations per client. Native execution rechecks the identity, rejects wildcard
systemd selections, checks its queue deadline, and reports a fresh native status
after the action. Windows uses SCM without stopping dependent services;
systemd uses a fixed executable and a separate argument vector. Neither path
executes operator-supplied shell text. The NetRatel client service is protected;
client offboarding is the removal path for that service.

`Completed` means the client observed the expected resulting state. An accepted or
started operation is still pending. `OutcomeUnknown` and `MayHaveExecuted` retain
uncertainty after an interrupted execution or response. Reusing the same operation
ID only reads the original operation; it never repeats a restart. Refresh the
inventory before deciding whether a new operation is needed. All outcomes remain
associated with the original tenant, client, service, actor, reason, and operation
ID in persisted state and audit.

Deploy the updated API and Web, and upgrade the Linux client binary to obtain the
collector fix. Upgrade either Windows or Linux clients to obtain refresh correlation. Older clients
continue to work with the additive backend fields and unattributed outcomes. Release publication is separate from the source merge.

Owner acceptance: open Services, confirm actual rows and the observation timestamp,
then request Refresh after the opening request finishes. Leave the dialog open and
confirm it reaches a completed outcome and updates the displayed observation when
a Complete collection arrives. On the affected deployment, also record distro,
systemd version, running client executable version, collector exit/status/reason
and row counts. These deployment observations have not been verified by fixture
tests.
