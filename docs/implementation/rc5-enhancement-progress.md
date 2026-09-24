# rc.5 PostgreSQL and account UX enhancement progress

This ledger tracks [the enhancement](https://github.com/BostonTechnologies/netratel/issues/107)
on one branch and PR. It records verified results, including incomplete checks, without
changing the historical rc.5 release record.

## Baseline (E00)

| Item | Captured state |
| --- | --- |
| Base | `origin/main` at `cd02b3623b0daa0b34dc049d09263c185aa7c27c`, `v0.1.0-rc.5` preparation commit. |
| Branch | `enhancement/postgresql-account-ux-onboarding` from that base. |
| Toolchain | `global.json` pins .NET SDK `10.0.401`; installed SDK matches. |
| Web UI | MudBlazor `9.10.0`, CodeBeam.MudBlazor.Extensions `9.1.0`, MudBlazor.Extensions `9.10.0`. |
| PR checks | `Public disclosure gate`, `.NET build and tests`, `CLI and stdio MCP review artifacts`, native Client package matrix, and additional image/Compose jobs in `.github/workflows/public-pr-validation.yml`; check identities must stay stable. |
| Credential request | Current Web sends one `Grant(tenantId, permission)`, optional one instance permission, UTC end of selected expiry date, and an always-visible resource field to `/api/v2/account/integration-credentials/`. |
| Security page | Password, authenticator setup, and disablement forms render together; no current MFA status read. |
| Shell | App-bar and drawer both render branding; section captions are left-aligned; login panel is right-aligned by current CSS. |
| Deployment | Base image recipe has bundled PostgreSQL and localhost origins. External PostgreSQL overlay exists. Fresh named `web-keys` volume ownership is tracked by #106. |
| Documentation | First-run guide starts with SQLite; README does not expose the API-container setup-proof command near its top. |

## Work and evidence

| Requirement | Issue | Implementation and tests | Validated SHA / result | Unresolved item / next action |
| --- | --- | --- | --- | --- |
| E00 inventory and traceability | #107, #61 | This ledger; source, CI, issue discussions; PR #108 | `003da15`, PR #108 opened | Record final exact-head hosted checks before review. |
| E01 PostgreSQL-only runtime and package | #107 | Provider retirement, migration/test ports, `check-postgresql-only.sh` | `cce277b`; 107 focused PostgreSQL tests, the retirement contract, 1,774 Release project tests, and published rc.5 PostgreSQL/local and OIDC upgrades passed | Hosted package and upgrade gates pending. |
| E02 first run and deployment | #107, #60, #106 | Local/OIDC selection and recovery, operator commands, root-owned volume init, public HTTPS overlay | `5dfc436`; extracted bundled and external PostgreSQL browser journeys passed; operator commands, restart, partial/full reset, and real public HTTPS ingress passed locally | Hosted exact-head deployment matrix pending. |
| E03 account security UX | #107, #64 | Protected status endpoint; state-driven MudBlazor cards and dialogs; local SVG QR; browser and API tests; role-assigned recovery and account security throttling | `89a0023` passed hosted browser gates; follow-up component tests passed 188/188, PostgreSQL regressions and source Compose browser passed | Hosted final exact-head gates pending. #64 retains separate external-provider acceptance. |
| E04 integration UX/grants | #107 | Authority catalog; multi-tenant/multi-grant wizard; URL validation; owner attenuation; PostgreSQL tests; field-specific creation errors and connection guidance | `89a0023` passed hosted CLI/MCP and browser gates; follow-up error/guidance review pending | Revalidate final head. |
| E05 login and shell | #107 | Centered login; drawer-only branding and section rules; labels; Playwright assertions | `6e745ea`; reviewed synthetic desktop/mobile captures; extracted bundle browser journeys passed | Hosted screenshot/browser gate pending. |
| E06 documentation and issue audit | #107, #22, #60, #61, #62, #64, #71 | README/hosting/first-run/INSTALL rewrites; rc.3 ledger; #22 closure; #71 checkpoint | #22 closed after merged #23/#26; #71 updated with exact rc.5 publication evidence; 18 release-validator tests and extracted bundle profiles passed | PR #108 carries the final hosted package check result. |
| E07 final acceptance and PR | #107 | PR #108 and release/CI gates | `89a0023`; hosted run 35908712130 passed all 22 jobs, including bundled/external/public-HTTPS browser, native Client, CLI/MCP, and rc.5 upgrades; follow-up Release build passed with 0 errors, 1,993 CI-filtered tests passed with 4 live-environment skips, and fresh source Compose browser passed | New exact-head hosted run pending. |

Historical release notes, migrations, and issue comments remain evidence of their original
state. This ledger does not treat an open issue as complete based only on a release number.

## Reproduced installation failures

| Reproduction | Cause and repair | Regression owner |
| --- | --- | --- |
| A managed runner pre-creates the shared key volume as root, then the first local sign-in fails. | The non-root API could not write its Data Protection key. Both source and release Compose now run a bounded root-owned volume-initialization helper that assigns the persistent directories to UID/GID 1654 before the API and Web start. | Extracted release browser sign-in after deliberately pre-creating root-owned volumes; #106. |
| Fresh PostgreSQL with explicit Local mode and a retained example OIDC authority skipped ownership setup. | Bootstrap selected the incidental OIDC value. Authentication mode now governs bootstrap and Web selection consistently; incomplete active OIDC settings fail with a useful configuration error. | Bootstrap mode tests and the Local/stale-authority public HTTPS journey. |
| An empty replacement database with a retained Ready descriptor risked presenting a new owner flow. | The descriptor and database no longer described the same installation. This enters Recovery, while a deliberately complete disposable reset creates fresh setup material. Recovery also clears administrator lockout, fences old sessions, and revokes owned integration credentials in one PostgreSQL transaction. | PostgreSQL recovery regression and the extracted bundle partial/full reset acceptance. |
| Public HTTPS behind a proxy required public origin, trusted forwarding, allowed host, and secure-cookie settings together. | The loopback recipe could not serve as a public deployment example. The separate HTTPS overlay wires those settings, an ingress, persistent keys, and a separately configured public MCP resource. | Disposable TLS proxy journey checks allowed and forged Origin/Host, sign-in, and restart. |

The owner's earlier failed deployment has no attached error log. These are reproduced
failure paths and fixes, not a claim that one of them uniquely explains that incident.

## Synthetic visual evidence

The captures use a disposable PostgreSQL installation and synthetic accounts.
They contain no setup code, one-time integration secret, authenticator secret, or
recovery code.

| Journey | Capture |
| --- | --- |
| New owner setup | [Mobile setup](assets/rc5-enhancement/setup-mobile.png) |
| Centered local sign-in | [Desktop login](assets/rc5-enhancement/login-desktop.png), [mobile login](assets/rc5-enhancement/login-mobile.png), [200% zoom at narrow height](assets/rc5-enhancement/login-zoom-200-narrow.png) |
| Drawer branding and section rules | [Desktop drawer](assets/rc5-enhancement/drawer-desktop.png) |
| Local account security | [Mobile security](assets/rc5-enhancement/security-mobile.png) |
| Granular integration access | [Mobile integration](assets/rc5-enhancement/integration-access-mobile.png) |

## Historical issue disposition

| Issue | Disposition |
| --- | --- |
| #22 | Closed after verifying the merged rc.2 correctness and branding work. |
| #60 | First-run docs and supported operator commands are delivered here; close when PR #108 merges. |
| #62 | F01–F08 and V01–V03 are classified against merged evidence in the rc.3 ledger; close when PR #108 merges. |
| #106 | Root-owned key-volume reproduction and repair are delivered here; close when PR #108 merges. |
| #64 | Retain open for remaining external-provider credential lifecycle and identity-continuity acceptance. This PR adds status-driven security UX, PostgreSQL recovery fencing, role-assigned administrator recovery, and account security throttling. |
| #61 | Retain the epic while its remaining children are open. |
| #71 | The immutable rc.5 publication evidence was posted to the issue; retain its dependent release-closure scope without publishing from this PR. |
