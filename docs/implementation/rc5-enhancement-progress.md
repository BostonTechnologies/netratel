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
| E00 inventory and traceability | #107, #61 | This ledger; current source, CI, issue discussions | Baseline captured at `cd02b362` | Capture reproducible runtime baselines and open the draft PR. |
| E01 PostgreSQL-only runtime and package | #107 | Pending | Not validated | Inventory and remove provider branches; port SQL regressions to PostgreSQL. |
| E02 first run and deployment | #107, #60, #106 | Pending | Not validated | Reproduce local mode with stale OIDC, key volume ownership, public proxy, reset/restart. |
| E03 account security UX | #107, #64 | Pending | Not validated | Add protected status and state-driven page. |
| E04 integration UX/grants | #107 | Pending | Not validated | Add multi-grant editor, endpoint validation, authority filtering. |
| E05 login and shell | #107 | Pending | Not validated | Center panel, retain drawer branding, update layout regressions. |
| E06 documentation and issue audit | #107, #22, #60, #61, #62, #64, #71 | Pending | Not validated | Rewrite newcomer journey; inspect accepted historical work. |
| E07 final acceptance and PR | #107 | Pending | Not validated | Run real PostgreSQL, HTTPS, browser, packaging, and exact-head CI matrix. |

Historical release notes, migrations, and issue comments remain evidence of their original
state. This ledger does not treat an open issue as complete based only on a release number.
