# NetRatel 0.1.1-beta.47

This release adds the missing operator policy and inventory acceptance surfaces,
provides a supported Windows recovery artifact, and consolidates the requested
dependency updates. It carries forward the previously delivered callback,
paired-authoring, SSO and updater corrections. Publication verifies distribution
integrity; the owner's live campaign supplies functional acceptance.

## Changes

- **Services refresh correlation (#399):** new clients advertise
  `client-services-refresh-v1` and return the admitted request ID with terminal
  inventory telemetry. The API persists the request, connection, registration
  epoch and deadline before dispatch. Only matching current-session telemetry
  may complete it. Requests have bounded history, coalescing, throttling and
  terminal Complete, Partial, Error, Unsupported, Offline or TimedOut outcomes.
  The Services page follows its exact request; API/MCP inventory reads expose
  the correlation status and request history. Legacy inventory remains visibly
  unattributed. A fresh correlated request does not prove an old uncorrelated
  refresh completed.
- **Bounded operator policy Web UI (#398):** authorized account administrators
  can list, preview and confirm create, replace, disable and revoke operations
  using the existing policy mutation engine. Target profiles and credential
  admission are inspectable alongside current policy state. Every confirmation
  rechecks current authority, exact payload, parent scope, target revision and
  plan expiry. Account-backed routes exclude integration credential and service
  principals. An accepted receipt remains visible when subsequent readback is
  unavailable or a later concurrent change is observed. A control-plane parent
  cannot grant development-environment-wide authority.
- **Supported Windows recovery (#312):** each Windows client archive now
  contains `updater/repair-windows-client.ps1`, generated from the canonical
  installer and verified against its release manifest. Preview validates an
  existing installation and an explicitly supplied, checksum-verified archive;
  Apply uses the existing protected cutover/rollback path. Recovery preserves
  the enrolled identity, service configuration, installed application settings,
  pending result delivery and exact native update evidence. The installer now
  recognizes the exact registered product-service SID within its existing
  bounded ACL trust rules. The tool can recover an older installed client to an
  explicitly identified earlier fixed release, leaving a genuine automatic
  successor update available for acceptance. Manual recovery itself does not
  count as automatic progression. See the [client guide](../client/CLIENT.md).
- **Dependency maintenance:** consolidate #354, #353, #332, #331 and #329;
  all five SDK container updates #328–#324; and action updates #218–#216.
  SDK and container build stages use 10.0.402. The requested package set includes
  CodeBeam.MudBlazor.Extensions 9.2.0, VeloxDev 10.0.1, YamlDotNet 18.1.0 and
  Microsoft 10.0.13 packages. Previously removed vulnerable
  `MudBlazor.Extensions` is not reintroduced by an older grouped proposal.
  GitHub actions remain pinned to the verified commits for download-artifact
  8.0.2, setup-node 7.1.0 and upload-artifact 7.0.2.

The MCP catalog revision advances for Services guidance and correlation
semantics. Reconnect the selected consumer and verify its version, source and
catalog against the deployed release. Use the completed publication record,
immutable image digests and checksummed archives together; run migrations before
starting the matching components as described in [release installation](../../release/INSTALL.md).

## Owner acceptance

The beta.46 campaign advanced 16 of its 53 retained contexts. The remaining
historical register has **37 unresolved rows: 12 P0, 23 P1 and two P2**. Its
17 exclusions remain within the original 518-context universe. Historical
outcomes and private evidence remain unchanged.

The next campaign uses explicit, linked successor specifications only where the
original fixture or exact input is permanently unavailable. It keeps the original
unresolved history and records prospective coverage separately; fresh fixture
passes never become fabricated original passes. See the current
[campaign guide](../mcp-http/campaign-retest.md).

| Issue | Required owner result after deployment |
| --- | --- |
| #399 | Fresh admitted Services request reaches its matching terminal inventory outcome; session fencing, coalescing, throttling and legacy attribution remain correct. |
| #398 | Real browser lifecycle under bounded authority, exact confirmation, current readback, audit, revocation and a separately checked campaign credential. |
| #394 | Owned paired callback rejection stops automatic retries; repaired explicit replay is idempotent; transient failure still recovers. |
| #390 | The deployed companion creates and synchronizes a request definition, then executes the paired request with the expected inputs and durable result. |
| #382 | Genuine SSO-authenticated investigation completes its first daemon turn with the initiating user's authority. Local login is a separate observation. |
| #361 | Preserve beta.46's successful new activation-path observation; verify the retained legacy repair obligation and the next approved automatic update. |
| #312 | Supported recovery preserves identity and evidence, followed by a real offer, download, activation, running binary and acknowledged terminal automatic-update result. |
| #141 | One owned monitoring occurrence produces the expected paired incident, with duplicate prevention, recovery and rearm. |
| #294 | Complete the remaining assertions and required successor coverage, real backup/decryption continuity, measured native hour and dependency-ordered cleanup. |

Source tests and the publication workflow cannot supply these live receipts.
