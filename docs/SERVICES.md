# Client Services

The Services action in the client card and table opens a full-screen, read-only
inventory. It replaces the manual Ping shortcut; automatic heartbeat latency
and the diagnostic Ping API remain available. Opening or rendering client cards
does not request a collection.

Windows clients query Service Control Manager for stable service names, display
names, state and start mode. Linux clients combine installed systemd service
units with loaded units, including stopped, unloaded and transient services.
Systemd load, active, substate and unit-file enablement remain separate: enabled
does not mean running, and an active/exited oneshot is not a failed service.
Other platforms report Unsupported. Collectors do not retrieve command lines,
environment variables or credentials, and expose no service-control action.

## Inventory and selected watches

The last complete inventory is stored in PostgreSQL and remains readable while
a client is offline or after an API restart. A separate latest-attempt record
shows partial, failed or unsupported collections without replacing that cache.
Each row identifies its observation age and whether its evidence came from the
inventory or a selected watch. A failed or incomplete query means Unknown;
Missing requires a successful authoritative lookup.

Search and All, Running, Stopped, Failed and Monitored filters operate on the
selected client's cached view. The Refresh button requests one bounded
collection and reports Requested, Offline, Unsupported or Throttled. A request
acknowledgment is not a completed inventory. The viewer keeps existing evidence
visible while waiting for a complete result.

Watches are exact service names selected by the server. The Services slice's
default policy selects no names; the monitoring rule source supplies selections
in the monitoring slice. Stopped or automatically started services are not
automatically monitored. Selected names without evidence remain Unknown.

The client collects inventory on reconnect, every 15 minutes by default, and
after an admitted explicit refresh. Selected watches run every 30 seconds by
default with jitter. Policies expire, collection is cancellable, and only one
OS collection runs at a time. Refresh requests have a minimum 15-second interval.

## Transport and persistence

Services use the existing authenticated V2 telemetry stream. Both sides must
accept `client-services-v1`; an upgraded client sends no Services payload to an
older server. Metrics and Services share one writer, sequence and acknowledgment
credit. The existing 16 KiB gRPC limit remains unchanged: each actual encoded
Services envelope is smaller than 12 KiB, and the complete inventory is bounded
to 1 MiB, 128 chunks and 2,048 entries. A policy selects at most 64 names.

An ordered, bounded chunk assembly replaces inventory only after its final
complete chunk. Interrupted assemblies, invalid metadata, stale sessions and
out-of-order messages cannot overwrite a complete cache. PostgreSQL revisions
fence competing writes. Connection epochs are allocated atomically in
PostgreSQL so API restarts cannot reuse an old session number.

The actor region is single-node, with one bounded, passivating projection actor
per tenant/client. Every persistence operation owns a scoped DbContext. These
durable fences do not provide multi-node actor availability.

## HTTP surface

The following routes require `TelemetryRead` permission for the exact tenant and
registered, nondeleted agent before accessing its cached data:

- `GET /api/v2/agents/{tenantId}/{agentId}/services`
- `POST /api/v2/agents/{tenantId}/{agentId}/services/refresh`
- `GET /api/v2/agents/{tenantId}/{agentId}/services/watch-policy`
- `GET /api/v2/agents/{tenantId}/{agentId}/services/events`

The bounded event stream reads the selected client's projection every two
seconds, sends changed durable revisions or connectivity, and emits keepalives.
It performs no OS collection. Closing the dialog or changing its owning client
cancels requests and the subscription; late responses cannot replace another
client's view.
