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
| PR checks | `Public disclosure gate`, `.NET build and tests`, `CLI and stdio MCP review artifacts`, native Client package matrix, and additional image/Compose jobs in `.github/workflows/public-pr-validation.yml`; active checks use generic PostgreSQL names and no fixed-release upgrade images. |
| Credential request | Current Web sends one `Grant(tenantId, permission)`, optional one instance permission, UTC end of selected expiry date, and an always-visible resource field to `/api/v2/account/integration-credentials/`. |
| Security page | Password, authenticator setup, and disablement forms render together; no current MFA status read. |
| Shell | App-bar and drawer both render branding; section captions are left-aligned; login panel is right-aligned by current CSS. |
| Deployment | Base image recipe has bundled PostgreSQL and localhost origins. External PostgreSQL overlay exists. Fresh named `web-keys` volume ownership is tracked by #106. |
| Documentation | First-run guide starts with SQLite; README does not expose the API-container setup-proof command near its top. |

## Work and evidence

| Requirement | Issue | Implementation and tests | Validated SHA / result | Unresolved item / next action |
| --- | --- | --- | --- | --- |
| E00 inventory and traceability | #107, #61 | This ledger; source, CI, issue discussions; PR #108 | `003da15`, PR #108 opened | Record final exact-head hosted checks before review. |
| E01 PostgreSQL-only runtime and package | #107 | Provider retirement, migration/test ports, `check-postgresql-only.sh` | `cce277b`; 107 focused PostgreSQL tests, the retirement contract, 1,774 Release project tests, and published rc.5 PostgreSQL/local and OIDC upgrades passed | Generic PostgreSQL package and migration gates remain active; fixed-release upgrade evidence is historical. |
| E02 first run and deployment | #107, #60, #106 | Local/OIDC selection and recovery, operator commands, root-owned volume init, public HTTPS overlay | `5dfc436`; extracted bundled and external PostgreSQL browser journeys passed; operator commands, restart, partial/full reset, and real public HTTPS ingress passed locally | Hosted exact-head deployment matrix pending. |
| E03 account security UX | #107, #64 | Protected status endpoint; state-driven MudBlazor cards and dialogs; local SVG QR; browser and API tests; role-assigned recovery and account security throttling | `89a0023` passed hosted browser gates; follow-up component tests passed 188/188, PostgreSQL regressions and source Compose browser passed | Hosted final exact-head gates pending. #64 retains separate external-provider acceptance. |
| E04 integration UX/grants | #107 | Authority catalog; multi-tenant/multi-grant wizard; URL validation; owner attenuation; PostgreSQL tests; field-specific creation errors and connection guidance | `89a0023` passed hosted CLI/MCP and browser gates; follow-up error/guidance review pending | Revalidate final head. |
| E05 login and shell | #107 | Centered login; drawer-only branding and section rules; labels; Playwright assertions | `6e745ea`; reviewed synthetic desktop/mobile captures; extracted bundle browser journeys passed | Hosted screenshot/browser gate pending. |
| E06 documentation and issue audit | #107, #22, #60, #61, #62, #64, #71 | README/hosting/first-run/INSTALL rewrites; rc.3 ledger; #22 closure; #71 checkpoint | #22 closed after merged #23/#26; #71 updated with exact rc.5 publication evidence; 18 release-validator tests and extracted bundle profiles passed | PR #108 carries the final hosted package check result. |
| E07 final acceptance and PR | #107 | PR #108 and release/CI gates | `89a0023`; hosted run 35908712130 passed all 22 jobs, including bundled/external/public-HTTPS browser, native Client, CLI/MCP, and rc.5 upgrades; follow-up Release build passed with 0 errors, 1,993 CI-filtered tests passed with 4 live-environment skips, and fresh source Compose browser passed | A new exact-head hosted run with generic PostgreSQL check names is pending. The version-pinned upgrade jobs and their unused smoke fixtures were removed from active CI. |

## PR #108 review corrections and rc.6 candidate

The reviewed source head was `57602acad7ad2748c31eeafe944e8cb42bec9c38`.
Its exact-head run `35969316729` passed all 19 generic PR jobs. The reviewer
identified four remaining defects. The tests below were first run against the
reviewed behavior and reproduced the failures before the repairs were applied.
The repaired source head `f11b1dd059a781f18cdb2869f09012b73219abf8`
passed [all 21 PR jobs](https://github.com/BostonTechnologies/netratel/actions/runs/35981914122),
including the generic PostgreSQL previous-release Local/OIDC upgrade matrix,
source and release-image OIDC browser tests, bundled and external PostgreSQL
first-run, restart/reset, and public HTTPS acceptance.

| Finding | Reproduction and repair | Local regression evidence | Hosted evidence |
| --- | --- | --- | --- |
| R1 administrator handover | PostgreSQL Local/Hybrid restart tests entered RecoveryRequired after initial administrator A was disabled. Ready continuity now uses the matching durable instance marker; interrupted setup still checks the original objects and matching operation when available. The regression uses real local account, role-assignment, disable, login and access endpoints with successor B, checks the final-administrator guard, removes the original tenant, and restores temporarily missing storage. | Initial Local/Hybrid failures reproduced; repaired endpoint journey passed in `BootstrapLegacyAdoptionPostgresTests`. Marker removal, wrong marker, key-material recovery, post-commit reconciliation and retry remain covered. | The PostgreSQL provider regression and previous-release Local/OIDC upgrade jobs passed in the linked run. |
| R2 OIDC audiences | Oidc/Hybrid bootstrap rejected plural audiences without a singular value. A shared API OIDC resolver now feeds bootstrap and JWT bearer settings, retaining singular and legacy aliases. | Initial plural failures reproduced; signed listed/unrelated audience fixture and aliases passed in `BootstrapLegacyAdoptionPostgresTests`. | Source and release-image OIDC browser jobs and the historical OIDC upgrade passed in the linked run. |
| R3 one-time results | A pending integration create could be closed and reopened into another draft; MFA setup could be canceled while pending. Both component regressions failed against reviewed behavior. Dialogs now hold in-flight work and acknowledgments, with request generations and disposal guards. | Controlled pending-task integration and account-security component tests passed after repair. | The .NET component and source/extracted-release browser jobs passed in the linked run. |
| R4 OIDC action | Reviewed `branding-compose` capture `10796305722` showed a blank solid primary button. The login action now uses a filled MudBlazor variant with the palette's contrast text and a visible focus outline. The first repair run exposed a 3.70:1 dark focused state from `PrimaryDarken`; the corrected dark palette measures above 4.5:1. Browser smoke measures label/icon contrast in system/light/dark, checks keyboard focus, and activates the named link. | The corrected source Compose OIDC smoke passed both HTTP and HTTPS browser cases, and the [light](assets/rc5-enhancement/login-oidc-light-desktop.png) and [dark](assets/rc5-enhancement/login-oidc-dark-desktop.png) synthetic captures show the readable action and focus ring. | The [hosted `branding-compose` artifact](https://github.com/BostonTechnologies/netratel/actions/runs/35981914122/artifacts/10801330485) passed; its light and dark desktop PNGs are byte-for-byte identical to the local captures. |

Executable review regressions and repairs: [PostgreSQL bootstrap and signed OIDC tests](../../src/NetRatel/NetRatel.Tests/API/BootstrapLegacyAdoptionPostgresTests.cs), [lifecycle continuity](../../src/NetRatel/NetRatel.API/Bootstrap/BootstrapLifecycleService.cs), [shared OIDC settings](../../src/NetRatel/NetRatel.API/Bootstrap/OidcApiConfiguration.cs), [integration component tests](../../src/NetRatel/NetRatel.Web.ComponentTests/IntegrationCredentialsStateTests.cs), [account security component tests](../../src/NetRatel/NetRatel.Web.ComponentTests/AccountSecurityStateTests.cs), and [browser contrast/sign-in test](../../src/NetRatel/NetRatel.Web.PlaywrightTests/OidcComposeBrowserSmokeTests.cs). The Release solution suite passed 2,014 tests with four existing live-environment skips; the focused component suite passed 197 tests. The disposable source PostgreSQL first-run, restart, partial-loss recovery, full reset, and OIDC journeys passed locally.

The remote rc.6 tag, GitHub release, and candidate tags in the five approved
container repositories were absent when checked. `Directory.Build.props`, the
release manifest, current notes, owner promotion examples, and active version
assertions now describe candidate `0.1.0-rc.6` together. rc.5 release notes and
earlier test fixtures remain historical. PR and release workflows contain a
generic Local/OIDC PostgreSQL previous-release upgrade matrix using controlled
published rc.5 image digests; no CI job or package repository is RC-named.
Neither this ledger nor a candidate version closes #64's external-provider
credential/identity acceptance or #71's later publication verification.

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
| OIDC sign-in and keyboard focus | [Light desktop](assets/rc5-enhancement/login-oidc-light-desktop.png), [dark desktop](assets/rc5-enhancement/login-oidc-dark-desktop.png) |
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
| #107 | The PostgreSQL-only/account UX scope and the R1–R4 review corrections are delivered in PR #108; close when it merges. |
| #64 | Retain open for remaining external-provider credential lifecycle and identity-continuity acceptance. This PR adds status-driven security UX, PostgreSQL recovery fencing, role-assigned administrator recovery, and account security throttling. |
| #61 | Retain the epic while its remaining children are open. |
| #71 | The immutable rc.5 publication evidence was posted to the issue; retain its dependent release-closure scope without publishing from this PR. |
