# Execution-source AI diagnostic policy boundary

This report records source inspection and the NetRatel implementation boundary for #590/#591. It does not claim a deployed Netclaw, model or customer execution result.

## Verified native contract

The existing integration documents [tenant and tool authority](../integrations/netclaw-http-mcp.md#isolate-customer-signalr-sessions) and [AI operations](../integrations/netclaw-ai-operations.md). The inspected native release is Netclaw **0.28.2**, commit `7419d84f7317dae42783d8a9ed6e353f78f961a8`.

- [SessionHub](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Daemon/Gateway/SessionHub.cs) exposes Create/Ensure/AttachSession, SendMessage and RespondToInteraction. Its session and message methods accept no diagnostic tool policy.
- [SessionRegistry.SendMessageAsync](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Daemon/Gateway/SessionRegistry.cs) constructs SignalR input with `Audience = TrustAudience.Personal`.
- [ToolAudienceProfiles](https://github.com/netclaw-dev/netclaw/blob/7419d84f7317dae42783d8a9ed6e353f78f961a8/src/Netclaw.Configuration/ToolAudienceProfiles.cs) supplies real daemon tool/filesystem policy. Personal's shipped defaults permit all tools/MCP servers and file access. An operator can configure another policy; this inspection does not establish the policy of any deployed daemon.
- The native text-admission acknowledgement and session history are evidence about a message/session, not proof that write tools, shell execution or MCP mutation are disabled.

Selected clients, prompts, device names and local labels do not enforce a restricted native tool scope. Rejecting a tool output after it arrives cannot prevent an earlier side effect.

## Implemented NetRatel boundary

The execution-source worker validates the saved tenant, run, node, action key, investigation, independently allocated message, original target and pinned connector profile. It rechecks current publisher/credential grants and uses the shared transactional execution-source admission seam; it does not require a fabricated Monitoring occurrence or the expired worker lease. An operator approval cannot substitute for the original execution authority.

The `IFlowInvestigationToolPolicy` adapter contract permits a source turn only after actual diagnostic-only enforcement for that exact profile, session and saved turn. The current native adapter returns `netclaw-investigation-tool-policy-unavailable` without invoking a client method. The worker persists an immutable **Unsupported** AI result before any `SendMessage`, records that no turn started, and produces no findings. Existing schema-one alert/manual paths retain their previous protocol, message identity and policy behavior.

Future support requires an actual enforceable daemon/session contract or equivalent verified restricted execution boundary. No hypothetical RPC, prompt assertion, administrator fallback or checkbox is treated as that proof. This repository campaign does not change daemon/production configuration or deploy another service.

Configured permitted repair and rerun graph actions use the canonical keyed services independently. Their actual terminal events determine outcomes; a completed AI turn, accepted execution or successful repair command alone never verifies recovery. An unavailable investigation can follow an explicit unresolved incident path carrying original failure evidence.

## Validation scope

Owning tests cover source-specific tenant/publisher/credential attenuation, exact saved turn and target correlation, yielded-lease transactional admission, unavailable/denied source results, immutable duplicate outcomes and zero AI sends. Repository CI must run these tests; this report records no local test pass because the command workspace was unavailable.

Full AI investigation -> permitted repair -> linked rerun acceptance remains dependent on an enforceable remote diagnostic policy. Incident enrichment, pinned canonical actions and other independent acceptance must continue; this report does not close #590 or #591.
