# NetRatel 0.1.1-beta.49

This release addresses the Services policy lifetime boundary observed during
beta.47/beta.48 testing, filters unresolved systemd discovery placeholders and
makes operator policies searchable and accessible through an inspection/edit
dialog. It integrates the requested dependency updates and retains the earlier
callback, paired-authoring, SSO, updater and job-history repairs. Distribution
verification and deployed functional acceptance have separate outcomes.

## Changes

- **Services policy clock tolerance:**
  [PR #402](https://github.com/BostonTechnologies/netratel/pull/402) reserves one
  minute of issuance headroom for both watched and empty-fallback policies.
  Policies expire after 59 minutes, below the client's unchanged one-hour
  ceiling. A slightly lagging client clock no longer makes an otherwise valid
  newly issued policy exceed that ceiling. The retained clock comparison
  implicated this boundary; it did not capture the exact wire-level rejection.
- **Selected refresh delivery during acknowledgement waits:** the telemetry
  publisher keeps one bounded pending Services-policy slot. An ordinary renewal
  received while metrics or inventory acknowledgement credit is pending keeps
  the selected refresh request ID instead of replacing it with an uncorrelated
  renewal. A real gRPC regression covers this ordering and verifies the later
  inventory carries the selected ID. Presence-session fencing remains intact.
- **Bounded policy rejection diagnostics:** converted-policy rejection reports
  a fixed reason, normalized request GUID, revision, expiry, client UTC and
  connection ID/epoch. Expired, excessive-lifetime, stale and conflicting/replayed
  policies remain rejected. Malformed wire policies report only a fixed category;
  service names, tokens and raw payloads are not logged.
- **Linux Services discovery:**
  [PR #403](https://github.com/BostonTechnologies/netratel/pull/403) excludes
  unresolved `not-found` dependency placeholders from loaded-unit discovery.
  Installed and loadable services remain in inventory, including concrete
  template instances. Real discovery/property failures retain their partial
  outcome rather than being hidden to force a Complete result.
- **Operator policy workspace:**
  [#398](https://github.com/BostonTechnologies/netratel/issues/398) gains
  case-insensitive search, lifecycle and target filters, and pagination with
  10/25/50/100 rows. **Inspect** opens a centered, scrollable dialog immediately,
  keeping the selected policy's details, edit form, exact preview, confirmation,
  current readback and audit together. Closing the dialog preserves the list
  state and the accepted receipt. The existing account authority, bounded
  parent policy, self-escalation, version, target-profile, confirmation and
  idempotency checks remain in force.
- **Dependency maintenance:** integrate the updates requested by
  [#401](https://github.com/BostonTechnologies/netratel/pull/401),
  [#404](https://github.com/BostonTechnologies/netratel/pull/404),
  [#405](https://github.com/BostonTechnologies/netratel/pull/405),
  [#406](https://github.com/BostonTechnologies/netratel/pull/406),
  [#407](https://github.com/BostonTechnologies/netratel/pull/407),
  [#408](https://github.com/BostonTechnologies/netratel/pull/408) and
  [#409](https://github.com/BostonTechnologies/netratel/pull/409). The pinned SDK
  and all five Docker build stages use 10.0.403. Overlapping package proposals
  are consolidated, with related Microsoft package references aligned at
  10.0.14 so older direct references do not downgrade the updated dependency
  graph.

## Deployment and controlled updating

Deploy the completed publication's matching API, Web, HTTP MCP and migration
images using its immutable digests and checksum-verified bundles. Run migrations
before starting those components as described in
[release installation](../../release/INSTALL.md). Upgrade the selected native
clients from the matching published archives and retain their actual executable
hashes and enrolled identities.

Establish the complete eligible-agent scope
before publishing an automatic update pack; a newly created test tenant alone
does not isolate a shared catalogue. Follow the
[controlled update cohort guide](../client/CLIENT.md#controlled-update-cohort),
including offline-agent exclusions, preserved administrator holds and the
before/after audit of unintended offers. A supported manual recovery install is
recorded separately from automatic offer, staging, activation and acknowledgement.
