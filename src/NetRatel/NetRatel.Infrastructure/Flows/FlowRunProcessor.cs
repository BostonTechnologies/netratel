using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>A bounded single-node backend worker. Every external step passes current grants/suppression and durable receipt fences.</summary>
public sealed class FlowRunProcessor(IFlowExecutionStore store, IFlowRuntimeAdapter runtime, IServiceScopeFactory scopes)
{
    private readonly Guid _workerId = Guid.NewGuid();
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        var lease = await store.ClaimAsync(_workerId, cancellationToken).ConfigureAwait(false); if (lease is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(FlowLimits.ExecutionTimeout);
        FlowRuntimeResult result;
        try { result = await runtime.ExecuteAsync(lease, (draft, token) => ExecuteActionAsync(lease, draft, token), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = new(FlowRunStatus.Failed, "execution-cancelled"); }
        catch (Exception) { result = new(FlowRunStatus.Failed, "execution-failed"); }
        // Receipt persistence can finish after an execution timeout; an expired/reclaimed lease still rejects it.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.CompleteRunAsync(lease, result, completion.Token).ConfigureAwait(false); return true;
    }

    private async Task<FlowIncidentActionResult> ExecuteActionAsync(FlowRunLease lease, FlowIncidentActionDraft draft, CancellationToken ct)
    {
        var existing = await store.GetOrCreateActionAsync(lease, draft, ct).ConfigureAwait(false);
        if (existing is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        if (Terminal(existing) is { } terminal) return terminal;
        await using var scope = scopes.CreateAsyncScope(); var provider = scope.ServiceProvider;
        var gate = await AdmitDispatchAsync(provider, lease, ct).ConfigureAwait(false);
        if (!gate.Allowed)
        {
            var denied = new FlowIncidentActionResult(existing.Status == FlowActionStatus.Dispatching ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed, SafeCode(gate.Code));
            await store.CompleteActionAsync(lease, draft.ActionNodeId, denied, ct).ConfigureAwait(false); return denied;
        }
        var dispatcher = provider.GetRequiredService<IFlowIncidentActionDispatcher>();
        if (existing.Request is null)
        {
            var preparation = await dispatcher.PrepareAsync(draft, ct).ConfigureAwait(false);
            if (preparation.Status != FlowIncidentPreparationStatus.Ready || preparation.Action is null || !FlowContractValidation.ValidPrepared(preparation.Action, draft))
            {
                var unavailable = new FlowIncidentActionResult(FlowIncidentActionResultKind.Unavailable, SafeCode(preparation.Code ?? "connector-preparation-unavailable"));
                await store.CompleteActionAsync(lease, draft.ActionNodeId, unavailable, ct).ConfigureAwait(false); return unavailable;
            }
            if (!await store.SavePreparedActionAsync(lease, draft.ActionNodeId, preparation.Action, ct).ConfigureAwait(false)) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        }
        // Preparation can await external read-only discovery. Recheck current suppression, enablement
        // and grants after it; StartAction then rechecks the durable lease immediately before send.
        gate = await AdmitDispatchAsync(provider, lease, ct).ConfigureAwait(false);
        if (!gate.Allowed)
        {
            var denied = new FlowIncidentActionResult(existing.Status == FlowActionStatus.Dispatching ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed, SafeCode(gate.Code));
            await store.CompleteActionAsync(lease, draft.ActionNodeId, denied, ct).ConfigureAwait(false); return denied;
        }
        var started = await store.StartActionAsync(lease, draft.ActionNodeId, ct).ConfigureAwait(false);
        if (started is null) return new(FlowIncidentActionResultKind.Failed, "action-lease-lost");
        if (Terminal(started) is { } finished) return finished;
        if (started.Status == FlowActionStatus.RetryWaiting) return new(FlowIncidentActionResultKind.RetryableSafe, "retry-waiting");
        if (started.Status != FlowActionStatus.Dispatching || started.Request is null) return new(FlowIncidentActionResultKind.Failed, "action-state-invalid");
        FlowIncidentActionResult result;
        try { result = await dispatcher.DispatchAsync(started.Request, ct).ConfigureAwait(false); }
        // Once durable Dispatching is committed, an interrupted call may have committed remotely.
        catch (OperationCanceledException) { result = new(FlowIncidentActionResultKind.DeliveryUnknown, "delivery-interrupted"); }
        catch (Exception) { result = new(FlowIncidentActionResultKind.DeliveryUnknown, "delivery-transport-unknown"); }
        if (existing.Status == FlowActionStatus.Dispatching && result.Kind is not (FlowIncidentActionResultKind.Succeeded or FlowIncidentActionResultKind.DeliveryUnknown))
            result = new(FlowIncidentActionResultKind.DeliveryUnknown, "interrupted-delivery-unresolved");
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!await store.CompleteActionAsync(lease, draft.ActionNodeId, result, completion.Token).ConfigureAwait(false)) return new(FlowIncidentActionResultKind.DeliveryUnknown, "action-receipt-not-committed");
        return result;
    }
    private static async Task<FlowDispatchDecision> AdmitDispatchAsync(IServiceProvider provider, FlowRunLease lease, CancellationToken ct)
    {
        var flow = await provider.GetRequiredService<IFlowDefinitionService>().GetAsync(lease.Event.TenantId, lease.Version.FlowId, ct).ConfigureAwait(false);
        if (flow is not { Enabled: true }) return new(false, "flow-disabled");
        if (!await provider.GetRequiredService<IFlowExecutionAuthorityVerifier>().AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, ct).ConfigureAwait(false)) return new(false, "execution-authority-revoked");
        return await provider.GetRequiredService<IFlowDispatchGuard>().CanDispatchAsync(lease.Event, ct).ConfigureAwait(false);
    }
    private static FlowIncidentActionResult? Terminal(FlowActionExecutionState state) => state.Status switch
    {
        FlowActionStatus.Succeeded => new(FlowIncidentActionResultKind.Succeeded, state.Code ?? "incident-created", state.Receipt),
        FlowActionStatus.Failed => new(FlowIncidentActionResultKind.Failed, state.Code ?? "action-failed"),
        FlowActionStatus.DeliveryUnknown => new(FlowIncidentActionResultKind.DeliveryUnknown, state.Code ?? "delivery-unknown"), _ => null
    };
    private static string SafeCode(string code) => code.Length is > 0 and <= 128 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_') ? code : "dispatch-denied";
}

public sealed class FlowRunWorker(FlowRunProcessor processor, IFlowExecutionStore store, TimeProvider clock, ILogger<FlowRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pruneAt = clock.GetUtcNow(); using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            do
            {
                try
                {
                    await processor.ProcessOneAsync(stoppingToken).ConfigureAwait(false);
                    if (clock.GetUtcNow() >= pruneAt) { await store.PruneHistoryAsync(stoppingToken).ConfigureAwait(false); pruneAt = clock.GetUtcNow().AddHours(1); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Flow worker operation failed; persisted lease/evidence will govern recovery."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
