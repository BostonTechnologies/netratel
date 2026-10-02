using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public static class FlowServiceRegistration
{
    public static IServiceCollection AddNetRatelFlows(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System); services.TryAddSingleton<FlowPersistenceService>();
        services.TryAddSingleton<IFlowDefinitionService>(provider => provider.GetRequiredService<FlowPersistenceService>());
        services.TryAddSingleton<IFlowEventIngress>(provider => provider.GetRequiredService<FlowPersistenceService>());
        services.TryAddSingleton<IFlowExecutionStore>(provider => provider.GetRequiredService<FlowPersistenceService>());
        services.TryAddSingleton<IFlowRuntimeAdapter, VeloxFlowRuntimeAdapter>(); services.TryAddSingleton<FlowRunProcessor>();
        services.TryAddScoped<IFlowExecutionAuthorityVerifier, FlowCurrentAuthorityVerifier>();
        services.TryAddScoped<IFlowDispatchGuard, UnavailableMonitoringDispatchGuard>();
        services.TryAddScoped<IFlowConnectorCatalog, UnavailableFlowConnector>(); services.TryAddScoped<IFlowIncidentActionDispatcher, UnavailableFlowConnector>();
        return services;
    }
    private sealed class UnavailableMonitoringDispatchGuard : IFlowDispatchGuard
    { public Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default) => Task.FromResult(new FlowDispatchDecision(false, "monitoring-dispatch-unavailable")); }
    private sealed class UnavailableFlowConnector : IFlowConnectorCatalog, IFlowIncidentActionDispatcher
    {
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult<FlowConnectorReferenceDto?>(null);
        public Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FlowConnectorReferenceDto>>([]);
        public Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default) => Task.FromResult(new FlowIncidentPreparationResult(FlowIncidentPreparationStatus.Unavailable, Code: "connector-unavailable"));
        public Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default) => Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Unavailable, "connector-unavailable"));
    }
}
