# Services inventory and refresh

Linux inventory unions installed `.service` unit files with loaded service units,
including stopped/unloaded services and transient services. Bare template
definitions (`worker@.service`) are excluded: they are definitions, not runtime
services. Concrete instances (`worker@blue.service`) remain in inventory.
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

Existing clients do not echo the refresh request ID in telemetry. The viewer
therefore reports a **subsequent inventory outcome**, without claiming that a
particular request caused it. Only newer inventory cursors and terminal outcomes
end the wait: intermediate chunks and selected watches do not. Complete, Partial,
Error, Unsupported and offline outcomes are explicit. A 70-second deadline
(admission spacing + collection + assembly + delivery allowance), checked by the
existing display timer, also ends an unfulfilled wait. Closing or changing client
or tenant cancels requests/subscriptions and fences late responses.

Deploy the updated API and Web, and upgrade the Linux client binary to obtain the
collector fix. Existing Windows clients continue to work with the additive backend
fields. Release publication is separate from the source merge.

Owner acceptance: open Services, confirm actual rows and the observation timestamp,
then request Refresh after the opening request finishes. Leave the dialog open and
confirm it reaches a completed outcome and updates the displayed observation when
a Complete collection arrives. On the affected deployment, also record distro,
systemd version, running client executable version, collector exit/status/reason
and row counts. These deployment observations have not been verified by fixture
tests.
