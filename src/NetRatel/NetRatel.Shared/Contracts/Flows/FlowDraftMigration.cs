namespace NetRatel.Shared.Contracts.Flows;

/// <summary>
/// Explicit editable-draft conversion only. It neither rewrites immutable versions nor promises that
/// newly required outcome routes are configured. ValidateComplete reports those routes for author review.
/// </summary>
public static class FlowDraftMigration
{
    public static FlowGraphDto UpgradeToSchemaTwo(FlowGraphDto draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.SchemaVersion == FlowGraphV2Limits.SchemaVersion) return draft;
        if (draft.SchemaVersion != FlowLimits.SchemaVersion || !FlowGraphValidator.ValidateDraft(draft).Valid)
            throw new ArgumentException("A valid schema-one draft is required.", nameof(draft));
        var nodes = draft.Nodes.ToDictionary(node => node.Id);
        return draft with
        {
            SchemaVersion = FlowGraphV2Limits.SchemaVersion,
            Edges = draft.Edges.Select(edge => edge with
            {
                Id = Guid.NewGuid(),
                SourcePort = nodes[edge.SourceNodeId].Kind switch
                {
                    FlowNodeKind.AlertRaised => FlowNodeCatalogue.Event,
                    FlowNodeKind.Condition => FlowNodeCatalogue.True,
                    FlowNodeKind.MapIncident => FlowNodeCatalogue.Result,
                    FlowNodeKind.InvestigateWithAi => FlowNodeCatalogue.Succeeded,
                    _ => edge.SourcePort
                },
                TargetPort = FlowNodeCatalogue.Input
            }).ToArray()
        };
    }
}
