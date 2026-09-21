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
| Merge | PR #72 merged normally as `df05ef63e38a0ec2fe773d23100c4df399ce63a0` after all hosted checks passed. |
| Next action | Extend the artifact matrix in #70 without replacing the independent OIDC and PostgreSQL acceptance paths. |

## #67 — scoped local account and administration journeys

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-67-access-selection-race` |
| Repair | The A-slow/B-fast assignment response no longer updates the editor after B becomes selected. Assignment/removal requests capture their target before awaiting and only refresh that target while it remains selected. |
| Regression | `AccessAdministrationSelectionTests.Slow_previous_selection_cannot_replace_the_current_users_assignments`. The test failed against the prior implementation and passes after the repair. |
| Local evidence | Targeted Release component test: 1 passed. Slopwatch on the changed Razor/test files: 0 findings. |
| Provider / auth | UI-only deterministic component coverage; broader local lifecycle, delegated administration, scoped-route, and browser matrix cells remain in #67. |
| Merge | PR #73 merged normally as `f10bab2a750a404260e80eaac608f162a7b3c7d2` after all hosted checks passed. |
| Next action | Continue the remaining lifecycle, delegated administration, scoped-route, and browser matrix cells in #67. |

## #64 — viable administrator identity continuity

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-64-viable-admin-invariant` |
| Reproduction | The previous counters included every instance-admin role assignment, including assignments retained by disabled local users. The A/B sequence could therefore allow removal of the final viable local administrator. |
| Repair | A shared invariant service counts only durable principals with an enabled local login or a complete external issuer/subject binding. Destructive local-user and instance-admin-assignment mutations use SQLite serializable transactions or PostgreSQL transaction advisory locking. |
| Regressions | `InstanceAdministratorInvariantTests`: disabled assigned local user, enabled assigned local user, unresolved assignment, and complete external identity binding. `InstanceAdministratorInvariantPostgresTests`: two independent PostgreSQL contexts cannot remove both viable administrators. |
| Local evidence | Release build discovered the five tests; VSTest executed them: 5 passed. Slopwatch on changed C# files: 0 findings. |
| Provider / auth | In-memory semantic coverage and PostgreSQL transaction-lock coverage are complete for this invariant. Endpoint/browser lifecycle, lockout, MFA, and OIDC credential lifecycle cells remain required before #64 can close. |
| Merge | PR #74 merged normally as `30ebe4641f2935bf97fa9cb4dc42f14541b0326d` after all hosted checks passed. |
| Next action | Continue account lifecycle, lockout, MFA, and OIDC-created credential acceptance. |

## #64 — verified OIDC principal projection

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-64-oidc-principal-projection` |
| Reproduction | The OIDC JWT configuration left `TokenValidationParameters.AuthenticationType` at IdentityModel's default while principal projection accepts only a validated identity typed `Oidc`. |
| Repair | The OIDC bearer configuration explicitly sets `AuthenticationType = "Oidc"`; no untrusted application-principal claim is accepted from the token. |
| Regression | `Validated_oidc_token_projects_a_stable_application_principal` validates an RSA-signed issuer/audience/lifetime/signing-key JWT, runs the transformation, and asserts the durable issuer/subject binding. |
| Local evidence | Focused VSTest suite: 19 passed. Slopwatch on changed C# files: 0 findings. |
| Provider / auth | Real JWT validation plus in-memory durable-principal persistence. Credential lifecycle and a disposable external-provider upgrade fixture remain in #64/#70. |
| Merge | PR #75 merged normally as `29a0da1cf48d8a151c85b7fe33694077d8579e46` after the complete hosted image, bundle, OIDC, package, MCP, and browser matrix passed. |
| Next action | Continue the remaining #64 acceptance. |

## #63 — transactional bootstrap completion recovery

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-63-bootstrap-transaction-recovery` |
| Reproduction | First-run setup committed the initial administrator and tenant before `descriptor.json` could be marked Ready. A crash in that interval left an expired configuration lease in restricted recovery despite the durable initialization having succeeded. |
| Repair | The existing cross-context database transaction now commits a singleton initialization record containing the bootstrap instance and operation, tenant, and initial administrator. Startup reconciles only a matching committed record, tenant, enabled instance administrator, and principal binding; only the expired-configuration-lease recovery reason is eligible. |
| Migrations | Generated PostgreSQL and SQLite migrations create `BootstrapInitializations`; no historical migration was changed. |
| Regressions | SQLite and PostgreSQL Testcontainers regressions construct the post-commit/pre-descriptor state, expire the lease, and verify Ready reconciliation. Existing state-store coverage verifies the specific recovery reason. |
| Local evidence | Focused VSTest suite: 18 passed across SQLite, PostgreSQL, and state-store coverage. Slopwatch: 0 findings. |
| Provider / auth | SQLite and PostgreSQL migration/application paths are exercised. Reconciliation preserves the restricted state when durable evidence is absent, malformed, mismatched, or inaccessible. |
| Merge | Pending CI-gated PR. |
| Next action | Open a focused PR and continue the remaining bootstrap interrupted-write and upgrade acceptance cells in #63/#70. |

## #65 — local HTTP MCP target requirements

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-65-mcp-target-model` |
| Reproduction | Local HTTP MCP rejected `netratel_capabilities/get` before tool execution because every exchange demanded both tenant and agent despite discovery operations having no business target. |
| Repair | A purpose-specific instance discovery permission and a separate instance-grant table preserve existing tenant tuple grants. The exchange and API-side current-access revalidation both allow only the catalogued no-target discovery operations with null tenant/agent and a current explicit grant. |
| Migrations | Generated PostgreSQL and SQLite identity migrations add `IntegrationCredentialInstanceGrants`; existing grants, verifiers, and credential purposes are unchanged. |
| Regressions | No-target capabilities exchange asserts no invented tenant/agent and a discovery-only delegation. Service and evaluator tests prove a tenant-only credential cannot obtain discovery authority merely because its owner is an instance administrator. |
| Local evidence | Focused VSTest suite: 64 passed, including SQLite migration coverage. Slopwatch: 0 findings. |
| Provider / auth | SQLite and PostgreSQL migration paths generated; local credential exchange remains purpose/resource-bound and OIDC propagation is unchanged. |
| Merge | Pending CI-gated PR. |
| Next action | Complete parameterized target/permission classification for every advertised operation and the real joined local HTTP MCP journey before closing #65. |

No credentials, setup proofs, recovery codes, private endpoints, or customer data are recorded here.
