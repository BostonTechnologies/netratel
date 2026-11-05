namespace NetRatel.Application.Flows;

/// <summary>References exact server-owned facts and pinned delivery selection; never accepts browser execution authority.</summary>
public sealed record FlowExecutionEventAdmissionRequest(int TenantId, Guid FlowVersionId, Guid SourceEventId,
    IReadOnlyList<Guid>? ContributingEventIds = null, Guid? DeliveryId = null);

public interface IFlowExecutionEventIngress
{
    Task<FlowIngressResult> EnqueueExecutionAsync(FlowExecutionEventAdmissionRequest request,
        CancellationToken cancellationToken = default);
}
