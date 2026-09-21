# rc.3 post-review progress

This ledger follows [the post-rc.3 correction epic](https://github.com/BostonTechnologies/netratel/issues/61).
It records follow-up work without changing the historical status of the original
local-first delivery or `v0.1.0-rc.3`.

## Current baseline

| Item | Evidence |
| --- | --- |
| Integration baseline | `main` and `v0.1.0-rc.3` resolve to `1e6a9227375ffbc075690f1e5ffa0ba051f042af` when the review follow-up started. |
| Follow-up ledger | Epic #61; reproduction #62; bootstrap #63; identity #64; MCP target mapping #65; MCP failures #66; scoped administration #67; image profiles #68; OpenAPI #69; acceptance gates #70; release #71. |
| F01 reproduction | With OIDC unset, the released `compose.images.yaml` rejected `docker compose ... config --quiet` because OIDC variables were required. |

## #68 — distributable local-first Compose profiles

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-68-release-local-first-profiles` |
| Repair | Image-only SQLite profile, external PostgreSQL override, optional OIDC configuration for local-account profiles, shared API/Web Data Protection volume, API restart behavior, packaged local HTTP MCP guide, and extracted-bundle validation. |
| Regressions | `tools/ci/verify-release-local-first-profiles.sh`; extracted image-bundle SQLite browser setup/login smoke. |
| Local evidence | The extracted image bundle completed the existing `LocalFirstComposeBrowserSmokeTests` with OIDC absent; 1 passed. `tools/ci/test-release-validation.py` passed: 17 tests. |
| Provider / auth | SQLite with local account: exercised locally from an extracted image bundle. Bundled PostgreSQL, external PostgreSQL, and OIDC remain CI/acceptance matrix cells. |
| Merge | Pending CI-gated PR and normal review. |
| Next action | Open #68 PR, inspect every required job, merge normally only after all checks pass; then extend the artifact matrix in #70. |

No credentials, setup proofs, recovery codes, private endpoints, or customer data are recorded here.
