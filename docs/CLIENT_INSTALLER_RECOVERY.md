# Client installer recovery audit

Starting source: freshly fetched `origin/main`, `e56328d3e13c78488533f1a621006e4b7a72da8d`, product `0.1.0-rc.14`. Candidate: `0.1.0-rc.15`, changed only through `Directory.Build.props`; no RC.15 tag or release existed when checked. This change does not publish, merge or deploy anything. Final head and hosted receipts belong in the PR validation record.

## Evidence and causal map

The supplied Windows observation establishes download/extraction, a Running service, and an installer timeout whose last displayed stage was `disconnected`. It does not identify the transport error. No sanitized client log, Linux error output or authorized live fixture was available. Removing the installer gate does **not** establish a fix for either reported disconnect, or a common cause.

Local first-parent and constituent git diffs were inspected, including the historical `a33138e` flow, rather than relying on truncated GitHub patches.

| Change | Consequence |
| --- | --- |
| #113 / #116 | Protected install grants, verified artifact normalization/import, immutable script snapshots, tenant binding and validated public origins remain. The old installer is a structural reference, not release/configuration data. |
| #120, `4ea861a` | Keep `f1e4079` API persistence and `7f3f990` dotted `/netratel.gateway.v1.` routing. Its withdrawn Windows/WSL experiment is not recreated. |
| #127, `482f34a` | Keep operational Akka and protocol/storage compatibility. `4cd9f2d` coupled installation to two acknowledged heartbeats and the Program/presence reporter; remove that installer-only coupling. `39d6f92` had moved full Windows journeys to owner acceptance. |
| #132, `4f8550d` | Expanded preflight/seeds and reintroduced seven native Windows cases, including LocalSystem enrollment/repair and published-prior upgrade. Those hosted journeys and their fixture are removed. Their earlier execution did not diagnose the owner's disconnect. |
| #133, `f88756e` | Keep source/tag/version guards. The RC.13 tag points to `f341dc8` with RC.12 props; RC.14 points to `f88756e` with RC.14 props. |
| #124, `e56328d` | Keep `d1c3ff1` quiet request-body idle-timeout protection. It is **after** the RC.14 tag. |

The public RC.14 `publication.json` binds packages/build receipt `36859455498` and images to `f88756e716d4124f2c59100e9809dca8fe303004`. Its API digest is `sha256:015b2a3fa499c287e8cacf88fea168baac855b68b49b5545f0e41cbeb2e331f1`. This publication predates #124's ingress fix. The owner's deployed image/source revision was not observed; version strings alone do not establish it.

## Implementation and confirmed fixes

- Windows and Linux now use ordinary readable embedded `.ps1`/`.sh` resources and a small, single-pass renderer. Values become complete shell literals; JSON/XML values use their proper encoders. No second installer framework, encoded payload, generated C# or endpoint prerequisite was introduced.
- Windows preserves authorized same-origin downloads, redirect refusal, SHA-256/size/runtime/version/manifest validation, contained flat/runtime-wrapper archives, protected LocalSystem bootstrap, existing credentials, updater exclusion, same-version staging and bounded stop/start/rollback. It uses ordinary service/file primitives instead of the embedded C# process runner. Managed ordinary and product-prefixed environment aliases follow the client's real precedence. Explicit gateway pins are preserved when the request leaves the gateway blank, and are reported with their source.
- Linux preserves root/user installation, existing identity, verified ZIP/tar artifacts, supported quoted paths and ordinary systemd EnvironmentFile/drop-in customization. Unsupported identities, dynamic/reset directives and conflicting endpoint/path overrides fail before activation with a specific reason. Prior updater state/request/ready paths and policy are resolved before selecting the stable updater lock. Service results and rollback use systemctl, not a PID health heuristic.
- macOS had a demonstrated pointer bug: `mv -f` could follow `current` into the old directory. Both activation and rollback now use atomic Python `os.replace`. Prior plist restoration and rollback responsibility begin before bootout, so a failure before pointer replacement still restarts the prior daemon. Narrow accompanying fixes cover XML root-path escaping, ZIP containment, attempt cleanup and filtered launchd diagnostics.
- Seed scripts use explicit `IsUpdateSeed` rendering, replacing message/marker substitutions. Protected detached handoff, process identity, owned paths, configuration hashes and attempt cleanup remain. Their result reports local activation, not an installer heartbeat receipt. An explicit same-origin gateway override remains explicit.
- Removed the installer timeout DTO field, readiness options/reporter, Program integration, callback-only presence subscriptions and obsolete reporter tests. Kept real heartbeat sequence/protocol/tenant/agent/connection/epoch validation, `OnActivationHeartbeatAccepted`, update approval, activation confirmation and rollback. A missing challenge file is no longer a service startup dependency.
- Client startup now records the effective normalized API source alongside the existing gateway source. Installer output separates local installation/startup, enrollment and online facts; failure tails are bounded and redact credentials/capability URLs. No generic identity reset or stale credential restoration was added.
- Review exposed a repair regression in the replacement templates: adding a populated `Client` section could hide legacy flat settings. Both installers now preserve the runtime's flat/nested binding shape, including an empty `Client` object, and resolve existing updater paths before locking. Seven added cases exercise rendered code and the real configuration loader to verify identity, tunables, gateway pins and update policy/path preservation.

## Adjacent runtime inventory

This is a focused source/history audit, not complete production acceptance. No additional proven connected defect warranted an auth, persistence or operational runtime rewrite.

| Area | Kept behavior and compatibility |
| --- | --- |
| Commands/jobs/persistence | Akka routers/actors, durable command persistence and accepted-transition fanout; historical `JobShadowObservations`, source idempotency fields/index and persisted ordinals remain. |
| Terminal/remote support | V2 routes, actual-session tenant authorization, consent, operator binding and peer/epoch fencing. Historical terminal enum ordinals remain readable without selecting the retired transport. |
| Realtime | Normal runtime fanout and tenant authorization; existing envelope/property/enum shapes and historical group/event wire names remain. |
| Client directory/offline presentation | Persisted agents remain in the directory while live presence enriches them; empty, unavailable and unauthorized outcomes remain distinct. |
| Bootstrap/authentication | Post-Ready operational registration, health checks, PoP, issuer/tenant validation, unreadable credential preservation and narrowly authorized structured `agent_not_found` recovery remain. Disabled/revoked errors do not trigger generic reenrollment. |
| Endpoints/ingress | Separate REST composition and dotted gRPC routing, HTTPS gateway validation and #124 quiet-stream idle protection remain. Explicit service/installed gateway overrides are not inferred absent from a blank generated input. |

## Measured size

Generated counts use synthetic tenant/grant/origin/hash inputs, the same UTC validity instant, x64 Windows/Linux and arm64 macOS. Logical lines omit a final empty line; bytes are UTF-8. Functions count shell definitions, not embedded Python helpers (which are included in line/byte totals).

| Generated output | Before lines / bytes / functions | After lines / bytes / functions |
| --- | --- | --- |
| Windows service | 2,326 / 132,201 / 43 | 529 / 37,101 / 14 |
| Windows non-service | 410 / 18,049 / 6 | 529 / 37,102 / 14 |
| Linux service | 1,125 / 50,034 / 7 | 683 / 40,653 / 7 |
| Linux non-service | 392 / 16,230 / 3 | 683 / 40,654 / 7 |
| macOS service | 250 / 12,208 / 3 | 303 / 15,226 / 4 |
| macOS non-service | 88 / 4,380 / 3 | 121 / 5,732 / 4 |

The maintained installer/seed/readiness implementation file set totals **5,521 lines / 299,793 bytes before**, **2,402 lines / 141,701 bytes after**. This includes the renderer with macOS, both complete resources and all embedded Python, request interface, seed service and removed reporter; no helper is hidden outside this total. Seeds reduce from 1,329 to 746 source lines. Tests and unchanged genuine updater implementations are excluded from this implementation metric. Adjacent client caller changes primarily delete reporter plumbing and add the small API-source resolution result.

Windows exceeds the preferred 250–400 line target to retain owned-path/credential checks, artifact validation, configuration precedence, updater handoff and safe repair/rollback. Linux shares one flow between modes and retains ordinary systemd customization checks. Statements were not minified to meet a count; non-service outputs share the resources rather than duplicating maintained installers.

## CI scope and validation

Removed exactly three Windows-only steps / 50 workflow lines: prior package fetch, seven native case invocations and receipt upload. Deleted their 4,639-line native fixture and its obsolete Python workflow-selection assertion. No journey was moved into another job or generic selection. Existing guidance now requires explicit owner approval to reintroduce these journeys.

Retained native compilation/package matrices, archive layouts/checksums/SBOMs, source/version guards, generic unit/security/contract/integration suites and receipt aggregates, existing browser checks, Mac offline package/helper checks, Linux offline published-updater gate, dotted-ingress check and unrelated established server/Compose checks.

Local toolchain: pinned .NET SDK 10.0.401; Debug builds only. PowerShell 7.6.6 supplied syntax and isolated function probes. It does **not** establish native Windows PowerShell 5.1 execution. Bash/Python and temporary-filesystem probes do not establish native launchd/systemd production acceptance.

Local commands/results (with the pinned SDK and `NETRATEL_TEST_POWERSHELL` selecting the offline PS7 probes):

| Command | Result |
| --- | --- |
| `dotnet restore NetRatel.sln` | Passed. |
| `dotnet build NetRatel.sln --configuration Debug --no-restore -m:1` | Passed, zero errors/warnings on the final incremental build. |
| `tools/ci/verify-product-version.sh` | Passed, 20 projects at the candidate version. |
| `python3 tools/ci/test-release-validation.py` | 57/57 passed, including updated resource/retired-selector guards. |
| `tools/ci/check-public-disclosure.sh` and `tools/ci/test-public-disclosure.sh` | Passed. |
| Main project, Debug/no-build, `--filter-not-trait category=compose category=hosted` | 1,959/1,963 passed, zero skipped; see qualification below. |
| Affected installer/API/client classes, Debug/no-build | Final 214/214 passed, zero skipped, including the seven flat-settings correction cases. |
| API integration project, Debug/no-build, same generic category exclusions | Final 19/19 passed, zero skipped. |
| Web component project, Debug/no-build, same generic category exclusions | 229/229 passed, zero skipped. |
| Web Playwright project, Debug/no-build, same generic category exclusions | 39/39 passed, zero skipped. |
| `bash tools/ci/tests/nginx-gateway-idle.sh` | Blocked, exit 125: Docker Hub unauthenticated pull rate limit for `node:22-alpine`; no blind rerun. |

The full local Main run first exposed a case-sensitive source-text assertion against a correctly lower-cased HTTP header parser; the corrected case-insensitive HTTP-name assertion passes in the final affected-class run. Its other three visible failures are `CommandCancellationTests.Running_shell_cancellation_publishes_one_terminal_acknowledgement_with_a_cancelled_execution_token` and both theory rows of `Shell_cancellation_and_timeout_terminate_the_process_tree_and_finish_redirected_streams`. The failing stacks end at unchanged fixture `StopChildAsync` / `DisposeAsync` lines 257/263. Recorded children were killed `sleep` zombies (`State: Z`, `PPid: 1`, exit signal 9); this workspace's PID 1 is `tail -f /dev/null` and does not reap them. The fixture and termination code predate #127. Cleanup can mask earlier assertions, so this is an **overall failed local aggregate**, not a pass. No timeout, production runner or assertion was changed to turn it green.

API integration initially hit the same Docker Hub limit during fixture initialization (17/19 failed), then was rerun once after the required image was demonstrably cached and passed 19/19. Playwright's privileged dependency installer was unavailable to this non-root user; ordinary browser installation succeeded and the complete 39-case suite passed using available dependencies. An intermediate concurrent MSBuild child-node exit and a test-local name collision were corrected before the successful serial build.

The PR validation record supplies exact command selections, source SHA, log excerpts and hosted conclusions. Hosted iteration is limited to one initial validation plus at most two evidence-driven corrections, with no empty commits, manual rerun loop, relaxed generic receipt requirements or timeout inflation.

## Owner acceptance after approved publication

Use a **freshly generated** link from the new server/template and the intended imported package. Old protected links contain immutable script snapshots and do not update themselves. Do not reuse the historical capability links.

1. Windows and Linux clean install and repair: inspect reported API/gateway sources, executable/account/log paths, existing AgentId/device key/tenant, actual bootstrap/enrollment and online state. Running/active alone is insufficient.
2. Verify service restart and same-version repair, updater exclusion and genuine approved update/activation/rollback behavior without resetting credentials.
3. Test supported custom paths and service environment/drop-in configuration, including a deliberately rejected unsupported case before mutation.
4. Test the narrow Mac upgrade and rollback with `current` already pointing at a directory and a prior customized plist, including failure after bootout but before pointer replacement.
5. If either client disconnects, retain a bounded sanitized client log/journal and effective origins/configuration sources; classify token/API/TLS/proxy/gateway errors from evidence. These acceptance steps have not been run against owner machines or production.
