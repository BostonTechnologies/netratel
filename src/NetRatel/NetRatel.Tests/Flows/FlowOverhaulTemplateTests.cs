using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowOverhaulTemplateTests
{
    public static IEnumerable<object[]> TemplateIds =>
        FlowOverhaulTemplates.Descriptors.Select(template => new object[] { template.Id });

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Templates_are_configurable_drafts_and_need_real_configuration_before_publication(string templateId)
    {
        var graph = FlowOverhaulTemplates.CreateDraft(templateId);
        var draft = graph.SchemaVersion == 1 ? FlowGraphValidator.ValidateDraft(graph) : FlowGraphV2Validator.ValidateDraft(graph);
        Assert.True(draft.Valid, string.Join(", ", draft.Issues.Select(issue => issue.Code)));
        var unconfigured = graph.SchemaVersion == 1 ? FlowGraphValidator.ValidatePublished(graph) : FlowGraphV2Validator.ValidatePublished(graph);
        Assert.False(unconfigured.Valid);
        Assert.All(graph.Nodes, node => Assert.Null(node.ConnectorId));
        Assert.All(graph.Nodes.Where(node => node.Kind is FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript),
            node => Assert.Null(node.ExecutionAction));

        var configured = ConfigureSynthetic(graph);
        var complete = configured.SchemaVersion == 1 ? FlowGraphValidator.ValidatePublished(configured) : FlowGraphV2Validator.ValidatePublished(configured);
        Assert.True(complete.Valid, string.Join(", ", complete.Issues.Select(issue => issue.Code)));
        var bytes = JsonSerializer.Serialize(configured);
        Assert.Equal(bytes, JsonSerializer.Serialize(JsonSerializer.Deserialize<FlowGraphDto>(bytes)));
    }

    [Fact]
    public void Repair_success_requires_the_rerun_and_only_its_terminal_success_reaches_recovered()
    {
        var graph = FlowOverhaulTemplates.CreateDraft(FlowOverhaulTemplates.ScriptRecovery);
        var repair = Assert.Single(graph.Nodes, node => node.Kind == FlowNodeKind.RunLibraryScript);
        var rerun = Assert.Single(graph.Nodes, node => node.Kind == FlowNodeKind.RerunOriginalExecution);
        var recovered = Assert.Single(graph.Nodes, node => node.Kind == FlowNodeKind.Finish);
        Assert.Equal(1, rerun.Rerun!.MaximumAttempts);
        Assert.Equal(rerun.Id, Assert.Single(graph.Edges, edge => edge.SourceNodeId == repair.Id &&
            edge.SourcePort == FlowNodeCatalogue.Succeeded).TargetNodeId);
        Assert.Equal(recovered.Id, Assert.Single(graph.Edges, edge => edge.SourceNodeId == rerun.Id &&
            edge.SourcePort == FlowNodeCatalogue.Succeeded).TargetNodeId);
        Assert.All(graph.Edges.Where(edge => edge.SourceNodeId == rerun.Id && edge.SourcePort != FlowNodeCatalogue.Succeeded),
            edge => Assert.NotEqual(recovered.Id, edge.TargetNodeId));
        Assert.DoesNotContain(graph.Edges, edge => edge.SourceNodeId == repair.Id && edge.TargetNodeId == recovered.Id);
    }

    [Fact]
    public void Request_unknown_and_authoritative_timeout_keep_distinct_messages_and_never_rerun()
    {
        var graph = FlowOverhaulTemplates.CreateDraft(FlowOverhaulTemplates.RequestTimeoutUnknown);
        var source = Assert.Single(graph.Nodes, node => node.Kind == FlowNodeKind.ExecutionEvent);
        Assert.Contains(FlowExecutionOutcome.Unknown, source.ExecutionTrigger!.Outcomes);
        Assert.Contains(FlowExecutionOutcome.TimedOut, source.ExecutionTrigger.Outcomes);
        Assert.DoesNotContain(graph.Nodes, node => node.Kind is FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or
            FlowNodeKind.RerunOriginalExecution or FlowNodeKind.InvestigateWithAi);
        var condition = Assert.Single(graph.Nodes, node => node.Kind == FlowNodeKind.Condition);
        Assert.Equal("TimedOut", condition.Predicate!.Value.GetString());
        var mappingIds = graph.Edges.Where(edge => edge.SourceNodeId == condition.Id).Select(edge => edge.TargetNodeId).ToArray();
        Assert.Equal(2, mappingIds.Distinct().Count());
        Assert.Contains(graph.Nodes, node => node.Mapping?.DescriptionTemplate.Contains("not a definitive failure or timeout", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Template_instances_do_not_share_node_edge_or_action_identity()
    {
        var first = FlowOverhaulTemplates.CreateDraft(FlowOverhaulTemplates.ScriptRecovery);
        var second = FlowOverhaulTemplates.CreateDraft(FlowOverhaulTemplates.ScriptRecovery);
        Assert.Empty(first.Nodes.Select(node => node.Id).Intersect(second.Nodes.Select(node => node.Id)));
        Assert.Empty(first.Edges.Select(edge => edge.Id).Intersect(second.Edges.Select(edge => edge.Id)));
        Assert.Throws<ArgumentException>(() => FlowOverhaulTemplates.CreateDraft("unsupported-template"));
    }

    private static FlowGraphDto ConfigureSynthetic(FlowGraphDto graph) => graph with
    {
        Nodes = graph.Nodes.Select(node =>
        {
            if (node.Kind is FlowNodeKind.CreateIncident or FlowNodeKind.InvestigateWithAi)
                node = node with { ConnectorId = Guid.Parse("11111111-1111-4111-8111-111111111111"), ConnectorRevision = 1 };
            if (node.Kind == FlowNodeKind.ActionGroup)
                node = node with { Actions = [new(Guid.NewGuid(), FlowActionKind.Discord,
                    Guid.Parse("22222222-2222-4222-8222-222222222222"), 1)] };
            if (node.Kind == FlowNodeKind.RunLibraryScript)
                node = node with { ExecutionAction = new(FlowExecutionSourceKind.Script,
                    new("91", "Synthetic approved script", "4", new string('a', 64)))
                    { PinnedSnapshotReference = "synthetic-test-pin", Parameters = [] } };
            if (node.ExecutionTrigger?.Scope == FlowExecutionScope.SelectedDefinitions)
                node = node with { ExecutionTrigger = node.ExecutionTrigger with
                    { DefinitionReferences = [new(FlowExecutionSourceKind.Script, "91")] } };
            return node;
        }).ToArray()
    };
}
