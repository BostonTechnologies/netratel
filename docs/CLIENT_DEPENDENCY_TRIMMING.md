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
The pinned SDK is 10.0.401. Hosted results and exact cleanup SHA will be recorded
after the assessment and final native comparison.

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

Each run retains full logs, exit status, evaluated properties, restored graphs,
actual final prebundle dependency graph and file inputs, exact package versions,
archive/SBOM/checksums and timing/size metrics. The SBOM uses the candidate's final
graph, including linked inputs, rather than a stale untrimmed graph. Restored
package counts are distinguished from shipped runtime inputs. Release-style
publishes run only on matching GitHub-hosted OS runners. Required PR/main CI has
no added publish job or process matrix.

## Readiness and owner acceptance

Trimming and Native AOT are diagnostic paths only. Remaining concerns include
reflection-based JSON outside established contexts, scheduled-task dynamic COM
in both Windows launchers, media keyframe/bitrate reflection and native load paths.
A successful Linux publish cannot establish Windows reachability or complete
functional compatibility. SIPSorcery 10.0.17 is available on NuGet as checked on
2026-10-08; this PR retains 10.0.16. Its upstream fixes do not establish support
for NetRatel's complete media stack. No Native AOT promotion or media/COM redesign
is part of this change.

Agent initialization, gateway readiness and RSS require an approved test
environment and are not measured here. `--version` is only an artifact launch
check, before normal initialization; fresh and cached extraction are labeled
separately and are not interpreter-startup measurements.

For owner runtime measurements, use the same host/account/configuration and
approved enrolled test gateway for A/B/C. Record process launch time and existing
local startup/configuration log timestamps separately from `Presence admitted`;
record enrollment/token/network delays separately. Set a fresh
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
