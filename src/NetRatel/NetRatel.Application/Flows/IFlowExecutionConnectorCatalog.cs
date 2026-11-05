using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Source-aware lookup for a verified execution graph; the schema-one catalogue remains unchanged.</summary>
public interface IFlowExecutionConnectorCatalog
{
    Task<FlowConnectorReferenceDto?> GetForExecutionAsync(int tenantId, Guid connectorId,
        FlowExecutionAuthorityDto authority, IReadOnlyList<FlowExecutionSourceKind> sources,
        CancellationToken cancellationToken = default);
}
