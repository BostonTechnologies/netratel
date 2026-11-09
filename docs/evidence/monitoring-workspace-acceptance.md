# Monitoring workspace acceptance

Synthetic, disposable fixtures only. This checklist covers issues
[#673](https://github.com/BostonTechnologies/netratel/issues/673),
[#674](https://github.com/BostonTechnologies/netratel/issues/674),
[#680](https://github.com/BostonTechnologies/netratel/issues/680),
[#683](https://github.com/BostonTechnologies/netratel/issues/683) and
[#687](https://github.com/BostonTechnologies/netratel/issues/687).

- [x] **Authoring:** actual HTTP MCP → shipped HTTP adapter → AgentClient → managed
  API admission/handler saves CPU, all-disk and exact-path disk rules and reads
  them back. The shipped stdio adapter preserves identical typed failures.
  Empty/invented CPU names and empty/wildcard disk names are rejected without
  revision changes. Preview uses the correct safe field path. Both catalogs
  publish explicit-null schemas, examples and guidance; unknown content stays redacted.
- [x] **Circuit:** real Blazor interop exercises both controllers, repeated render,
  Flows → Monitoring → Flows navigation and an injected cleanup JS exception.
  The destination remains interactive. A focused handle test proves that a
  failed observer cleanup/release cannot skip remaining module releases.
- [x] **Management:** complete 107-item fixtures, 20/50/100 pagination, matching
  totals, search beyond page one, independent section state, deterministic
  duplicate names and page clamping after removal. Existing editor/revision,
  conflict, unsaved-input and tenant-fencing regressions remain in place.
  Browser checks cover muted tabs, sticky headers, keyboard navigation,
  deep-link reload, light/dark themes and a narrow viewport representing 200% zoom.
- [x] **Client tools:** six categories, exact device identity, aggregate exclusion,
  deduplicated read-only readiness, existing dialog launch parameters and explicit
  shell/Explorer choices. Real top-layer menus remain inside viewport bounds on
  first/last rows of a 100-row page, including touch/keyboard and scroll dismissal.
- [x] **Prepared investigation:** one local durable investigation, authorized
  alert/device association, server-derived unsent context and a stable first
  message ID. Browser fixtures prove new-tab opening, a device absent from the
  first search page, retained composer edits, zero sends before Start, one first
  send, popup-blocked fallback and lost-create-response reconciliation. PostgreSQL
  verifies persisted context, duplicate Start rejection, cross-tenant/tampered IDs
  and MonitoringRead denial with or without an occurrence association.

Owning checks: `MonitoringApiTests`, `ManagedOperationsMcpTests`,
`MonitoringPageTests`, `MonitoringManagementTests`, `BrowserInteropCleanupTests`,
`NetclawPersistencePostgresTests` and optional `MonitoringWorkspaceAcceptanceTests`.
Browser tests remain outside required PR/main CI. Required hosted checks and their
nonzero receipts remain authoritative for merge and release source identity.

No production monitoring configuration, incident, agent or deployment was changed;
no live Netclaw message was sent. Build/distribution integrity and owner functional
acceptance are separate. Owner deployment/native-client acceptance remains pending.
