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

## #67 — delegated tenant-administration boundary

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/delegated-tenant-access-administration` |
| Reproduction | A principal with tenant-scoped `identity.admin` could not assign a lower-ranked role in that tenant because all access-administration routes required instance administration. |
| Repair | Authenticated access administration now permits a tenant administrator only for an explicit tenant scope. The server requires `identity.admin`, a strictly lower role rank, and a role permission set already held by the acting principal. Instance roles and instance scope remain instance-administrator-only; tenant user and assignment queries are filtered to the selected tenant. |
| Regressions | `AccessAdministrationEndpointTests` exercises an allowed lower-rank Tenant A grant and rejects both an at-ceiling grant and Tenant B assignment. The first case returned `403` before the repair. |
| Local evidence | Targeted Release test: 2 passed. Slopwatch on changed C# and test files: 0 findings. |
| Provider / auth | In-memory HTTP endpoint coverage with authenticated durable-principal role assignments. Browser tenant selection, lifecycle/MFA/recovery, scoped route/search, and final artifact acceptance remain required #67/#70 cells. |
| Next action | Submit for hosted CI; retain this as partial #67 progress until the remaining acceptance journeys are completed. |

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

## #64 — MFA replacement boundary

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/mfa-replacement-semantics` |
| Reproduction | The enrolled-user setup route accepted the current password, reset the authenticator key, and returned a new secret even while the old factor remained the active authentication mechanism. The response did not set `Cache-Control: no-store`. |
| Repair | Setup now returns a no-store conflict while two-factor authentication is enabled, preserving the enrolled key. Re-enrollment requires the supported disable flow—current password and current factor—before a new setup. Setup and recovery-code generation responses are explicitly no-store. |
| Regressions | `LocalTwoFactorEndpointTests` uses the real local-auth route, Identity store, and authorization policy to prove enrolled-key preservation plus no-store behavior for both rejection and new setup. The enrolled case returned `200` before repair. |
| Local evidence | Targeted Release test: 2 passed. |
| Provider / auth | In-memory endpoint coverage for local accounts. Browser MFA/recovery-code lifecycle, rate-limit behavior, lockout recovery, and role-assigned administrator recovery remain required #64/#67 acceptance cells. |
| Next action | Submit for hosted CI, then continue the remaining account-security journey rather than closing V03 on this boundary alone. |

## #64 / #67 — browser local-account security journey

| Field | Checkpoint |
| --- | --- |
| Branch | `feat/local-account-security-journey` |
| Reproduction | The local-account API exposed activation, password change, authenticator enrollment, recovery-code generation, and explicit disablement, but the Web application had no supported browser path for an operator to perform those lifecycle actions. |
| Repair | The Web application now has an anonymous activation page for an administrator-provided one-time handoff and a local-account security page for password changes, authenticator enrollment, one-time recovery-code acknowledgement, and current-factor disablement. The account navigation points to this supported surface. Secrets remain transient UI state and are not included in the ledger. |
| Regression | `LocalFirstComposeBrowserSmokeTests` extends the real local Compose journey: an administrator creates a disabled user, that user activates the account in the browser, enrolls an authenticator, uses one recovery code once, and disables the authenticator with a current code. |
| Local evidence | Release Web and Playwright test projects compile. Docker-backed execution is pending the hosted exact-head matrix. |
| Provider / auth | Browser flow uses the existing local cookie/BFF boundary and local Identity APIs. OIDC pages continue to state that their provider manages sign-in settings. Rate-limit/recent-auth, lockout/role-admin recovery, scoped-route/search, and final artifact matrix cells remain required. |
| Next action | Run the complete hosted source/extracted-bundle local-first and OIDC matrix, then continue the remaining #64/#67 acceptance without closing either issue on this slice alone. |

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
| Merge | PR #76 merged normally as `c05dbb11ee12b5255c737cdcf719df40de534cec` after the complete hosted validation workflow passed. |
| Idempotent retry repair | A retry carrying the same completed operation ID now reads the matching singleton marker and returns the original tenant/user identity without inspecting or applying replay form data. A different operation remains rejected; setup is never reopened. SQLite and PostgreSQL container regressions prove the original operation result and exactly one tenant/administrator after a replay with different submitted fields. |
| Next action | Merge this focused replay repair, then continue the remaining bootstrap interrupted-write and upgrade acceptance cells in #63/#70. |

## #65 — local HTTP MCP target requirements

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/issue-65-mcp-target-model`; target catalog: `fix/issue-65-mcp-target-catalog`; current target-admission branch: `fix/issue-65-mcp-target-admission`. |
| Reproduction | Local HTTP MCP rejected `netratel_capabilities/get` before tool execution because every exchange demanded both tenant and agent despite discovery operations having no business target. |
| Repair | A purpose-specific instance discovery permission and a separate instance-grant table preserve existing tenant tuple grants. The exchange and API-side current-access revalidation both allow only the catalogued no-target discovery operations with null tenant/agent and a current explicit grant. |
| Migrations | Generated PostgreSQL and SQLite identity migrations add `IntegrationCredentialInstanceGrants`; existing grants, verifiers, and credential purposes are unchanged. |
| Regressions | No-target capabilities exchange asserts no invented tenant/agent and a discovery-only delegation. Service and evaluator tests prove a tenant-only credential cannot obtain discovery authority merely because its owner is an instance administrator. |
| Local evidence | Focused VSTest suite: 64 passed, including SQLite migration coverage. Slopwatch: 0 findings. |
| Provider / auth | SQLite and PostgreSQL migration paths generated; local credential exchange remains purpose/resource-bound and OIDC propagation is unchanged. |
| Merge | PR #77 merged normally as `959c3dbb2dc4b08b5cf43b7aefd54f4d666495fe` after the complete hosted validation workflow passed. |
| Current foundation | PR #78 merged normally as `7e38d5a1b7c3b879f6de2e1f951cc43571e48942`: every advertised operation has fail-closed `NoBusinessTarget`, `Tenant`, `ObjectDerived`, or `Agent` metadata, published through capabilities and covered by catalog regressions. |
| Current admission repair | The local exchange and API current-access recheck enforce the catalog: no-business-target operations use explicit instance grants with no invented tenant/agent, tenant operations use tenant grants with no agent, and agent operations require an exact pair. Existing OIDC policy-selector delegation remains target-bound. |
| Current object-owner repair | On the server-selected V2 operator surface, the local gateway carries a normalized identifier only where the checked-in schema exposes a durable job, job-run, task, or request reference. The API resolves that record and requires its persisted tenant/agent owner to equal the signed pairing target at both exchange and current-access recheck. Missing, deleted, cross-tenant, wrong-agent, duplicate, malformed, or omitted required references fail closed. Development compatibility schemas, object-derived policy selectors, and script operations retain their established contracts rather than receiving an invented generic identifier. |
| Regressions | `McpOperationObjectTargetResolverTests` cover all four persisted owner projections, wrong-tenant/wrong-agent and family mismatch rejection. `NetRatelMcpHttpTests` covers normalized schema propagation, closed catalog metadata, and exchange rejection for an unresolved owner. Focused VSTest: 57 passed. |
| Next action | Add the real joined local HTTP MCP journey and remaining object-derived schemas before closing #65. |

## #66 — local HTTP MCP failure classification

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/mcp-local-error-classification` |
| Reproduction | The local credential authentication exchange previously reported every non-success upstream response, including throttling and service failure, as an invalid credential. The execution exchange raised an undifferentiated exception. |
| Repair | Local authentication and execution exchanges now carry a typed, non-secret failure classification: invalid credential, forbidden, throttled with a 60-second-bounded retry delay, unavailable dependency, deadline exceeded, or malformed response. Both use a 15-second linked deadline and a 16 KiB bounded JSON reader; caller cancellation continues to propagate. The HTTP ingress preserves normal 401 challenge semantics, emits safe JSON only for classified non-authentication failures, and the MCP filter reports the corresponding safe MCP error rather than an upstream detail. |
| Regressions | `NetRatelMcpHttpTests.Local_credential_authentication_*` covers upstream 401/403/429/500/400, `Retry-After`, connection failure, timeout, malformed payload, and oversized payload. |
| Local evidence | Release test-project build passed; focused VSTest: 9 passed. Slopwatch on all changed C# and test files: 0 findings. |
| Persisted-authority regressions | `Local_credential_mcp_journey_uses_persisted_authority_and_revokes_an_already_minted_execution` uses the actual local MCP host, HTTP-MCP credential verifier, persisted effective access, exchange endpoint, and current-access middleware: it mints a discovery-only assertion, reaches the execution endpoint, then rejects that same assertion after revocation. `Local_credential_postgres_exchange_isolates_concurrent_callers_and_rechecks_revocation` runs the exchange against PostgreSQL with concurrent callers carrying different explicit grants; only the discovery-scoped caller succeeds, and revocation still rejects its minted assertion. |
| Provider / auth | SQLite and PostgreSQL now cover persisted credential purpose/resource/grant enforcement and current execution recheck. OIDC behavior and native Client compatibility are unchanged; recovery-after-outage remains an acceptance cell in #66/#70. |
| Next action | Merge only after hosted CI passes, then complete recovery-after-outage and artifact-level acceptance rather than treating these joined regressions as final acceptance. |

## #69 — OpenAPI authentication contract

| Field | Checkpoint |
| --- | --- |
| Branch | `fix/openapi-auth-contract` |
| Reproduction | The operation transformer selected the first named authorization policy and described most routes as only `Bearer`, omitting local-session and grant-constrained integration-credential alternatives. It also advertised the purpose-bound local HTTP MCP exchange as an ordinary bearer flow. |
| Repair | The catalog now maps each supported policy to its explicit authentication alternatives, intersects requirements when metadata combines policies/schemes, and fails document generation if a newly added named policy has no declared contract. The local HTTP MCP pairing/exchange surface is excluded from interactive documentation; its scheme description explains the additional pairing boundary without disclosing credentials. |
| Regressions | `ReleaseOpenApiDocumentTests` starts the actual Production API with a bootstrap-ready SQLite local-first fixture, generates `/openapi/v1.json`, verifies every security reference and operation ID, checks anonymous and denied runtime behavior, validates representative local/OIDC/M2M/native alternatives, and confirms the HTTP MCP exchange is absent. |
| Local evidence | Targeted Release integration test: 1 passed. Source registration suite: 13 passed. Slopwatch on all changed C# and test files: 0 findings. |
| Next action | PR #84 merged normally as `3e589c94dcafb4da89e8b5fc6a5856bb9863eca9` after the complete hosted validation workflow passed. Retain Scalar browser and extracted-image validation as part of the #70 artifact acceptance matrix. |

## #70 — packaged PostgreSQL local-first acceptance

| Field | Checkpoint |
| --- | --- |
| Branch | `test/issue-70-postgres-artifact-acceptance` |
| Repair | Extend the existing extracted-bundle local-first journey to the bundled PostgreSQL recipe and the external PostgreSQL overlay, while retaining the existing SQLite cell and separate OIDC matrix. |
| Regression | The shared artifact harness selects an extracted Compose recipe and explicit test-only overlays. The external fixture creates a database owned by the application role, which is `NOSUPERUSER`, so migrations cannot accidentally depend on bundled bootstrap-superuser privileges. |
| Local evidence | Shell syntax, YAML parsing, and bundled/external PostgreSQL Compose interpolation passed without starting containers. Hosted source and release candidate evidence remains pending. |
| Provider / auth | The same browser setup, local credential, extracted CLI/stdio, and HTTP MCP path runs for SQLite, bundled PostgreSQL, and external PostgreSQL with OIDC unset. OIDC runs remain distinct. |
| Next action | Run the complete hosted PR matrix; record exact job results before claiming the new PostgreSQL cells. |

## #70 — historical rc.3 local upgrade and restore gate

| Field | Checkpoint |
| --- | --- |
| Branch | `test/issue-70-rc3-local-upgrade-restore`. |
| Repair | Add a CI-only historical-boundary harness that runs the immutable published `0.1.0-rc.3-1e6a9227375f` API, migrations, and Web images against local SQLite, then replaces them with the current candidate images from the extracted Compose bundle. |
| Regression | The harness initializes an rc.3 tenant and local administrator, archives SQLite, bootstrap state, and the shared Data Protection key volume, verifies an in-place current-image upgrade, restores that exact rc.3 state into clean volumes, and verifies current-image migration plus authenticated tenant access again. |
| Hosted evidence | PR #91 merged as `c553bab56432e370d6c7114b88a816e78c097fb3`. Exact-head run `35663466193` passed the gate in 5m35s alongside the complete required matrix. |
| Provider / auth | SQLite local-account continuity is now an explicit historical image-to-extracted-candidate acceptance path. The separate PostgreSQL/OIDC, Client, and other #70 cells remain gated independently. |
| Next action | Retain the release workflow rehearsal gate and add the historical PostgreSQL/OIDC continuity cell before recording that path as complete. |

## #70 — historical rc.3 PostgreSQL/OIDC continuity gate

| Field | Checkpoint |
| --- | --- |
| Branch | `test/issue-70-rc3-postgresql-oidc-upgrade`. |
| Repair | Add a CI-only historical-boundary harness that starts the immutable published rc.3 API, migrations, Web, and Client images with the disposable OIDC provider and PostgreSQL, then upgrades the same named PostgreSQL, API-key, Web-key, and Client-state volumes with current candidate images selected by the extracted Compose bundle. |
| Regression | The browser journey authenticates through both the direct and TLS-proxied OIDC paths on rc.3 and after the upgrade. It requires a durable external issuer/subject principal after rc.3 login and asserts that the exact principal count is retained after the candidate migration. The rc.3 authority creates a tenant and enrolls a published Client; that same persisted Client must authenticate against the upgraded candidate API. |
| Provider / auth | PostgreSQL/OIDC is distinct from the existing fresh OIDC and local-first matrices. It preserves published external identity, Client enrollment, and signing-key state; the existing independent gates retain CLI, MCP, local-account, and full Client command/telemetry coverage. |
| Next action | Run the required PR matrix and then keep the equivalent release-rehearsal job as a promotion prerequisite. |

No credentials, setup proofs, recovery codes, private endpoints, or customer data are recorded here.
