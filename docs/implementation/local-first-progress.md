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
| P00 | [#30](https://github.com/BostonTechnologies/netratel/issues/30) | [#43](https://github.com/BostonTechnologies/netratel/pull/43) | `35d23b2` | Merged after the hosted Public PR validation run passed all component-image, native package, disclosure and OIDC smoke gates. |
| P01 | [#31](https://github.com/BostonTechnologies/netratel/issues/31) | [#44](https://github.com/BostonTechnologies/netratel/pull/44) | `fcd202d` | Merged after hosted exact-head validation passed, including source and release-image generic OIDC Compose smoke coverage: [run 35494345483](https://github.com/BostonTechnologies/netratel/actions/runs/35494345483). |
| P02 | [#32](https://github.com/BostonTechnologies/netratel/issues/32) | pending | pending | In progress on `feat/issue-32-local-identity`: PostgreSQL-backed local Identity model, stable principal links, protected local-account endpoints, MFA/recovery primitives, and session revalidation. |
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

## P01 bootstrap runtime

- API startup now reconciles an API-owned descriptor before registering the
  operational graph. `Unconfigured`, `Configuring`, and `RecoveryRequired`
  start only the anonymous liveness/readiness/setup-status surface; they do
  not register PostgreSQL infrastructure, OIDC handlers, agent admission,
  hosted workers, outbox work, or Akka authority.
- The descriptor, journal, generated one-time proof, and key-material proof
  are held in a private bootstrap directory. The descriptor retains only
  hashes and configuration references. An absent descriptor with remaining
  state, absent key proof, or an integrity mismatch enters recovery instead of
  inventing a new installation.
- A proof claim has a durable operation ID and bounded lease, is rate limited
  per remote address, performs browser-origin checks when an Origin is sent,
  and consumes the proof. Generated proofs expire and are renewed only on a
  later startup; deployment-managed proofs must be replaced by the deployment
  owner before restart. P01 deliberately stops at `Configuring`: initial
  local-user creation and provider migration remain P02/P04/P05 work.
- A configured PostgreSQL store is adopted only when NetRatel-specific
  continuity evidence exists (tenant plus durable agent, signing, outbox, job
  or request evidence) and usable OIDC configuration is present. Empty schema
  is not legacy evidence; unavailable configured storage and incomplete legacy
  evidence enter recovery. A complete deployment-owned OIDC configuration can
  explicitly retain the prior operational startup path for compatibility, but
  this is not legacy-data adoption and creates no inferred application data.
- Compose now permits missing OIDC values during bootstrap. In that state the
  Web host uses a status-only setup shell that proxies the API setup status;
  configured OIDC deployments retain the existing full Web path. The shell
  cannot create an account or invoke business APIs.

## P02 native identity foundation

- A separate, versioned Identity context shares the selected PostgreSQL store
  without changing existing tenant, agent, OIDC-signing, or native Client data.
  Local users receive durable NetRatel principal IDs. Verified external users
  resolve by issuer plus subject only; email is never an account-link key.
- Authentication mode is explicit when configured. `Auto` (including an absent
  setting) preserves an already configured OIDC deployment as OIDC-only and
  selects local mode only when OIDC is absent; `Hybrid` must be selected
  deliberately. Local accounts receive no broad legacy Operator authority until
  the P03 effective-access migration is complete.
- The API local-account surface uses framework password hashing, lockout,
  time-limited MFA challenge state, authenticator/recovery-code support, and
  a protected cookie revalidated against enabled state, security stamp, and
  authorization revision. P05 will add the browser forms and first-owner flow;
  no anonymous self-registration or reset route is exposed.

## Commands and validation

| Commit | Command | Result |
| --- | --- | --- |
| `7ef8680` | remote tag/release, issue/PR and branch-protection inventory | Completed 2026-09-20; `v0.1.0-rc.1` is the only remote tag; release inventory is recorded above. |
| `7ef8680` | `dotnet restore NetRatel.sln` | Passed. Restoring was required because prior ignored local test assets were stale and selected VSTest despite the repository's Microsoft.Testing.Platform setting. |
| `7ef8680` | `dotnet build NetRatel.sln --configuration Release --no-restore` | Passed: 0 errors, 42 pre-existing warnings. |
| `7ef8680` | `dotnet test NetRatel.sln --configuration Release --no-build --filter 'category!=compose' -- --report-trx --report-trx-filename 'netratel-p00-{asm}_{tfm}_{arch}.trx'` | Passed: 1,860 succeeded, 4 explicit live-environment skips, 0 failed. TRX is retained only as local CI-style evidence. |
| `feat/issue-31-bootstrap` | `dotnet build NetRatel.sln --configuration Release --no-restore` | Passed: 0 warnings, 0 errors. |
| `feat/issue-31-bootstrap` | `dotnet test src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj --no-restore --filter 'FullyQualifiedName~Bootstrap'` | Passed: bootstrap state, replay/restart, recovery, empty-schema, configured-storage failure, and representative PostgreSQL/OIDC legacy-adoption coverage. |
| `feat/issue-31-bootstrap` | fresh API process with no usable database/OIDC configuration | Passed: `/health/live` returned 200, `/health/ready` 503, setup status 200, business route 404; a 64-byte proof claimed once with 202 and replay returned 400. |
| `feat/issue-31-bootstrap` | fresh API + Web process with no OIDC configuration | Passed: the Web root returned the status-only setup shell and its same-origin setup-status proxy returned API state 200. |
| `feat/issue-31-bootstrap` | `docker compose --env-file .env.example config --quiet` | Passed with blank OIDC and agent-signing inputs, proving the fresh setup Compose configuration resolves without placeholder identity credentials. |

## Current checkpoint

Current phase: P01. Branch: `feat/issue-31-bootstrap`. PR:
[#44](https://github.com/BostonTechnologies/netratel/pull/44). Next action:
merge only after hosted exact-head validation gates pass.

## Blockers

None currently identified. Publication permissions and package ownership will
be verified only in P12 against the selected candidate; no release action has
been attempted.
