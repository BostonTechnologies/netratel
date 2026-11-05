using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Creates an inert, disabled draft with unconfigured execution and connector references.</summary>
public interface IFlowTemplateService
{
    Task<FlowDefinitionWriteResult> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request,
        string actorId, CancellationToken cancellationToken = default);
}
