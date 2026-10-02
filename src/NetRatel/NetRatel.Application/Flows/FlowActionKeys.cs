namespace NetRatel.Application.Flows;

public static class FlowActionKeys
{
    public static string Create(FlowRunLease lease, Guid nodeId) =>
        $"{lease.SourceInstanceId:N}:{lease.Event.OccurrenceId:N}:{lease.Event.EventId:N}:{lease.Version.Id:N}:{nodeId:N}";
}
