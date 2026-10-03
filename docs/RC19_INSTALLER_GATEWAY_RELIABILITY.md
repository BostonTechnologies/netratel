# RC.19 installer and gateway reliability evidence

This ledger concerns the requested `0.1.0-rc.19` candidate. The comparison baseline is RC.18 source `6154a60a6ca9cbcfab65d08d92c4c70031f4cff4`. The candidate's authoritative source identity is the final tested PR head and, after approval, the merged commit and publication record. A source change or laboratory receipt does not establish release or production acceptance.

Tracking: [RC.19 #157](https://github.com/BostonTechnologies/netratel/issues/157), [Linux installer #155](https://github.com/BostonTechnologies/netratel/issues/155), [Windows helper #156](https://github.com/BostonTechnologies/netratel/issues/156), and [sustained gateway #138](https://github.com/BostonTechnologies/netratel/issues/138). Retained implementation history includes [PR #135](https://github.com/BostonTechnologies/netratel/pull/135), [PR #139](https://github.com/BostonTechnologies/netratel/pull/139), [PR #147](https://github.com/BostonTechnologies/netratel/pull/147), and [RC.18 PR #154](https://github.com/BostonTechnologies/netratel/pull/154); [bootstrap issue #140](https://github.com/BostonTechnologies/netratel/issues/140) remains historical context. This work does not revise their historical acceptance claims or close #138.

## Evidence boundaries

| Evidence | Observation | What it establishes |
| --- | --- | --- |
| Owner's Windows report, 3 October 2026 | 12 presence admissions, 11 transport failures lasting 60.336–60.983s, unchanged service PID; HTTP 200/application-grpc/HTTP/2 and `INTERNAL_ERROR 0x2`; 13 separate helper XML registration failures | An admitted service repeatedly loses streams. Helper availability is a separate feature failure. These figures were supplied in the attached report, not independently recomputed from original logs. |
| Owner's Linux report | Two enrolled identities; lifetimes 60.201/60.046/60.022s and 60.241/60.047/60.026s; `NRestarts=0`; the same transport signature | Cross-platform recurrence with stable processes. These clients ran after an operator corrected `WorkingDirectory`; this was not an unchanged installer pass. |
| Local synthetic duplex laboratory | Same 15s heartbeat/data cadence and ACK protocol, direct h2c control versus two otherwise matching Traefik profiles | The fixture's whole-request read deadline can reset an active duplex stream; a single static timeout change removes that fixture failure. |
| Native authenticated product gateway laboratory | Production `AgentGatewayService`, real Akka presence router and native client renewal test through the same proxy fixture: 1/1 passed, no skips, 195.824s | Direct and corrected HTTPS streams exchanged 14 ACKs with unchanged connection/epoch across 195s; default HTTPS failed near 60s. The real client renewed its two-minute test token once, canceled the old lifetime, advanced its epoch and retained healthy presence. |
| Deployed ingress/API | Actual proxy version/digest, effective static settings and terminating-hop logs were not available | The deployment's terminating hop and production cause remain unproven. The tested fixture version does not identify the deployed version. |

The first reported Windows failure was `2026-10-03T10:32:56.3449916Z` (12:32:56 SAST); the latest supplied failure was `2026-10-03T10:47:01.8488545Z`. Initial token expiry around 10:46:53Z cannot explain the first failure. The separately reported normal renewal near 10:46Z is not one of the 11 transport failures. Earlier control/file/telemetry transport errors must be distinguished from later presence-owner child cancellation. Approximately 15s ACK age at reset does not establish idle expiry.

The attachment contains the owner's request/report. Original Windows log and generated task XML bytes, host-side Linux logs, and deployed proxy/API logs were not attached. Windows investigation paths on the owner's machine were not accessible from this workspace. No live installation capability URL was fetched or redeemed.

## Linux repair and native path limits

The production renderer now emits `WorkingDirectory` as a systemd scalar path, while preserving command/environment quoting, endpoint separation and input rejection. The normal and seeded generated units are checked with the native systemd parser. Staged pre-activation validation uses an isolated root and inert executable fixtures when `systemd-analyze` is available; it does not execute the client or stop an existing service. Unsupported or invalid units fail before activation. Absence of the native verifier is reported explicitly.

Local parser evidence uses systemd 257.13. Default, spaces and non-ASCII directory semantics are covered. A literal backslash survives native `WorkingDirectory` and environment parsing, but the native parser rejects the corresponding executable root. Installation roots containing a backslash therefore fail before mutation with a supported-root error. Backslashes in state/log/environment values remain supported; a parser-accepted directory alone is not an executable installation acceptance claim. Newlines, specifiers and unknown layouts remain rejected.

Rollback now distinguishes confirmed no-process state from an unavailable manager or an unclassified/running process using the owned unit, active/substate, PIDs and owned cgroup evidence. Fresh failures unwind only the attempt's registration, enablement, links and artifacts; existing repairs restore prior installation state while preserving identity. Original activation failures and separate rollback failures remain visible, and uncertain rollback retains recovery data.

The final local Linux/seed class selection passed 59/59 tests with no skips in 13.626s. Against unchanged baseline source, the two normal/seeded native-parser assertions reproduce the quoted-directory defect. Three other baseline failures were failures of new diagnostic-message assertions and are not direct proof of the old rollback behavior; that behavior is supported by the owner's report and the inspected old rollback condition. Final controlled rollback fixtures pass. Rollback tests use controlled manager responses and temporary owned files; the native parser checks are separate evidence. No real client enrollment or system service installation was performed.

## Windows helper boundary

Original failing XML bytes were unavailable, so their length/hash, BOM/declaration match and any concurrent partial write cannot be established. RC.18 had already removed invalid `LogonType=Group`; this candidate does not claim that old fix as new or assert that UTF-8 is universally rejected.

The chosen production handoff removes the mutable XML-file/schtasks import boundary. A narrowly dispatched child passes the complete XML as Unicode to Task Scheduler `RegisterTask`; the parent bounds that child to 15s. `TASK_CREATE_OR_UPDATE`, the Users group SID, `TASK_LOGON_GROUP`, `LeastPrivilege`, `IgnoreNew` and targeted `RunEx` session semantics are retained. No password or SYSTEM interactive-capture fallback is added. The helper launchers use complete atomic replacement, and process-owned callers serialize registration and avoid repeating successful unchanged work. Failed work remains eligible for retry.

The shared child runner drains stdout/stderr concurrently, retains at most 65,536 characters per stream, and applies its deadline before process creation/reads. Timeout or cancellation terminates the spawned tree and bounds reaping/drain cleanup to one further second; cleanup failures are reported. A descendant already orphaned before its parent's exit cannot be rediscovered through the exited parent's process tree, but inherited pipes still cannot extend the operation deadline. The Unix-only fixture owns that descendant's cleanup.

Local focused client/session/process tests passed 72/72 with no skips in 11.683s; installer/bootstrap regressions passed 76/76 with no skips in 9.626s. Native Windows acceptance is pending. The retained generic Windows offline lane selects 21 executed cases: 12 installer/ACL cases, three complete Unicode `TASK_VALIDATE_ONLY` cases, and six transient-process cases. Validate-only creates or runs no task and uses the exact production XML/COM seam; it does not claim schtasks accepted old file bytes. Registration or an accepted launch is not a connected helper: a validated pipe hello in the intended session remains necessary. Repair refuses to terminate an existing helper when replacement registration is unconfirmed. Feature-specific unavailable outcomes retain healthy core gateway presence.

The working Windows installer is unchanged. Its PS5.1 type loading, numeric bootstrap tenant, diagnostic filtering, scoped ACLs, LocalSystem bootstrap, separate endpoint persistence and updater rollback remain regression requirements. Actual task registration, interactive consent, capture and input remain owner acceptance operations.

## Sustained transport reproduction and operator scope

The isolated Traefik v3.7.13 image identified itself as `sha256:f9309349d2c1477b15d04728f38882ba2e9a50b7f76729541121a7ee60d53490`. One synthetic receipt recorded:

| Profile | Lifetime | ACKs | Result |
| --- | ---: | ---: | --- |
| Direct private h2c control | 195.033s | 14 | Same stream, no reconnect, clean completion |
| Traefik default entrypoint | 60.036s | 4 | Client `INTERNAL_ERROR 0x2`; latest ACK age 15.001s; upstream cancellation |
| Same proxy with only `readTimeout=0s` | 195.011s | 14 | Same stream, no reconnect, clean completion |

Exact dotted gRPC namespace routing, a near-miss routed to REST, and TLS-to-private-h2c routing are retained. The existing NGINX regression also preserved one bidirectional stream across 65s of request-body silence. Neither synthetic responders nor the NGINX test establish native product renewal or identify the deployed proxy.

For an operator-confirmed Traefik deployment with the conflicting whole-request body limit, the precise reference change is `entryPoints.<dedicated-gateway-listener>.transport.respondingTimeouts.readTimeout: 0s`. Merge the [static reference fragment](deployment/traefik-gateway-static.yaml) into the existing operator-owned configuration; its example listener is `netratel-gateway` on port 8443. Apply it through the ordinary approved Traefik restart/configuration procedure, attach only the authenticated gateway router, and preserve TLS, trust, network and authentication controls. Publish that listener's actual HTTPS Gateway URL independently of Branding Site/API values.

A hostname or dynamic router label on the same shared listener does not isolate a static entrypoint timeout. This change removes only the conflicting read-body deadline; review finite `writeTimeout` against intended stream lifetime separately. Idle/keep-alive, upstream response-header timeouts, HTTP/2 PING and GOAWAY policies remain distinct boundaries. The [deployment procedure](CLIENT.md#sustained-traefik-gateway-streams) documents the full scope. No production ingress change was made.

The native authenticated probe uses fixed fixture bearer identities and an in-memory agent catalog, not production JWT issuance or enrollment. It passed direct/proxied sustained positive ACK exchange, default-profile failure, the same one-knob corrected profile, and intended client token renewal with old-session fencing. Early fixture attempts failed at public-CA loading and no-SNI certificate selection; both were corrected without relaxing certificate/hostname validation, with failures retained separately. Native Windows handoff and all retained hosted checks remain publication gates. Final hosted receipts, review disposition and source/version/artifact identities belong to the candidate PR. #138 remains open until its deployed-path acceptance contract is met.

## Size checkpoint and owner acceptance

Static templates are Linux 811 lines/47,948 bytes (baseline 683/40,649) and unchanged Windows 675 lines/49,053 bytes. Calling the actual compiled `ScriptTemplateService.Build` with fixed synthetic inputs (tenant 4098, RC.19 artifact, distinct example API/Gateway origins, fixed expiry, no live capability) yields normal/seeded Linux 811 lines with 47,995/48,052 UTF-8 bytes and normal/seeded Windows 675 lines with 49,007/49,198 bytes. Supporting production C# (client plus seed normalizer) changes by +594/-156 lines, net +438; the bounded child runner is 166 lines. These measurements are evidence, not version inputs. All 20 projects evaluate to `0.1.0-rc.19` from `Directory.Build.props`. Final hosted checks and native receipts remain pending.

After verified RC.19 publication, the owner can use this non-destructive procedure:

1. Deploy the matching approved server/templates and import the matching client package. Verify preserved Branding Site/API and Gateway values, then generate new immutable Windows/Linux links; old snapshots remain unchanged.
2. Run the generated commands unchanged on disposable clean Windows and Linux/systemd clients. Separately repair/update an enrolled RC.18 client without deleting credentials. Verify original identity, exact version, paths/endpoints, bootstrap consumption and service state independently.
3. Confirm Linux units need no manual edit. Inspect rollback using an owned simulated startup failure only on a disposable target, retaining recovery evidence.
4. With an interactive Windows user present, verify task registration, actual helper process/session and connection. Separately exercise authorized consent, capture and input. No interactive user is a legitimate feature-unavailable state.
5. Observe the corrected actual ingress for at least 30 minutes and at least two normal token lifetimes, extending the window when needed. Record stable service PIDs, recent ACK/telemetry, renewal handoffs, retries and absence of unexplained 60s resets. Perform harmless command, file-read and terminal operations before/after a controlled authorized reconnect.

Preserve enrolled identities and recovery state. No owner-host deployment, service restart, enrollment, desktop operation or destructive cleanup was performed by this work. RC.19 remains a prerelease; later stable promotion requires separate owner acceptance and authorization.
