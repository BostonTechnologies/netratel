using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>
/// An Allowed decision requires actual daemon enforcement of diagnostic-only tools for this exact
/// profile, session, investigation and saved turn. Prompt text, local labels and a reported tool call
/// are not enforcement. Configured pinned repairs execute through separate canonical Flow actions.
/// </summary>
public interface IFlowInvestigationToolPolicy
{
    Task<FlowDispatchDecision> EnforceDiagnosticsOnlyAsync(IAiAssistantChatClient client,
        NetclawRuntimeProfile profile, string sessionId, Guid investigationId, Guid messageId,
        FlowIncidentActionDraft draft, CancellationToken cancellationToken = default);
}

/// <summary>
/// Native Netclaw 0.28.2 has no session tool-policy negotiation or proof contract. Its SignalR input
/// uses the Personal audience. No AI turn is safe to start as a diagnostic-only source investigation.
/// </summary>
public sealed class NativeFlowInvestigationToolPolicy : IFlowInvestigationToolPolicy
{
    public const string UnavailableCode = "netclaw-investigation-tool-policy-unavailable";

    public Task<FlowDispatchDecision> EnforceDiagnosticsOnlyAsync(IAiAssistantChatClient client,
        NetclawRuntimeProfile profile, string sessionId, Guid investigationId, Guid messageId,
        FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FlowDispatchDecision(false, UnavailableCode));
    }
}
