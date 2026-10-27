# NetRatel 0.1.1-beta.45

This beta addresses the remaining reproducible callback, updater, log continuity,
file paging and owned-object cleanup defects. It also includes the paired
request-definition authoring and input synchronization changes merged after the
previous release.

- **Durable callbacks (#394):** a terminal peer rejection is persisted for its
  delivery phase and stops automatic retries. HTTP 408, 425, 429, server and
  transport failures remain retryable. Explicit audited replay after repair
  shares the delivery lease, records the resulting acknowledgement and is
  idempotent. Rejecting a running notification does not suppress a subsequent
  terminal notification.
- **Linux update activation (#361):** request noninteractive, nonblocking systemd
  activation and observe its bounded enqueue acknowledgement. A definite
  rejection permits another offer only after confirming the service has no
  active or queued work and no matching native handover marker. Uncertain
  handovers remain protected from duplicate activation.
- **Windows update results (#312):** align runtime protected-state validation
  with the updater's exact TrustedInstaller and registered product-service SID
  trust rules. Invalid result files produce a bounded diagnostic. Preserve
  historical results until supported delivery/reconciliation acknowledges them;
  do not delete protected state to unblock an update.
- **Log continuity:** real Linux file truncation emits a gap even when the
  shortened file is empty. MCP tail retains empty gap notifications and session
  gap state when initial history fills the record limit. Cumulative dropped
  counts are no longer repeatedly added.
- **File browse continuation (#308):** API, typed client, CLI and MCP support
  protected five-minute continuation cursors. Every page rechecks current
  authority and the complete bounded native listing. Changed policy or listing
  returns a stale-cursor conflict; expiry, tampering and changed caller/request
  binding are explicit failures. POSIX names differing only by case remain
  distinct. The existing 4,096-entry native bound remains enforced. See
  [file commands](../integrations/CLI_AND_MCP.md).
- **Owned cleanup (#304):** authorized tenant administrators can retire
  MCP-owned jobs through the existing job API after their originating credential
  is revoked. Retirement and run admission share a database fence; active runs
  prevent retirement. Active job lists and scheduling exclude retired jobs,
  while run history, original ownership, audit and idempotent deletion receipts
  remain. Retired references no longer prevent the associated script's supported
  deletion.
- **Paired authoring (#390):** include the scoped request-definition create and
  input-sync routes from #392, including current mapping/grant/revision checks
  and idempotent results. The empty-job completion correction from #393 is also
  included.

Run the supplied migrations before starting API/Web. This release includes
request-input metadata and nullable durable callback rejection fields. API,
Web, migrations, MCP and native assets must use the same published source and
verified artifact set. The MCP catalog changes for cursor support; reconnect
consumers and verify their advertised catalog after deployment.
