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
        var gate = await AdmitDispatchAsync(provider, lease, ct);
        if (!gate.Allowed)
        {
            var denied = new FlowIncidentActionResult(FlowIncidentActionResultKind.Failed, SafeCode(gate.Code));
            await store.CompleteActionAsync(lease, draft.ActionNodeId, denied, ct); return denied;
        }
        var started = await store.StartActionAsync(lease, draft.ActionNodeId, ct);
        if (started is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        if (Terminal(started) is { } completed) return completed;
        if (started.Status != FlowActionStatus.Dispatching) return new(FlowIncidentActionResultKind.Failed, "action-state-invalid");
        FlowIncidentActionResult result;
        try { result = await provider.GetRequiredService<NetclawFlowConnector>().DispatchAsync(draft, ct); }
        catch (Exception) { result = new(FlowIncidentActionResultKind.DeliveryUnknown, "ai-investigation-admission-unconfirmed"); }
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await store.CompleteActionAsync(lease, draft.ActionNodeId, result, completion.Token) ? result
            : new(FlowIncidentActionResultKind.DeliveryUnknown, "ai-investigation-receipt-unconfirmed");
    }
}
