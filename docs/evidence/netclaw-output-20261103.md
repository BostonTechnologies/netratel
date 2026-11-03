# NetClaw output regression — 3 November 2026

## Finding

The reported beta.4 symptom is an operator prompt followed by `turn_completed` with no persisted assistant response, including after reload. The original session's raw SignalR events/native transcript were not available in this build environment; the bounded production API log lookup returned no matching entries. The first point of loss in that historical execution cannot be proven from the saved completion alone.

A concrete, reproducible NetRatel rejection was identified by comparing deployed source `68920653` with the upstream **0.28.2** tag, commit `7419d84f7317dae42783d8a9ed6e353f78f961a8`:

- [Native DTO](https://github.com/netclaw-dev/netclaw/blob/0.28.2/src/Netclaw.Actors/Protocol/SessionOutputDto.cs) has nullable `TurnNumber`; ordinary text/delta/tool mapping leaves it null.
- [Native mapper](https://github.com/netclaw-dev/netclaw/blob/0.28.2/src/Netclaw.Actors/Protocol/SessionOutputDtoMapper.cs) sets a numeric turn identity on completion.
- [Native SignalR registration](https://github.com/netclaw-dev/netclaw/blob/0.28.2/src/Netclaw.Daemon/Gateway/SessionHubServiceCollectionExtensions.cs) uses default JSON serialization, retaining null properties.
- Beta.4 tests only whether the property exists before requiring it to equal `beforeSend + 1`. Thus `{ "type": "text", "turnNumber": null }` is rejected, as are normal deltas and tool events when a baseline is known. `{ "type": "turn_completed", "turnNumber": 1, "turnOutcome": "completed" }` passes for baseline zero.
- [RatelDesk's working consumer](https://github.com/BostonTechnologies/rateldesk/blob/v0.1.3-beta.3/src/Helpdesk.Infrastructure/AiAssistant/Chat/AiAssistantChatSessionManager.cs) accepts the same `text`/`text_delta` names and `text` field without treating null as a numbered mismatch.

This establishes a receiver/persistence defect consistent with the live report. It does not establish whether every event in the historical session reached NetRatel.

## Correction and regression coverage

The shared Chat/AI Ops worker distinguishes missing/null identity from a malformed or mismatched number. Session, active-message and daemon-fingerprint fences remain. Completed full text replaces its draft, repeated full-text snapshots are deduplicated, and delta-only output is flushed. A missing reply produces a durable diagnostic, stays unresolved, and keeps the healthy receiver available. A delayed answer can reconcile the observed completion without sending the request again. Intentional quiet completion is visible. Continuation baseline reuse requires the previous recorded terminal's exact message and session identities.

Focused regressions cover both routes, fresh and subsequent turns, nullable text/delta/combined output, duplicate full text and terminal events, delayed terminal ordering, missing output, explicit quiet completion, foreign sessions, stale/malformed numeric identities, restart recovery, and model silence longer than five minutes. Existing restart tests retain snapshot, protocol-limited, cancelled and failed attachment cases. Automated result dispositions remain immutable.

Local validation commands (from the repository root, with the SDK from `global.json` and Docker available):

```sh
dotnet restore NetRatel.sln
dotnet build src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj --configuration Debug --no-restore
dotnet test --project src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj --configuration Debug --no-build --filter-class 'NetRatel.Tests.Netclaw.*' --results-directory work/netclaw-tests --report-trx
tools/ci/validate-layout.sh
```

Release builds, full fast regression receipts and artifact integrity checks run on GitHub-hosted Actions through the existing generic workflows. Distribution integrity and deployed functional acceptance are separate states.

## Pending owner acceptance

Use the beta artifacts with real NetClaw **0.28.2**. In a fresh NetRatel Chat, a fresh AI Ops investigation and RatelDesk, submit:

```text
Diagnostic only. Reply with exactly NETCLAW_PARITY_20261103_OK. Do not use tools, access systems, or make changes.
```

Verify the exact marker is displayed once, survives reload, and is saved before completion. Repeat a distinct no-tools marker on a subsequent turn. Retain bounded native emitted-event metadata and NetRatel `NetclawOutput` logs for correlation; redact contents/secrets before sharing. Check event type, nullable/numeric identity, session/message, text length and rejection/persistence reason.

Test disconnect/recovery and a long authorized tool call separately; never replay a potentially executed action to manufacture a passing result. Confirm missing output is labelled unresolved with a working read-only recovery action. An existing session opened in another console can still divert replies without disconnecting the original transport; that deferred daemon-contract limitation remains [#475](https://github.com/BostonTechnologies/netratel/issues/475). Track the receiver/history findings with [#474](https://github.com/BostonTechnologies/netratel/issues/474) and [#477](https://github.com/BostonTechnologies/netratel/issues/477). Keep these defects open until deployed acceptance is recorded.
