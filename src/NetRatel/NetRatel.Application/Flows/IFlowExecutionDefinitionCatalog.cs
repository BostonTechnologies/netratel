using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public interface IFlowExecutionDefinitionCatalog
{
    Task<FlowExecutionDefinitionsPageDto> ListAsync(int tenantId, FlowExecutionAuthorityDto authority,
        FlowExecutionSourceKind sourceKind, string? search = null, string? afterId = null, int limit = 50,
        CancellationToken cancellationToken = default);
    Task<FlowExecutionActionSettingsDto?> GetAsync(int tenantId, FlowExecutionAuthorityDto authority,
        FlowExecutionSourceKind sourceKind, string definitionId, CancellationToken cancellationToken = default);
}
