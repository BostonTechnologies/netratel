using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowRunProcessor
{
    private async Task<FlowIncidentActionResult> ExecuteNetclawActionAsync(FlowRunLease lease, FlowIncidentActionDraft draft,
        IServiceProvider provider, CancellationToken ct)
    {
        var existing = await store.GetOrCreateActionAsync(lease, draft, ct);
        if (existing is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        var polling = existing.Status is FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval;
        var gate = polling ? new FlowDispatchDecision(true, "ai-result-poll") : await AdmitDispatchAsync(provider, lease, ct);
        if (!gate.Allowed)
        {
            var denied = new FlowIncidentActionResult(FlowIncidentActionResultKind.Failed, SafeCode(gate.Code))
            { AiResult = new(lease.Event.TenantId, lease.RunId, lease.Version.Id, draft.ActionNodeId, Guid.Empty,
                lease.Event.EventId, FlowAiResultStatus.Cancelled, provider.GetRequiredService<TimeProvider>().GetUtcNow(), provider.GetRequiredService<TimeProvider>().GetUtcNow()) };
            await store.CompleteActionAsync(lease, draft.ActionNodeId, denied, ct); return denied;
        }
        var started = polling ? existing : await store.StartActionAsync(lease, draft.ActionNodeId, ct);
        if (started is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        if (Terminal(started) is { } completed) return completed;
        if (started.Status is not (FlowActionStatus.Dispatching or FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval)) return new(FlowIncidentActionResultKind.Failed, "action-state-invalid");
        FlowIncidentActionResult result;
        try { result = await provider.GetRequiredService<NetclawFlowConnector>().DispatchAsync(draft, ct); }
        catch (Exception)
        {
            var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
            result = new(FlowIncidentActionResultKind.DeliveryUnknown, "ai-investigation-admission-unconfirmed")
            { AiResult = new(lease.Event.TenantId, lease.RunId, lease.Version.Id, draft.ActionNodeId,
                existing.AiResult?.InvestigationId ?? Guid.Empty, lease.Event.EventId, FlowAiResultStatus.DeliveryUnknown, now, now) };
        }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await store.CompleteActionAsync(lease, draft.ActionNodeId, result, completion.Token) ? result
            : new(FlowIncidentActionResultKind.DeliveryUnknown, "ai-investigation-receipt-unconfirmed");
    }
}
