using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowGraphV2ContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string LegacyPublishedFixture = """{"schemaVersion":1,"nodes":[{"id":"11111111-1111-1111-1111-111111111111","kind":1,"name":"Alert raised","position":{"x":60,"y":160},"condition":null,"mapping":null,"connectorId":null,"connectorRevision":null},{"id":"22222222-2222-2222-2222-222222222222","kind":3,"name":"Map alert fields","position":{"x":350,"y":160},"condition":null,"mapping":{"titleTemplate":"{ruleName}","descriptionTemplate":"{value}","priority":null},"connectorId":null,"connectorRevision":null},{"id":"33333333-3333-3333-3333-333333333333","kind":4,"name":"Create incident","position":{"x":640,"y":160},"condition":null,"mapping":null,"connectorId":"44444444-4444-4444-4444-444444444444","connectorRevision":7}],"edges":[{"sourceNodeId":"11111111-1111-1111-1111-111111111111","sourcePort":"out","targetNodeId":"22222222-2222-2222-2222-222222222222","targetPort":"in"},{"sourceNodeId":"22222222-2222-2222-2222-222222222222","sourcePort":"out","targetNodeId":"33333333-3333-3333-3333-333333333333","targetPort":"in"}],"viewport":{"scale":1,"scrollX":0,"scrollY":0,"negativeX":0,"negativeY":0,"positiveX":0,"positiveY":0}}""";

    [Fact]
    public void Historical_published_graph_retains_exact_wire_bytes_with_all_new_fields_omitted()
    {
        var graph = JsonSerializer.Deserialize<FlowGraphDto>(LegacyPublishedFixture, Json)!;
        FlowGraphValidator.ValidatePublished(graph).Valid.Should().BeTrue();
        JsonSerializer.Serialize(graph, Json).Should().Be(LegacyPublishedFixture);
        graph.Nodes.Should().OnlyContain(node => node.ExecutionTrigger == null && node.ExecutionAction == null &&
            node.Rerun == null && node.Predicate == null && node.Inputs == null);
        graph.Edges.Should().OnlyContain(edge => edge.Id == null && edge.Route == null);
    }

    [Fact]
    public void Existing_managed_MCP_flow_operations_keep_their_schema_one_contract_while_v2_execution_is_gated()
    {
        var schema = NetRatelMcpToolDefinitions.CreateForHttp("dev")
            .Single(tool => tool.ProtocolTool.Name == "netratel_flows").ProtocolTool.InputSchema.GetRawText();
        schema.Should().Contain("InvestigateWithAi").And.Contain("actions").And.Contain("connectorRevision")
            .And.NotContain("executionTrigger").And.NotContain("executionAction").And.NotContain("predicate")
            .And.NotContain("RunLibraryScript").And.NotContain("RerunOriginalExecution");
        // Existing frozen catalog digest tests still verify exact bytes on both transports.
    }

    [Fact]
    public void Managed_schema_one_ingress_rejects_schema_two_even_when_new_fields_have_been_stripped()
    {
        var descriptor = ManagedOperationCatalog.Find("netratel_flows", "validate")!;
        var stripped = new FlowGraphDto(2, [], [], new());
        var request = JsonSerializer.SerializeToElement(new { tenantId = 17, data = stripped }, ManagedOperationCatalog.JsonOptions);
        ManagedOperationCatalog.TryValidate(descriptor, request, out var error).Should().BeFalse();
        error.Should().Contain("schema one");
        var legacy = JsonSerializer.Deserialize<FlowGraphDto>(LegacyPublishedFixture, Json)!;
        ManagedOperationCatalog.TryValidate(descriptor,
            JsonSerializer.SerializeToElement(new { tenantId = 17, data = legacy }, ManagedOperationCatalog.JsonOptions), out _).Should().BeTrue();
    }

    [Fact]
    public void Schema_two_is_opt_in_and_old_draft_readers_reject_the_new_catalogue_and_layout()
    {
        var v2 = RecoveryGraph();
        FlowGraphV2Validator.ValidatePublished(v2).Valid.Should().BeTrue();
        FlowGraphValidator.ValidateDraft(v2).Issues.Should().Contain(issue => issue.Code == "graph-schema");
        var v1 = JsonSerializer.Deserialize<FlowGraphDto>(LegacyPublishedFixture, Json)!;
        FlowGraphValidator.ValidateDraft(v1 with { Nodes = [v1.Nodes[0] with { Kind = FlowNodeKind.ExecutionEvent }] })
            .Issues.Should().Contain(issue => issue.Code == "node-kind");
        FlowGraphValidator.ValidateDraft(v1 with { Edges = [v1.Edges[0] with { Id = Guid.NewGuid() }] })
            .Issues.Should().Contain(issue => issue.Code == "edge-schema");
    }

    [Fact]
    public void Exclusive_outcomes_can_converge_and_repeated_action_kinds_keep_distinct_stable_ids()
    {
        var graph = RecoveryGraph();
        var rerun = graph.Nodes.Single(node => node.Kind == FlowNodeKind.RerunOriginalExecution);
        var original = JsonSerializer.Serialize(graph, Json);
        var duplicateKind = graph.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript) with
            { Id = Guid.NewGuid(), Name = "Second approved repair", Position = new(700, 100) };
        var succeeded = graph.Edges.Single(edge => edge.SourceNodeId == duplicateKind.Id ||
            edge.SourceNodeId == graph.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript).Id &&
            edge.SourcePort == FlowNodeCatalogue.Succeeded);
        graph = graph with
        {
            Nodes = [.. graph.Nodes, duplicateKind],
            Edges = [.. graph.Edges.Select(edge => edge == succeeded ? edge with { TargetNodeId = duplicateKind.Id } : edge),
                .. Outcomes(duplicateKind.Id, rerun.Id)]
        };
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().BeTrue();
        JsonSerializer.Serialize(JsonSerializer.Deserialize<FlowGraphDto>(original, Json), Json).Should().Be(original);
    }

    [Theory]
    [InlineData("cycle", "cycle")]
    [InlineData("fanout", "outcome-fanout")]
    [InlineData("port", "typed-port")]
    [InlineData("edge-id", "edge-id")]
    [InlineData("route", "edge-route")]
    [InlineData("missing", "outcome-path-unknown")]
    [InlineData("mapping", "mapping")]
    [InlineData("input-type", "input-type")]
    [InlineData("required-input", "input-required")]
    [InlineData("bound-target", "job-bound-target-required")]
    public void Invalid_branches_layout_and_bindings_have_node_or_edge_scoped_errors(string fault, string code)
    {
        var graph = RecoveryGraph();
        var script = graph.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript);
        var first = graph.Edges[0];
        graph = fault switch
        {
            "cycle" => graph with { Edges = [.. graph.Edges, Edge(script.Id, FlowNodeCatalogue.Succeeded, graph.Nodes[0].Id)] },
            "fanout" => graph with { Edges = [.. graph.Edges, first with { Id = Guid.NewGuid() }] },
            "port" => graph with { Edges = [first with { SourcePort = "shell" }, .. graph.Edges.Skip(1)] },
            "edge-id" => graph with { Edges = [first with { Id = null }, .. graph.Edges.Skip(1)] },
            "route" => graph with { Edges = [first with { Route = new([new(20001, 0)]) }, .. graph.Edges.Skip(1)] },
            "missing" => graph with { Edges = graph.Edges.Where(edge => edge.SourceNodeId != script.Id || edge.SourcePort != FlowNodeCatalogue.Unknown).ToArray() },
            "mapping" => Replace(graph, node => node.Kind == FlowNodeKind.MapIncident, node => node with { Mapping = new("{credentials}", "Description") }),
            "input-type" => Replace(graph, node => node.Id == script.Id, node => node with { Inputs = new Dictionary<string, FlowInputBindingDto> { ["client"] = new(FlowInputSource.Event, FlowDataType.Number, "event.target.displayName") } }),
            "required-input" => Replace(graph, node => node.Id == script.Id, node => node with { Inputs = null }),
            "bound-target" => Replace(graph, node => node.Id == script.Id, node => node with { Kind = FlowNodeKind.RunJob, ExecutionAction = node.ExecutionAction! with { SourceKind = FlowExecutionSourceKind.Job, Target = null } }),
            _ => throw new InvalidOperationException()
        };
        var result = FlowGraphV2Validator.ValidatePublished(graph);
        result.Valid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == code &&
            (issue.NodeId != null || issue.EdgeId != null || fault == "route"));
    }

    [Fact]
    public void Combined_source_selectors_are_qualified_and_execution_conditions_cannot_read_alert_fields()
    {
        var graph = RecoveryGraph();
        var trigger = graph.Nodes.Single(node => node.Kind == FlowNodeKind.ExecutionEvent);
        var selected = trigger.ExecutionTrigger! with
        {
            Sources = [FlowExecutionSourceKind.Job, FlowExecutionSourceKind.Script],
            DefinitionIds = ["42"]
        };
        FlowGraphV2Validator.ValidateDraft(Replace(graph, node => node.Id == trigger.Id,
            node => node with { ExecutionTrigger = selected })).Issues.Should()
            .Contain(issue => issue.Code == "execution-trigger" && issue.NodeId == trigger.Id);
        selected = selected with
        {
            DefinitionIds = null,
            DefinitionReferences = [new(FlowExecutionSourceKind.Job, "42"), new(FlowExecutionSourceKind.Script, "42")]
        };
        FlowGraphV2Validator.ValidateDraft(Replace(graph, node => node.Id == trigger.Id,
            node => node with { ExecutionTrigger = selected })).Valid.Should().BeTrue();
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Incorrect alert condition", new(0, 300),
            Condition: new(FlowValueField.NumericValue, FlowComparison.GreaterThan, 90));
        FlowGraphV2Validator.ValidateDraft(graph with { Nodes = [.. graph.Nodes, condition] }).Issues.Should()
            .Contain(issue => issue.Code == "condition-source" && issue.NodeId == condition.Id);
    }

    [Fact]
    public void A_value_produced_on_only_one_branch_is_unavailable_after_convergence()
    {
        var graph = RecoveryGraph();
        var script = graph.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript);
        var trigger = graph.Nodes.Single(node => node.Kind == FlowNodeKind.ExecutionEvent);
        var mapping = graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Check source", new(200, 400))
        {
            Predicate = new(new(FlowInputSource.Event, FlowDataType.Text, "event.outcome"),
                FlowComparison.Equal, JsonSerializer.SerializeToElement("Failed"))
        };
        graph = graph with
        {
            Nodes = [.. graph.Nodes.Select(node => node.Id == mapping.Id ? node with
            {
                Inputs = new Dictionary<string, FlowInputBindingDto>
                { ["result"] = new(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", script.Id) }
            } : node), condition],
            Edges = [.. graph.Edges.Where(edge => edge.SourceNodeId != trigger.Id),
                Edge(trigger.Id, FlowNodeCatalogue.Event, condition.Id),
                Edge(condition.Id, FlowNodeCatalogue.True, script.Id),
                Edge(condition.Id, FlowNodeCatalogue.False, mapping.Id)]
        };
        FlowGraphV2Validator.ValidatePublished(graph).Issues.Should()
            .Contain(issue => issue.Code == "input-unavailable" && issue.NodeId == mapping.Id);
    }

    [Fact]
    public void Draft_upgrade_is_explicit_and_does_not_rewrite_versions_or_hide_missing_outcome_routes()
    {
        var original = JsonSerializer.Deserialize<FlowGraphDto>(LegacyPublishedFixture, Json)!;
        var serialized = JsonSerializer.Serialize(original, Json);
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Threshold", new(200, 200),
            Condition: new(FlowValueField.NumericValue, FlowComparison.GreaterThan, 90));
        var draft = original with
        {
            Nodes = [original.Nodes[0], condition, .. original.Nodes.Skip(1)],
            Edges = [new(original.Nodes[0].Id, "out", condition.Id, "in"), new(condition.Id, "out", original.Nodes[1].Id, "in"), original.Edges[1]]
        };
        var upgraded = FlowDraftMigration.UpgradeToSchemaTwo(draft);
        upgraded.SchemaVersion.Should().Be(2);
        upgraded.Nodes.Select(node => node.Id).Should().Equal(draft.Nodes.Select(node => node.Id));
        upgraded.Edges.Should().OnlyContain(edge => edge.Id != null);
        FlowGraphV2Validator.ValidateDraft(upgraded).Valid.Should().BeTrue();
        FlowGraphV2Validator.ValidateComplete(upgraded).Issues.Should().Contain(issue => issue.Code == "outcome-path-false");
        FlowDraftMigration.UpgradeToSchemaTwo(upgraded).Should().BeSameAs(upgraded);
        JsonSerializer.Serialize(original, Json).Should().Be(serialized);
    }

    [Fact]
    public void Near_limit_graph_is_bounded_without_relaxing_schema_one_limits()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Selected execution", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(10000, 0));
        var middle = Enumerable.Range(0, 30).Select(index => index < 5
            ? Script("Repair " + index) with { Position = new(250 * index, 0) }
            : new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Enrich " + index, new(250 * index, 0), Mapping: new("{definitionName}", "{executionId}"))).ToArray();
        var sequence = new[] { trigger }.Concat(middle).Append(finish).ToArray();
        var edges = sequence.Zip(sequence.Skip(1)).SelectMany(pair =>
            pair.First.Kind == FlowNodeKind.RunLibraryScript
                ? Outcomes(pair.First.Id, pair.Second.Id)
                : [Edge(pair.First.Id, pair.First.Kind == FlowNodeKind.ExecutionEvent ? FlowNodeCatalogue.Event : FlowNodeCatalogue.Result, pair.Second.Id)]).ToArray();
        var graph = new FlowGraphDto(2, sequence, edges, new());
        graph.Nodes.Should().HaveCount(32);
        graph.Edges.Should().HaveCount(61);
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().BeTrue();
        JsonSerializer.SerializeToUtf8Bytes(graph, Json).Length.Should().BeLessThan(FlowGraphV2Limits.MaximumGraphBytes);
        FlowGraphV2Validator.ValidateDraft(graph with { Nodes = [.. graph.Nodes, finish with { Id = Guid.NewGuid() }] })
            .Issues.Should().Contain(issue => issue.Code == "graph-limit");
        FlowLimits.MaximumNodes.Should().Be(8);
        FlowLimits.MaximumEdges.Should().Be(7);
        FlowLimits.MaximumGraphBytes.Should().Be(32768);
    }

    [Fact]
    public void Secret_references_cannot_be_inserted_in_incident_templates_or_literal_secret_inputs()
    {
        var secret = new FlowInputBindingDto(FlowInputSource.SecretReference, FlowDataType.SecretReference, "credential:synthetic");
        FlowGraphV2Validator.ValidTemplate("{input.password}", 128,
            new Dictionary<string, FlowInputBindingDto> { ["password"] = secret }).Should().BeFalse();
        var graph = RecoveryGraph();
        graph = Replace(graph, node => node.Kind == FlowNodeKind.RunLibraryScript, node => node with
        {
            ExecutionAction = node.ExecutionAction! with { Parameters = [new("password", FlowDataType.SecretReference, true)] },
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["password"] = new(FlowInputSource.Literal, FlowDataType.SecretReference, Literal: JsonSerializer.SerializeToElement("unreviewed")) }
        });
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().BeFalse();
    }


    [Theory]
    [InlineData("SYNTHETIC_ENV", true)]
    [InlineData("_synthetic7", true)]
    [InlineData("credential:synthetic", false)]
    [InlineData("7_SYNTHETIC", false)]
    [InlineData("SYNTHETIC-PASSWORD", false)]
    [InlineData("length-129", false)]
    public void Execution_secret_bindings_are_exact_native_environment_reference_names(string reference, bool valid)
    {
        if (reference == "length-129") reference = new string('A', 129);
        var graph = Replace(RecoveryGraph(), node => node.Kind == FlowNodeKind.RunLibraryScript, node => node with
        {
            ExecutionAction = node.ExecutionAction! with { Parameters = [new("password", FlowDataType.SecretReference, true)] },
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["password"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, reference) }
        });
        FlowExecutionInputNames.IsEnvironmentReferenceName(reference).Should().Be(valid);
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().Be(valid);
    }

    [Theory]
    [InlineData(FlowNodeKind.MapIncident)]
    [InlineData(FlowNodeKind.InvestigateWithAi)]
    public void Unused_secret_bindings_cannot_imply_AI_or_mapping_secret_access(FlowNodeKind kind)
    {
        var graph = RecoveryGraph();
        var map = graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        graph = Replace(graph, node => node.Id == map.Id, node => node with
        {
            Kind = kind, Mapping = kind == FlowNodeKind.MapIncident ? node.Mapping : null,
            ConnectorId = kind == FlowNodeKind.InvestigateWithAi ? Guid.NewGuid() : null,
            ConnectorRevision = kind == FlowNodeKind.InvestigateWithAi ? 1 : null,
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["unused"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "SYNTHETIC_ENV") }
        });
        var validation = FlowGraphV2Validator.ValidateDraft(graph);
        validation.Valid.Should().BeFalse();
        validation.Issues.Should().Contain(issue => issue.Code == "secret-reference-source" && issue.NodeId == map.Id);
    }


    [Theory]
    [InlineData(200, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void Pinned_action_names_accept_native_library_names_with_an_explicit_presentation_bound(int length, bool valid)
    {
        var graph = Replace(RecoveryGraph(), node => node.Kind == FlowNodeKind.RunLibraryScript, node => node with
        {
            ExecutionAction = node.ExecutionAction! with
            { Definition = node.ExecutionAction.Definition with { Name = new string('N', length) } }
        });
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().Be(valid);
    }

    private static FlowGraphDto Replace(FlowGraphDto graph, Func<FlowNodeDto, bool> predicate,
        Func<FlowNodeDto, FlowNodeDto> update) => graph with { Nodes = graph.Nodes.Select(node => predicate(node) ? update(node) : node).ToArray() };

    private static FlowNodeDto Script(string name) => new(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, name, new(300, 100))
    {
        ExecutionAction = new(FlowExecutionSourceKind.Script, new("library-script", "Approved repair", "7", new string('a', 64)))
        { Target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Synthetic endpoint"), PinnedSnapshotReference = "snapshot:synthetic:7",
            Parameters = [new("client", FlowDataType.Text, true)] },
        Inputs = new Dictionary<string, FlowInputBindingDto> { ["client"] = new(FlowInputSource.Event, FlowDataType.Text, "event.target.displayName") }
    };

    private static FlowGraphDto RecoveryGraph()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Job failure", new(0, 100))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
            { DefinitionIds = ["job-definition"] } };
        var script = Script("Approved repair");
        var rerun = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RerunOriginalExecution, "Rerun original execution", new(600, 100)) { Rerun = new() };
        var map = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Failure evidence", new(900, 300),
            Mapping: new("{definitionName}: {outcome}", "{executionId} {reason}"));
        var incident = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.CreateIncident, "Create incident", new(1200, 300),
            ConnectorId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ConnectorRevision: 2);
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Recovered", new(900, 0));
        return new(2, [trigger, script, rerun, map, incident, finish],
            [Edge(trigger.Id, FlowNodeCatalogue.Event, script.Id),
             .. Outcomes(script.Id, map.Id).Select(edge => edge.SourcePort == FlowNodeCatalogue.Succeeded ? edge with { TargetNodeId = rerun.Id } : edge),
             .. Outcomes(rerun.Id, map.Id).Select(edge => edge.SourcePort == FlowNodeCatalogue.Succeeded ? edge with { TargetNodeId = finish.Id } : edge),
             Edge(map.Id, FlowNodeCatalogue.Result, incident.Id)], new());
    }

    private static FlowEdgeDto Edge(Guid source, string outcome, Guid target) => new(source, outcome, target, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() };
    private static IEnumerable<FlowEdgeDto> Outcomes(Guid source, Guid target) =>
        new[] { FlowNodeCatalogue.Succeeded, FlowNodeCatalogue.Failed, FlowNodeCatalogue.TimedOut, FlowNodeCatalogue.Cancelled,
            FlowNodeCatalogue.Unknown, FlowNodeCatalogue.Unsupported, FlowNodeCatalogue.Denied }.Select(port => Edge(source, port, target));
}
