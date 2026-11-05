using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowExecutionPreviewSetupTests : AsyncBunitContext
{
    private static readonly Guid TriggerId = Guid.Parse("f8406301-245f-4d64-9c6f-d08abed159bd");
    private static readonly Guid ExternalId = Guid.Parse("e66a28bf-432a-40b8-9e4d-8a7898d01d3d");
    private static readonly Guid AgentId = Guid.Parse("32762f91-1253-45e5-9324-87714ea115be");

    [Fact]
    public async Task Execution_sample_retains_decimal_string_ids_and_configured_source_facts()
    {
        FlowExecutionDryRunRequest? sent = null;
        var graph = Graph();
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a")
            .Add(c => c.Preview, request => sent = request));
        cut.Find("[data-testid=flow-sample-definition-id]").GetAttribute("value").Should().Be("selected-job");
        cut.Find("[data-testid=flow-sample-agent-id]").GetAttribute("value").Should().Be(AgentId.ToString());
        await Change(cut, "flow-sample-execution-id", "9223372036854775807");
        await Change(cut, "flow-sample-attempt-id", "attempt-2");
        await Change(cut, "flow-sample-outcome", FlowExecutionOutcome.Succeeded.ToString());
        await Submit(cut);

        sent.Should().NotBeNull();
        sent!.Graph.Should().BeSameAs(graph);
        sent.Alert.Should().BeNull(); sent.Fixtures.Should().BeNull();
        var input = sent.Input!;
        input.TenantId.Should().Be(17); input.SourceKind.Should().Be(FlowExecutionSourceKind.Job);
        input.Definition.Id.Should().Be("selected-job"); input.Target.AgentId.Should().Be(AgentId);
        input.ExecutionId.Should().Be("9223372036854775807"); input.AttemptId.Should().Be("attempt-2");
        input.Outcome.Should().Be(FlowExecutionOutcome.Succeeded); input.RawStatus.Should().Be("Completed");
        input.RequestKind.Should().BeNull(); input.Replay.Should().BeNull();
        input.Provenance.Should().Be("explicit-preview-sample");
        FlowExecutionEventValidator.Validate(input).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task Request_only_timeout_sample_has_no_invented_job_or_replay()
    {
        FlowExecutionDryRunRequest? sent = null;
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, Graph(FlowExecutionSourceKind.Request, agent: false))
            .Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a").Add(c => c.Preview, request => sent = request));
        await Change(cut, "flow-sample-request-kind", "Admission");
        await Change(cut, "flow-sample-outcome", FlowExecutionOutcome.TimedOut.ToString());
        await Submit(cut);
        sent!.Input!.SourceKind.Should().Be(FlowExecutionSourceKind.Request);
        sent.Input.RequestKind.Should().Be("Admission");
        sent.Input.Outcome.Should().Be(FlowExecutionOutcome.TimedOut);
        sent.Input.RawStatus.Should().Be("TimedOut");
        sent.Input.Target.AgentId.Should().BeNull();
        sent.Input.ParentExecutionId.Should().BeNull(); sent.Input.Replay.Should().BeNull();
        sent.Alert.Should().BeNull();
    }

    [Fact]
    public async Task External_results_distinguish_absent_fixture_missing_null_and_supplied_outputs()
    {
        var sent = new List<FlowExecutionDryRunRequest>();
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, Graph(external: true))
            .Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a").Add(c => c.Preview, request => sent.Add(request)));
        await Submit(cut);
        sent[^1].Fixtures.Should().BeNull();
        await Change(cut, "flow-sample-fixture-enabled", true);
        await Change(cut, "flow-sample-fixture-outcome", FlowNodeCatalogue.Unknown);
        await Submit(cut);
        sent[^1].Fixtures!.Single().Outcome.Should().Be(FlowNodeCatalogue.Unknown);
        sent[^1].Fixtures!.Single().Output.ValueKind.Should().Be(JsonValueKind.Undefined);
        await Change(cut, "flow-sample-fixture-output", "null");
        await Submit(cut);
        sent[^1].Fixtures!.Single().Output.ValueKind.Should().Be(JsonValueKind.Null);
        await Change(cut, "flow-sample-fixture-output", "{\"executionId\":\"9223372036854775807\",\"summary\":\"Synthetic result\"}");
        await Submit(cut);
        var output = sent[^1].Fixtures!.Single().Output;
        output.GetProperty("executionId").GetString().Should().Be("9223372036854775807");
        output.GetProperty("summary").GetString().Should().Be("Synthetic result");
        sent.Should().HaveCount(4);
    }


    [Fact]
    public async Task Enabling_a_sample_requires_an_explicit_outcome_instead_of_defaulting_to_success()
    {
        var sent = new List<FlowExecutionDryRunRequest>();
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, Graph(external: true))
            .Add(c => c.TenantId, 17).Add(c => c.ContextKey, "draft-a").Add(c => c.Preview, request => sent.Add(request)));
        await Change(cut, "flow-sample-fixture-enabled", true);
        cut.Find("[data-testid=flow-sample-fixture-outcome]").GetAttribute("value").Should().Be("");
        await Submit(cut);
        sent.Should().BeEmpty();
        cut.Find("[data-testid=flow-sample-error]").TextContent.Should().Contain("Choose the simulated outcome").And.Contain("Sample external job");
        await Change(cut, "flow-sample-fixture-outcome", FlowNodeCatalogue.Denied);
        await Submit(cut);
        sent.Should().ContainSingle();
        sent[0].Fixtures!.Single().Outcome.Should().Be(FlowNodeCatalogue.Denied);
        sent[0].Fixtures!.Single().Output.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task Invalid_or_oversized_fixture_json_does_not_submit_a_preview()
    {
        var calls = 0;
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, Graph(external: true))
            .Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a").Add(c => c.Preview, _ => calls++));
        await Change(cut, "flow-sample-fixture-enabled", true);
        await Change(cut, "flow-sample-fixture-outcome", FlowNodeCatalogue.Succeeded);
        await Change(cut, "flow-sample-fixture-output", "{broken");
        await Submit(cut);
        calls.Should().Be(0);
        cut.Find("[data-testid=flow-sample-error]").TextContent.Should().Contain("valid bounded JSON");
        await Change(cut, "flow-sample-fixture-output", JsonSerializer.Serialize(new { summary = new string('界', 9000) }));
        await Submit(cut);
        calls.Should().Be(0);
        cut.Find("[data-testid=flow-sample-error]").TextContent.Should().Contain("byte limit");
    }

    [Fact]
    public async Task Tenant_change_discards_previous_sample_fixtures_and_identity()
    {
        var sent = new List<FlowExecutionDryRunRequest>();
        var graph = Graph(external: true);
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a")
            .Add(c => c.Preview, request => sent.Add(request)));
        await Change(cut, "flow-sample-execution-id", "old-tenant-id");
        await Change(cut, "flow-sample-fixture-enabled", true);
        await Change(cut, "flow-sample-fixture-outcome", FlowNodeCatalogue.Succeeded);
        await Change(cut, "flow-sample-fixture-output", "{\"summary\":\"Old sample\"}");
        await Submit(cut);
        cut.Render(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 23).Add(c => c.ContextKey, "tenant-23-draft-a").Add(c => c.Preview, request => sent.Add(request)));
        await Submit(cut);
        sent[1].Input!.TenantId.Should().Be(23);
        sent[1].Input!.ExecutionId.Should().NotBe("old-tenant-id");
        sent[1].Input!.EventId.Should().NotBe(sent[0].Input!.EventId);
        sent[1].Fixtures.Should().BeNull();
        cut.Find("[data-testid=flow-sample-fixture-output]").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task Monitoring_sample_uses_monitoring_payload_and_rejects_nonfinite_values()
    {
        FlowExecutionDryRunRequest? sent = null;
        var source = new FlowNodeDto(TriggerId, FlowNodeKind.AlertRaised, "Synthetic alert", new(0, 0));
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "End", new(300, 0));
        var graph = new FlowGraphDto(2, [source, finish], [new(TriggerId, "event", finish.Id, "in") { Id = Guid.NewGuid() }], new());
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "tenant-17-draft-a")
            .Add(c => c.Preview, request => sent = request));
        await Change(cut, "flow-sample-value", "NaN");
        await Submit(cut);
        sent.Should().BeNull();
        cut.Find("[data-testid=flow-sample-error]").TextContent.Should().Contain("finite");
        await Change(cut, "flow-sample-value", "97.5");
        await Submit(cut);
        sent!.Input.Should().BeNull(); sent.Alert.Should().NotBeNull();
        sent.Alert!.NumericValue.Should().Be(97.5);
        sent.Fixtures.Should().BeNull();
    }


    [Fact]
    public async Task Same_editor_context_keeps_last_sample_across_layout_changes_and_reconciles_fixture_nodes()
    {
        var sent = new List<FlowExecutionDryRunRequest>();
        var graph = Graph(external: true);
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 17)
            .Add(c => c.ContextKey, "draft-a").Add(c => c.Preview, request => sent.Add(request)));
        await Change(cut, "flow-sample-execution-id", "sample-to-keep");
        await Change(cut, "flow-sample-fixture-enabled", true);
        await Change(cut, "flow-sample-fixture-outcome", FlowNodeCatalogue.Succeeded);
        await Change(cut, "flow-sample-fixture-output", "{\"summary\":\"Retained sample\"}");
        await Submit(cut);
        var moved = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Id == ExternalId ? node with { Name = "Renamed sample job", Position = new(450, 200) } : node).ToArray()
        };
        cut.Render(p => p.Add(c => c.Graph, moved).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "draft-a")
            .Add(c => c.Preview, request => sent.Add(request)));
        await Submit(cut);
        sent[1].Input!.ExecutionId.Should().Be("sample-to-keep");
        sent[1].Input!.EventId.Should().Be(sent[0].Input!.EventId);
        sent[1].Fixtures!.Single().Output.GetProperty("summary").GetString().Should().Be("Retained sample");
        cut.Find("summary").TextContent.Should().Contain("Renamed sample job");

        cut.Render(p => p.Add(c => c.Graph, moved).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "draft-b")
            .Add(c => c.Preview, request => sent.Add(request)));
        await Submit(cut);
        sent[2].Input!.ExecutionId.Should().NotBe("sample-to-keep");
        sent[2].Input!.EventId.Should().NotBe(sent[1].Input!.EventId);
        sent[2].Fixtures.Should().BeNull();
    }


    [Theory]
    [InlineData(FlowExecutionSourceKind.Job)]
    [InlineData(FlowExecutionSourceKind.Script)]
    public async Task Request_sample_keeps_request_source_and_selected_backing_definition_family(FlowExecutionSourceKind family)
    {
        FlowExecutionDryRunRequest? sent = null;
        var original = Graph(FlowExecutionSourceKind.Request, agent: false);
        var graph = original with { Nodes = original.Nodes.Select(node => node.Id == TriggerId ? node with
        {
            ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
            { DefinitionReferences = [new(family, "selected-backed-definition")], RequestKinds = [family == FlowExecutionSourceKind.Job ? "Job" : "Script"] }
        } : node).ToArray() };
        var cut = Render<FlowExecutionPreviewSetup>(p => p.Add(c => c.Graph, graph).Add(c => c.TenantId, 17).Add(c => c.ContextKey, "request-source")
            .Add(c => c.Preview, request => sent = request));
        cut.Find("[data-testid=flow-sample-definition-id]").GetAttribute("value").Should().Be("selected-backed-definition");
        await Submit(cut);
        sent!.Input!.SourceKind.Should().Be(FlowExecutionSourceKind.Request);
        sent.Input.DefinitionSourceKind.Should().Be(family);
        sent.Input.Definition.Id.Should().Be("selected-backed-definition");
        sent.Input.Replay.Should().BeNull(); sent.Input.ParentExecutionId.Should().BeNull();
        sent.Alert.Should().BeNull();
        FlowExecutionEventMatching.Matches(17, graph.Nodes[0].ExecutionTrigger!, sent.Input, DateTimeOffset.MinValue).Should().BeTrue();
    }

    private static FlowGraphDto Graph(FlowExecutionSourceKind source = FlowExecutionSourceKind.Job, bool agent = true, bool external = false)
    {
        var trigger = new FlowNodeDto(TriggerId, FlowNodeKind.ExecutionEvent, "Synthetic execution", new(0, 0))
        {
            ExecutionTrigger = new([source], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
            {
                DefinitionReferences = [new(source, source == FlowExecutionSourceKind.Job ? "selected-job" : "selected-source")],
                AgentIds = agent ? [AgentId] : null
            }
        };
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "End", new(600, 0));
        var nodes = new List<FlowNodeDto> { trigger, finish };
        var edges = new List<FlowEdgeDto>();
        if (external)
        {
            nodes.Insert(1, new(ExternalId, FlowNodeKind.RunJob, "Sample external job", new(300, 0)));
            edges.Add(new(TriggerId, "event", ExternalId, "in") { Id = Guid.NewGuid() });
            foreach (var outcome in new[] { "succeeded", "failed", "timed_out", "cancelled", "unknown", "unsupported", "denied" })
                edges.Add(new(ExternalId, outcome, finish.Id, "in") { Id = Guid.NewGuid() });
        }
        else edges.Add(new(TriggerId, "event", finish.Id, "in") { Id = Guid.NewGuid() });
        return new(2, nodes, edges, new());
    }
    private static Task Change(IRenderedComponent<FlowExecutionPreviewSetup> cut, string id, object value) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=" + id + "]").ChangeAsync(new() { Value = value }));
    private static Task Submit(IRenderedComponent<FlowExecutionPreviewSetup> cut) =>
        cut.InvokeAsync(() => cut.Find("[data-testid=flow-execution-preview]").ClickAsync(new()));
}
