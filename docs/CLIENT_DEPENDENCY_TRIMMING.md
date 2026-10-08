# Client dependency cleanup and trimming assessment

Refs [#121](https://github.com/BostonTechnologies/netratel/issues/121).

The native win-x64, linux-x64 and osx-arm64 distributions remain self-contained,
single-file and untrimmed. Docker retains its existing publish mode. The product
version authority, gateway protocol, manifest, native media/PTY helpers and
updater contracts are unchanged.

## Implementation

All PowerShell tasks now use `ExternalShellRunner`. New API requests using the
legacy aliases become canonical PowerShell library snapshots, preserving their
engine, parameters, working directory and timeout. Historical raw `RunPowerShell`
and `ExecPs` gateway dispatches retain their observable property-dictionary/string
success arrays through a small external adapter. API history and Web rendering
accept those arrays. Canonical command/library results keep their existing
exit-code/stdout/stderr envelope. External stdout/stderr remains bounded and
redacted in task logs; cancellation uses the existing process-tree cleanup.

Explicit `Pwsh` and `WindowsPowerShell` never substitute engines. A shared
executable resolver handles PATH and standard installation locations for tasks,
inventory and terminals; version discovery is cached. PowerShell Auto respects
engine/version directives and requires pwsh on Unix. Windows PowerShell source
and redirected output use UTF-8 without process-wide environment changes.

The seven embedded engine package references, embedded executor and result/stream
types, engine config and startup environment/profile/module rewrites are removed.
Nine audited unused client package references are removed. Five explicit live
references replace APIs previously supplied transitively: configuration binding,
command-line and environment providers, HTTP client factory and Windows EventLog.
Client direct package references fall from 24 to 13.

Five agent-only auth implementations now belong to Client. The single shared
PoP signature implementation belongs to Shared/Security; its serialization and
signed bytes are unchanged. Credential/key/journal formats, permissions and
refresh recovery remain intact. Existing Unix fallback credentials in the old
embedded profile directory are discovered in place, keeping their journal and
container identity together; new fallback storage uses the account's home.
`NETRATEL_POWERSHELL_HOME` is read only for that old credential-location recovery.
Installed settings containing `UseInProcPowerShell` continue loading.

Client no longer references Infrastructure. SchemaValidator and NJsonSchema now
belong to Infrastructure with the original validation test. Application remains
for client auth interfaces. Neither baseline nor cleanup client project closure
has an Akka dependency; generated gRPC still connects to the Akka-backed gateway.

## Reproduction and evidence

Baseline A is latest main at task start:
`b2a07e8a32d59539431d8040f4e31654f9e3f0f7`. B is cleanup source in the same normal
untrimmed publish mode; C is the same cleanup source in opt-in trimmed JIT mode.
The measured B/C source is
`8047ce86784a581bc99634503eab428817105a9b`; subsequent commits contain measurement
tooling/evidence only. The pinned SDK is 10.0.401 and product version remains
0.1.1-beta.3. [The hosted comparison](https://github.com/BostonTechnologies/netratel/actions/runs/37743230006)
published A/B once per RID and C once on Linux, on matching hosted OS runners.

All sizes below are bytes, using the existing native archive conventions.

| RID | Executable A → B | Extracted distribution A → B | Archive A → B | Archive reduction |
| --- | --- | --- | --- | --- |
| linux-x64 | 74,433,257 → 49,412,902 | 79,983,502 → 51,605,383 | 67,753,479 → 42,477,787 | 37.305% |
| win-x64 | 71,848,455 → 47,828,775 | 77,435,837 → 50,045,941 | 67,079,198 → 42,764,753 | 36.247% |
| osx-arm64 | 71,713,456 → 46,586,480 | 77,247,827 → 48,763,087 | 65,800,995 → 40,413,780 | 38.582% |

| RID | Restored NuGet A → B | Final runtime graph NuGet A → B | Final prebundle files A → B | Restore seconds A / B | Publish seconds A / B |
| --- | --- | --- | --- | --- | --- |
| linux-x64 | 177 → 54 | 144 → 51 | 616 → 240 | 13.054 / 1.728 | 86.316 / 16.384 |
| win-x64 | 177 → 54 | 150 → 50 | 626 → 240 | 68.236 / 1.552 | 76.960 / 17.495 |
| osx-arm64 | 177 → 54 | 143 → 50 | 613 → 239 | 15.752 / 2.061 | 72.846 / 22.190 |

A and B share SDK and runner image within each RID: Linux
`20260927.320.1`, Windows `20260925.250.1`, macOS `20260907.0351.1`.
Intermediates are fresh for every source/mode/RID; A/B/C run sequentially and share
the runner's NuGet cache. These are single observations with warmer restore and
OS caches for B/C, not controlled cold-build or startup benchmarks.

The final graphs remove the embedded PowerShell, EF Core/Npgsql, NJsonSchema,
Refit/Rundeck and MCP chains. SIPSorcery/FFmpeg remain in B on every RID with
matching media assembly hashes. PTY helpers, updater scripts, LICENSE and NOTICE
remain byte-identical; the removed loose sidecar is `powershell.config.json`.
`appsettings.json` changes only by removing the obsolete `UseInProcPowerShell` key.
Graph nodes may have dependency-only entries: the captured final prebundle file
inventory is the physical input evidence. The runtime pack and first-party
projects are counted separately from NuGet packages (A has five project nodes,
B/C four, including Client itself).

Linux and macOS completed integrity/content checks and both `--version` probes.
Windows A/B publish and packaging also succeeded, but the measurement helper
selected Windows' WSL `bash.exe` and failed before integrity/launch validation.
The original failure remains in the evidence; a separate verification reuses the
original archives after selecting Git Bash explicitly. The first follow-up
exposed a CRLF checksum-index issue: digest values were correct, but Git Bash's
checksum tool treated the carriage return as part of each filename. The helper
now writes LF, and archive reuse verifies the original index values before
normalizing only a separate validation copy.

[Windows archive verification passed](https://github.com/BostonTechnologies/netratel/actions/runs/37745497650)
for A/B on the same Windows image and SDK as the measurement. It verified the
original archive hashes, manifest sources, full file inventories, SBOM/checksums,
candidate content and fresh/cached `--version` probes, without restore/publish.
The [receipts](evidence/client-dependency-trimming/windows-archive-verification.json)
retain original and normalized index hashes and original archive/metrics identity.
The [failed verification](https://github.com/BostonTechnologies/netratel/actions/runs/37744824744)
and original failed metrics remain visible.

Committed [measurements](evidence/client-dependency-trimming/measurements.json)
retain exact stage outcomes, sizes, archive hashes, SDK/image identities and
inventory hashes. The [package inventories](evidence/client-dependency-trimming/package-inventories.json)
retain exact restored/final package versions and distribution files per RID.
Full logs, evaluated properties, project assets, final graphs and prebundle
inventories are in `client-measurement-evidence-<RID>`; native archives, SBOMs and
checksums are in `client-measurement-archives-<RID>` on the run above (14-day
retention). The normal release attestation/publication pipeline is unchanged.

### Linux trimming assessment

[Initial C](https://github.com/BostonTechnologies/netratel/actions/runs/37741600689)
used source `101d59124c918402cb47c7752764611adf9396fd`. It published successfully
in 62.580 seconds, with 363 compiler IL diagnostics and 59 linker diagnostics;
four ordinary warnings brought the build summary to 426. The one focused cycle
added Web-default task payload metadata and typed reads, preserving mixed-case
properties, quoted numbers, enum handling and literal parameters.

Final C at the same source as B published successfully in 27.979 seconds.
The two payload reads no longer warn: compiler IL diagnostics fell to 359 and
linker diagnostics to 57 (420 total build warnings including four ordinary
warnings). Remaining task-result serialization warnings were not suppressed.
Counts exclude the repeated MSBuild summary; raw `warningsByCode` metrics include
those repeats and must not be read as unique call-site counts.

| Linux size | B untrimmed | C trimmed JIT |
| --- | --- | --- |
| Executable | 49,412,902 | 16,083,102 |
| Extracted distribution | 51,605,383 | 17,900,397 |
| Archive | 42,477,787 | 9,331,008 |
| Final runtime NuGet nodes | 51 | 32 |
| Final prebundle files | 240 | 94 |

C passes manifest, SBOM/checksum, retained support-file and removed-content
checks. Linux media packages are absent from its linked graph; native PTY support
remains. This per-RID reachability result does not validate the complete media
stack. Native AOT was not published; IL3050 here is analyzer evidence.

### Launch probes and required CI

These seconds measure only `--version`, before normal initialization. Each pair
uses a fresh extraction directory and then reuses it.

| RID/mode | Fresh extraction | Cached extraction |
| --- | --- | --- |
| Linux A | 0.607 | 0.066 |
| Linux B | 0.364 | 0.055 |
| Linux C | 0.178 | 0.126 |
| macOS A | 1.109 | 0.085 |
| macOS B | 0.506 | 0.068 |
| Windows A (archive verification) | 0.876 | 0.108 |
| Windows B (archive verification) | 0.509 | 0.087 |

[Measured implementation CI](https://github.com/BostonTechnologies/netratel/actions/runs/37743234968)
passed in 10m55s: restore 26s, Release build 1m36s, Core 3,311 tests in 404s,
API/PostgreSQL 35 tests in 85s, Components 414 tests in 6s. All 3,760 executed
tests passed with zero skips. Latest-main CI was 11m23s (restore 23s, build 1m44s,
fast regressions 8m33s); these single samples do not isolate runner variability.
[Initial implementation CI](https://github.com/BostonTechnologies/netratel/actions/runs/37741445778)
passed 3,757 tests in 16m16s, also below the required 20-minute ceiling. The
measured implementation meets the 10–15 minute target. No publish or OS matrix
was added to required CI. The optional comparison builds only the client closure.

Local Debug validation passed 160 focused execution/auth/configuration/terminal
tests and then 22 payload/API tests after the metadata correction, with no skips.
All 85 Python release validation tests passed, including the two Windows tooling
regressions. Local process-tree tests ran under the
existing `docker-init` subreaper because this workspace's PID 1 does not reap
orphaned children; assertions and categories were preserved.

The optional comparison jobs took 183s (Linux A/B/C), 166s (macOS A/B) and 221s
(Windows A/B with the original verification failure): 9m30s aggregate runner
time. Initial Linux C took 86s; Windows archive-only verification took 41s for the
evidenced checksum correction and 65s for the passing follow-up. A preliminary
[dispatch](https://github.com/BostonTechnologies/netratel/actions/runs/37741444268)
failed at checkout in 39s before any restore/publish. There were exactly two
Linux trimmed publishes and one A/B publish pair per supported RID.

Use the existing optional workflow on the implementation branch:

```sh
gh workflow run integration-validation.yml --ref perf/client-dependency-trimming \
  -f suite=client-measurement -f source_ref=IMPLEMENTATION_SHA \
  -F assessment_only=true

gh workflow run integration-validation.yml --ref perf/client-dependency-trimming \
  -f suite=client-measurement -f source_ref=IMPLEMENTATION_SHA \
  -f baseline_ref=b2a07e8a32d59539431d8040f4e31654f9e3f0f7 \
  -F assessment_only=false -F client_trim_diagnostic=true
```

The first command performs only the initial Linux assessment. After at most one
focused correction, the second performs one native A/B comparison per RID and
the Linux C comparison. It builds only the client reference closure. All restore
and publish mode properties are consistent global properties; per-source/mode/RID
SDK artifacts paths retain separate intermediates for every referenced project.
Normal profiles cannot override the diagnostic trimming selection.

For the evidenced Windows tooling failure, the archive-only follow-up is:

```sh
gh workflow run integration-validation.yml --ref perf/client-dependency-trimming \
  -f suite=client-measurement \
  -f source_ref=8047ce86784a581bc99634503eab428817105a9b \
  -f baseline_ref=b2a07e8a32d59539431d8040f4e31654f9e3f0f7 \
  -f measurement_verify_run=37743230006 -F assessment_only=false
```

This mode selects one Windows job, checks out the original A/B sources and the
workflow's corrected tooling separately, verifies recorded archive hashes and
file inventories, and writes separate receipts. It does not restore or publish,
overwrite the original failed metrics, or repeat the trimming experiment.

Each run retains full logs, exit status, evaluated properties, restored graphs,
actual final prebundle dependency graph and file inputs, exact package versions,
archive/SBOM/checksums and timing/size metrics. The SBOM uses the candidate's final
graph, including linked inputs, rather than a stale untrimmed graph. Restored
package counts are distinguished from shipped runtime inputs. Release-style
publishes run only on matching GitHub-hosted OS runners. Required PR/main CI has
no added publish job or process matrix.

## Readiness and owner acceptance

The decision is **no-go for distributing trimmed or Native AOT clients**.
Reachable JSON reflection remains in `AgentEnrollmentService`,
`ClientAgentTokenService`, credential and pending-exchange reads/writes in
`AgentCredentialStore`, and signed hash serialization in
`PopSignatureService.ComputeTokenBodyHash`. Terminal control/status, updater
state, task results and remote-support signaling also retain linker warnings.
The diagnostic SDK enables configuration binding generation; no binding warning
was observed, and production binding settings were left unchanged.

Windows scheduled-task launchers `RemoteDesktopUserHelperTask` and
`RemoteSupportConsoleActiveSessionLauncher` retain `Schedule.Service`,
`Activator.CreateInstance` and dynamic built-in COM. Built-in COM is incompatible
with Native AOT; annotations cannot replace it. Media keyframe/bitrate reflection
and native load paths remain separate readiness boundaries.
A successful Linux publish cannot establish Windows reachability or complete
functional compatibility. SIPSorcery 10.0.17 is available on NuGet as checked on
2026-10-08; this PR retains 10.0.16. Its upstream fixes do not establish support
for NetRatel's complete media stack. No Native AOT promotion or media/COM redesign
is part of this change.

The next bounded step is a separate typed auth/storage JSON change. Before
changing any signed request or hash serialization, freeze pre-change UTF-8 body
and hash vectors for enrollment, legacy refresh and refresh-exchange v1,
including order, escaping, null handling, normalization and scope spelling/order.
Verify against those fixed vectors and old-client/new-server compatibility;
signing and verifying with the same changed implementation is insufficient.
Preserve credential/journal formats, then reassess that slice once on hosted
Linux. Broader Windows COM/media work needs its own decision.

Agent initialization, gateway readiness and RSS require an approved test
environment and are not measured here. `--version` is only an artifact launch
check, before normal initialization; fresh and cached extraction are labeled
separately and are not interpreter-startup measurements.

For owner runtime measurements, verify the archived checksums/manifests, then run
A/B/C sequentially through the normal client/service invocation on the same
host/account/configuration and controlled enrolled test gateway. Record process
launch timestamps and `[Client] Env=...` separately from `[Gateway] Presence
admitted`. The first is a configuration checkpoint, not complete local
initialization; the second includes authentication/network latency. Record those
delays separately. Set a fresh
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` for the first run and reuse it for the next run.
Capture idle and representative-work RSS with `ps -o rss= -p PID` on Unix (KiB)
or `(Get-Process -Id PID).WorkingSet64` on Windows (bytes). Capture external
interpreter/helper processes separately. Keep their versions, task contents,
elapsed intervals and gateway behavior with the results.

Before any future trimmed artifact distribution, owner functional acceptance must
cover enrollment and auth renewal, gateway presence/recovery, commands/jobs/library
scripts and legacy compatibility, terminals, logs/service inventory, remote
support/media and update/rollback on all supported RIDs. Build/integrity checks
do not mark these unrun features as passed.
