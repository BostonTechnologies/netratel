# Local-first implementation progress

Umbrella: [#29](https://github.com/BostonTechnologies/netratel/issues/29)

Integration branch: `main`

Baseline: `7ef86801c80b6cdf3c495ce7ff0a4fff41fa8ac4` (`chore(deps): modernize September 2026 dependency stack`)

This ledger records durable evidence only. Raw logs, credentials, screenshots
and disposable environments remain outside the repository.

## Accepted programme scope

The programme adds local-first setup, native accounts, scoped RBAC, SQLite,
guided setup, purpose-separated integration credentials, CLI/stdio/HTTP MCP
support, deployment branding, correct first paint, API documentation and a
verified prerelease. It preserves established PostgreSQL/OIDC installs, native
Client credentials and workflows, the current dependency baseline, approved
brand defaults, and receipt-backed release provenance.

It excludes production/fleet changes, private configuration or consumer-lock
changes, dependency rollbacks, destructive migration rewrites, tag movement,
asset replacement, package-visibility mutation, and repository-rule bypass.

## Phase ledger

| Phase | Issue | PR | Merge SHA | Evidence / current state |
| --- | --- | --- | --- | --- |
| P00 | [#30](https://github.com/BostonTechnologies/netratel/issues/30) | [#43](https://github.com/BostonTechnologies/netratel/pull/43) | pending | Inventory and ADRs on `docs/issue-30-local-first-inventory`; local baseline is green; hosted exact-head CI is pending. |
| P01 | [#31](https://github.com/BostonTechnologies/netratel/issues/31) | pending | pending | Blocked by P00 merge. |
| P02 | [#32](https://github.com/BostonTechnologies/netratel/issues/32) | pending | pending | Blocked by P01 merge. |
| P03 | [#33](https://github.com/BostonTechnologies/netratel/issues/33) | pending | pending | Blocked by P02 merge. |
| P04 | [#34](https://github.com/BostonTechnologies/netratel/issues/34) | pending | pending | Blocked by P03 merge. |
| P05 | [#35](https://github.com/BostonTechnologies/netratel/issues/35) | pending | pending | Blocked by P04 merge. |
| P06 | [#36](https://github.com/BostonTechnologies/netratel/issues/36) | pending | pending | Blocked by P03/P04/P05 merges. |
| P07 | [#37](https://github.com/BostonTechnologies/netratel/issues/37) | pending | pending | Blocked by P06 merge. |
| P08 | [#38](https://github.com/BostonTechnologies/netratel/issues/38) | pending | pending | Blocked by P06/P07 merges. |
| P09 | [#39](https://github.com/BostonTechnologies/netratel/issues/39) | pending | pending | Blocked by P03/P05 merges. |
| P10 | [#40](https://github.com/BostonTechnologies/netratel/issues/40) | pending | pending | Blocked by P09 merge. |
| P11 | [#41](https://github.com/BostonTechnologies/netratel/issues/41) | pending | pending | Blocked by P07/P08/P10 merges. |
| P12 | [#42](https://github.com/BostonTechnologies/netratel/issues/42) | pending | pending | Remains open through verified publication. |

## P00 inventory

### Runtime and persistence

- .NET SDK is pinned to `10.0.401`; root product version is `0.1.0-rc.2` in
  `Directory.Build.props` and `release/release-manifest.json`.
- `AddNetRatelInfrastructure` currently requires
  `ConnectionStrings:NetRatelDb` or `Default` and unconditionally calls
  `UseNpgsql`. `OrchestratorDbContext` owns the current EF model/migrations;
  it contains PostgreSQL extension, index, `jsonb`, filter and SQL-default
  assumptions. `NetRatel.Migrations` is the explicit migration component.
- API persists a Data Protection key ring (default `.keys`, application name
  `NetRatel-Keyring`) and separately loads agent signing material. Existing
  OIDC signing records, agent credentials/refresh tokens, tenant data,
  outbox, command/job, MCP policy and audit state are in the application
  database and are upgrade-critical.
- API currently starts storage initialization, hosted seed/retention/catalog,
  outbox and search services plus configured Akka authority before it can
  distinguish a setup-needed instance. P01 owns safe runtime gating.

### Identity, transport and presentation

- Browser/API authentication currently supports provider-neutral OIDC (with
  Azure compatibility aliases), optional machine tokens, M2M, system tokens
  and native agent tokens. The default/fallback operational policy is an OIDC
  administrator role/group assertion; there is no persisted local user model.
- Web is a browser-session client of the API, while the API remains the
  authorization and enrollment authority. Native Clients use separate
  enrollment, credential, refresh and signing paths. CLI/stdio use configured
  OIDC M2M credentials; HTTP MCP has its own resource/delegation boundary.
- `App.razor` currently loads theme preference JavaScript after body content;
  P10 owns first-paint correction. The approved NetRatel brand pack is already
  merged; P09 adds controlled effective overrides without changing identities.

### Release and CI baseline

- `v0.1.0-rc.1` is the only remote release tag. `rc.2` is an untagged source
  candidate and must be re-inventoried in P12 before selecting the final RC.
- Public PR validation builds/tests, creates final component images and runs
  source/release-image generic OIDC smoke. The non-publishing release workflow
  produces archives, SBOMs, checksums and attestations. Promotion requires an
  immutable receipt, preflight, journal, exact digest tests and explicit
  prerelease publication; it must remain so.
- Baseline hosted evidence: [PR #28](https://github.com/BostonTechnologies/netratel/pull/28)
  merged after all listed Public PR validation checks passed. P00 local command
  results are added below when complete; historical counts are not reused as
  current evidence.

## Architecture decisions and test ownership

| Concern | Decision | Implementation phase | Primary evidence |
| --- | --- | --- | --- |
| Bootstrap / legacy adoption | [ADR 0001](../adr/0001-local-first-bootstrap-runtime.md) | P01 | concurrent/replay/restart/adoption real-store tests |
| Stable principal / access | [ADR 0002](../adr/0002-stable-principals-and-scoped-permissions.md) | P02/P03 | local/OIDC and two-tenant direct/stream tests |
| Provider / migrations | [ADR 0003](../adr/0003-provider-selection-and-migration-ownership.md) | P04 | SQLite/PostgreSQL migration, restart and restore tests |
| HTTP MCP delegation | [ADR 0004](../adr/0004-local-http-mcp-delegation.md) | P06–P08 | joined gateway/API and concurrent-scope tests |
| Branding / configuration | [ADR 0005](../adr/0005-effective-branding-precedence.md) | P09/P10 | options/UI and before-runtime computed-colour tests |
| Protected routes | [endpoint inventory](local-first-endpoint-permissions.md) | P03 | unmapped-route gate plus target-scope matrix |

## Commands and validation

| Commit | Command | Result |
| --- | --- | --- |
| `7ef8680` | remote tag/release, issue/PR and branch-protection inventory | Completed 2026-09-20; `v0.1.0-rc.1` is the only remote tag; release inventory is recorded above. |
| `7ef8680` | `dotnet restore NetRatel.sln` | Passed. Restoring was required because prior ignored local test assets were stale and selected VSTest despite the repository's Microsoft.Testing.Platform setting. |
| `7ef8680` | `dotnet build NetRatel.sln --configuration Release --no-restore` | Passed: 0 errors, 42 pre-existing warnings. |
| `7ef8680` | `dotnet test NetRatel.sln --configuration Release --no-build --filter 'category!=compose' -- --report-trx --report-trx-filename 'netratel-p00-{asm}_{tfm}_{arch}.trx'` | Passed: 1,860 succeeded, 4 explicit live-environment skips, 0 failed. TRX is retained only as local CI-style evidence. |

## Current checkpoint

Current phase: P00. Branch: `docs/issue-30-local-first-inventory`. PR:
[#43](https://github.com/BostonTechnologies/netratel/pull/43). Next action:
wait for required checks on the current head, merge through the protected path,
then update this ledger and begin P01 from refreshed `main`.

## Blockers

None currently identified. Publication permissions and package ownership will
be verified only in P12 against the selected candidate; no release action has
been attempted.
