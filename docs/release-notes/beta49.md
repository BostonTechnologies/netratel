# NetRatel 0.1.1-beta.49

This release addresses the Services policy lifetime boundary observed in the
beta.47/beta.48 campaigns, filters unresolved systemd discovery placeholders and
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
  newly issued policy exceed that ceiling. The clock comparison in the retained
  campaign implicated this boundary; it did not capture the exact wire-level
  rejection. Normal-clock deployed acceptance remains a distinct requirement.
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

Keep one immutable NetRatel/companion candidate through each paired or
multi-window acceptance sequence. Establish the complete eligible-agent scope
before publishing an automatic update pack; a newly created test tenant alone
does not isolate a shared catalogue. Follow the
[controlled update cohort guide](../client/CLIENT.md#controlled-update-cohort),
including offline-agent exclusions, preserved administrator holds and the
before/after audit of unintended offers. A supported manual recovery install is
recorded separately from automatic offer, staging, activation and acknowledgement.

## Retained campaign accounting

The final beta.47 checkpoint records **21 passed / 32 open of 53 original
rows**, with 12 P0, 18 P1 and two P2 still open. Its 17 planned exclusions remain
inside the original 518-context universe. Beta.47 and beta.48 each retain their
historical **NO-GO** verdict. A new release does not rewrite those observations.

Keep original context IDs, assertion inputs, immutable owner identities and
predecessor evidence. Where an original fixture or exact input is permanently
unavailable, retain the unresolved historical row and its explicitly defined
prospective successor separately. Use the latest private ledger when an older
prose checkpoint or preparation report still names 37 open rows. Continue
independently ready work rather than restarting already evidenced contexts.

## Owner acceptance by issue

Source delivery and publication do not close the following live requirements.
Retain qualified earlier receipts at their actual source and scope, then complete
the missing assertions through the
[campaign guide](../mcp-http/campaign-retest.md).

| Issue | Delivered behavior and remaining deployed evidence |
| --- | --- |
| [#399](https://github.com/BostonTechnologies/netratel/issues/399) | Correlated Services refresh now has issuance headroom. After a normal-clock hosted-client reboot, require two separately admitted selected-native requests, each Complete with its exact matching collection. Preserve clock, connection and rejection diagnostics; unrelated background inventory does not count. |
| [#398](https://github.com/BostonTechnologies/netratel/issues/398) | The bounded policy lifecycle now has searchable, filtered pages and an inspection/edit dialog. Verify later-page selection, temporary exact-directory grant and removal, actual credential admission/operation, authoritative readback/audit, nonadministrator denial, stale review and self-escalation boundaries. |
| [#394](https://github.com/BostonTechnologies/netratel/issues/394) | Durable terminal callback rejection and explicit replay remain delivered. Retain the latest credited event results and verify their full receiver evidence: no automatic resend over reconciliation intervals, audited repaired replay, idempotent repeats and transient recovery. |
| [#390](https://github.com/BostonTechnologies/netratel/issues/390) | Paired request-definition creation and input synchronization remain delivered. Complete companion create/sync/replay/readback/binding/execution/callback and the required read-only, foreign-target, duplicate-input and stale/revoked-mapping denials. |
| [#382](https://github.com/BostonTechnologies/netratel/issues/382) | Time-bounded SSO authority and useful permission errors remain delivered. Require a genuine authorized SSO principal's first completed investigation turn through the intended authenticated daemon; Local login and connection/admission probes are separate observations. |
| [#361](https://github.com/BostonTechnologies/netratel/issues/361) | Keep the accepted Linux activation-path result. Exercise the remaining supported repair of an already-installed legacy helper, retaining identity and native attempts, then prove its next eligible unattended update and acknowledged executable hash. |
| [#312](https://github.com/BostonTechnologies/netratel/issues/312) | Supported Windows recovery remains delivered. Retain qualified LocalSystem recovery/progression receipts and complete the controlled automatic successor chain without replacing missing historical pending-result evidence with a clean fixture. |
| [#294](https://github.com/BostonTechnologies/netratel/issues/294) | Reconcile the 32 exact original open rows and separate successors, complete current product/security gates, preserve restore/decryption continuity, run the full declared 240-slot/3,600-second hour, then finish dependency-ordered cleanup and authority restoration. |

The paired receiver lifecycle under
[#141](https://github.com/BostonTechnologies/netratel/issues/141) also needs one
complete two-window sequence on the same server candidates: distinct occurrence,
flow and incident identities; post-commit lost-response reconciliation;
duplicate-webhook idempotency; acknowledgement, recovery and rearm; and owned
fixture cleanup. Retain prior partial sequences at their original boundaries.

The [beta.48 job-history repair](beta48.md) is retained. Complete product-level
new-start denial after retirement and foreign-tenant history denial while the
same terminal run/steps/logs remain readable to their authorized owner. A
consumer-side delegation denial does not establish a product API response.

Required PR/main validation, the generic all-target release build and completed
publication receipts establish source/build/distribution status. The owner
campaign supplies the normal-clock, browser, installed-client, paired, SSO and
measured-hour results; these notes do not claim those executions have passed.
