# NetRatel 0.1.1-beta.46

This release repairs connection diagnosis, event discovery and campaign
preparation problems identified while reviewing the beta.45 acceptance result.
The beta.45 report advanced four original log-gap/resynchronization assertions;
53 original contexts remain pending live execution, with 17 retained exclusions
inside the original 518-context universe.

- **Authenticated Netclaw diagnosis:** the existing connection test now reports
  bounded causes for DNS, refused/unreachable connections, TLS, authentication,
  endpoint/protocol, timeout and unavailable protected device-token failures.
  API responses and the saved Web connection view provide corresponding repair
  guidance without returning raw exception messages or credential material. Testing
  still uses the saved tenant pairing and a real authenticated daemon session;
  current revisions and cancellation remain enforced. A transport disposal
  failure can no longer leave a successful result paired with a failure code.
  The setup guide now uses supported daemon lifecycle commands and requires
  isolated tenant daemon contexts for native SignalR sessions, which share
  Netclaw's Personal tool audience within a daemon.
- **Owned event discovery:** `netratel_events/list` accepts bounded exact
  `eventType`, `correlationId` and notification `status` filters. Callback
  rejection/failure notifications can be `Published`; that status describes the
  notification outbox, not callback success. Redacted summary metadata identifies
  the replay path and disabled-state rejection. Retry preview rejects disabled
  events before issuing a plan. Notification pages use an identity tie-breaker
  for equal occurrence timestamps. Outbox claims and completion writes respect
  current notification state and lease ownership, so an accepted disable cannot
  be overwritten by a stale publishing worker. An already admitted publication
  can finish; disabling a notification does not cancel its originating workflow
  or the separate durable callback reconciler. Existing admission, confirmation,
  callback leases and replay behavior remain authoritative.
- **Campaign preparation:** the existing offline checker accepts the latest
  remaining-context ledger, checks its original IDs/counts/exclusions and pins
  its hash. It reports prerequisites per remaining context, separating original
  QA durable reads from runtime recovery and the platform-specific Windows/Linux
  checks. Supported direct-owner decisions no longer require a distinct
  reviewer. Historical ledgers and actual live acceptance remain separate.

The MCP catalog revision changes because event-list filters were added. Reconnect
the selected consumer and verify the release/catalog identity after deployment.
The product version remains controlled only by `Directory.Build.props`; use the
published archives, immutable images and completed publication record together.
Run the supplied migration step before API/Web startup as in the canonical
[release installation guide](../../release/INSTALL.md).

The [completion guide](../mcp-http/campaign-retest.md) records the 53 remaining
assertions and their execution order. It retains the genuine authenticated AI,
original QA recovery, designated reader, Windows, paired-application, keyring,
240-call/3,600-second endurance and cleanup obligations. Source tests and release
publication do not supply the missing live receipts.
