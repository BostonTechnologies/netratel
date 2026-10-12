using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowContractAndRuntimeTests
{
    [Fact]
    public void A_named_incomplete_template_can_be_saved_but_requires_a_connector_to_publish()
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        FlowGraphValidator.ValidateDraft(graph).Valid.Should().BeTrue();
        FlowGraphValidator.ValidateComplete(graph).Issues.Should().Contain(issue => issue.Code == "connector-required");
        graph = FlowTestData.Graph();
        FlowGraphValidator.ValidatePublished(graph).Valid.Should().BeTrue();
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("cycle")]
    [InlineData("fanout")]
    [InlineData("wrong-port")]
    [InlineData("wrong-type")]
    [InlineData("duplicate-id")]
    [InlineData("unknown-template")]
    [InlineData("node-budget")]
    [InlineData("nonfinite")]
    [InlineData("clr-property")]
    public void Untrusted_graphs_cannot_select_arbitrary_types_properties_or_unbounded_execution(string fault)
    {
        var graph = FlowTestData.Graph(); var trigger = graph.Nodes[0]; var map = graph.Nodes[1]; var action = graph.Nodes[2];
        graph = fault switch
        {
            "unknown-kind" => graph with { Nodes = [trigger with { Kind = (FlowNodeKind)999 }, map, action] },
            "cycle" => graph with { Edges = [.. graph.Edges, new(action.Id, "out", trigger.Id, "in")] },
            "fanout" => graph with { Edges = [.. graph.Edges, graph.Edges[0]] },
            "wrong-port" => graph with { Edges = [graph.Edges[0] with { SourcePort = "shell" }, graph.Edges[1]] },
            "wrong-type" => graph with { Edges = [new(trigger.Id, "out", action.Id, "in")] },
            "duplicate-id" => graph with { Nodes = [trigger, map with { Id = trigger.Id }, action] },
            "unknown-template" => graph with { Nodes = [trigger, map with { Mapping = new("{secret}", "description") }, action] },
            "node-budget" => graph with { Nodes = Enumerable.Range(0, 9).Select(_ => trigger with { Id = Guid.NewGuid() }).ToArray() },
            "nonfinite" => graph with { Viewport = new(double.NaN) },
            "clr-property" => graph with { Nodes = [trigger with { Mapping = new("System.Diagnostics.Process", "shell") }, map, action] },
            _ => throw new InvalidOperationException()
        };
        FlowGraphValidator.ValidateDraft(graph).Valid.Should().BeFalse();
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Malformed_UTF16_cannot_change_node_identity_or_templates_after_wire_encoding(int which)
    {
        var graph = FlowTestData.Graph(); var text = new string(which == 0 ? (char)0xD800 : (char)0xDC00, 1);
        FlowGraphValidator.ValidateDraft(graph with { Nodes = [graph.Nodes[0] with { Name = text }, .. graph.Nodes.Skip(1)] }).Valid.Should().BeFalse();
        FlowGraphValidator.ValidTemplate(text, 128).Should().BeFalse();
    }

    [Fact]
    public void Dry_run_maps_explicit_fields_without_runtime_dispatch_or_mutating_the_graph()
    {
        var graph = FlowTestData.Graph(); var serialized = JsonSerializer.Serialize(graph); var input = FlowTestData.EventData("alpha", 90);
        var result = FlowPureEvaluation.DryRun(new(graph, input));
        result.Valid.Should().BeTrue(); result.Code.Should().Be("inert-preview"); result.Incident!.Title.Should().Contain("alpha");
        result.VisitedNodeIds.Should().HaveCount(3); JsonSerializer.Serialize(graph).Should().Be(serialized);
        // Pure evaluation has no runtime/store/dispatcher argument or dependency; repeated reads remain inert.
        FlowPureEvaluation.DryRun(new(graph, input)).Should().BeEquivalentTo(result);
    }
    [Fact]
    public void Typed_condition_false_has_an_explicit_skipped_preview()
    {
        var result = FlowPureEvaluation.DryRun(new(FlowTestData.Graph(withCondition: true), FlowTestData.EventData("low", 10)));
        result.Valid.Should().BeTrue(); result.Skipped.Should().BeTrue(); result.Code.Should().Be("condition-false"); result.Incident.Should().BeNull();
    }

    [Fact]
    public async Task Published_Core_compiler_and_runtime_execute_only_the_reviewed_action_callback()
    {
        var lease = FlowTestData.Lease("actual-core", 90); var calls = 0;
        var result = await new VeloxFlowRuntimeAdapter().ExecuteAsync(lease, (draft, _) =>
        {
            calls++; draft.ActionNodeId.Should().Be(lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident).Id);
            draft.IdempotencyKey.Should().Be(FlowActionKeys.Create(lease, draft.ActionNodeId));
            draft.Fields.Title.Should().Contain("actual-core"); draft.Event.Should().Be(lease.Event);
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created", new("incident-1")));
        });
        result.Status.Should().Be(FlowRunStatus.Succeeded); calls.Should().Be(1);
    }
    [Fact]
    public async Task Condition_false_in_the_actual_Core_runtime_skips_the_external_action()
    {
        var result = await new VeloxFlowRuntimeAdapter().ExecuteAsync(FlowTestData.Lease("condition-false", 10, true), (_, _) => throw new InvalidOperationException("must remain inert"));
        result.Status.Should().Be(FlowRunStatus.Skipped); result.Code.Should().Be("condition-false");
    }
    [Fact]
    public async Task Parallel_real_Core_runs_have_isolated_models_helpers_and_blackboards()
    {
        var adapter = new VeloxFlowRuntimeAdapter(); var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var runs = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var name = $"isolated-{index}"; var lease = FlowTestData.Lease(name, index + 50);
            return await adapter.ExecuteAsync(lease, async (draft, _) =>
            { await Task.Yield(); draft.Fields.Title.Should().Contain(name); draft.Event.Data.NumericValue.Should().Be(index + 50); observed.Add(draft.IdempotencyKey); return new(FlowIncidentActionResultKind.Succeeded, "incident-created", new(name)); });
        }));
        runs.Should().OnlyContain(run => run.Status == FlowRunStatus.Succeeded); observed.Distinct().Should().HaveCount(12);
    }
    [Fact]
    public async Task Runtime_revalidates_tenant_and_configuration_fingerprint_before_constructing_a_model()
    {
        var lease = FlowTestData.Lease("tamper", 90);
        var adapter = new VeloxFlowRuntimeAdapter();
        (await adapter.ExecuteAsync(lease with { Event = lease.Event with { TenantId = 99 } }, (_, _) => throw new InvalidOperationException())).Code.Should().Be("runtime-input-invalid");
        (await adapter.ExecuteAsync(lease with { Version = lease.Version with { ConfigurationHash = new string('0', 64) } }, (_, _) => throw new InvalidOperationException())).Code.Should().Be("runtime-input-invalid");
    }

    [Fact]
    public void Historical_graph_and_action_JSON_omit_new_optional_fields_and_retain_their_fingerprint()
    {
        var graph = FlowTestData.Graph(); var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var historical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            graph.SchemaVersion,
            Nodes = graph.Nodes.Select(node => new { node.Id, node.Kind, node.Name, node.Position, node.Condition, node.Mapping, node.ConnectorId, node.ConnectorRevision }),
            graph.Edges, graph.Viewport
        }, json);
        JsonSerializer.SerializeToUtf8Bytes(graph, json).Should().Equal(historical);
        var lease = FlowTestData.Lease("historical", 90);
        JsonSerializer.Serialize(lease.Event.Data, json).Should().NotContain("offlineSummary");
        var action = FlowGraphValidator.ActionTargets(lease.Version.Graph).Single();
        var draft = new FlowIncidentActionDraft(17, lease.RunId, action.Id, action.ConnectorId!.Value, 1, lease.SourceInstanceId,
            FlowActionKeys.Create(lease, action.Id), lease.Event, new("title", "description"));
        JsonSerializer.Serialize(draft, json).Should().NotContain("\"kind\"");
    }

    [Fact]
    public void Action_groups_reject_duplicate_destinations_and_child_ID_aliases()
    {
        var graph = FlowTestData.GroupGraph(); var group = graph.Nodes.Single(node => node.Kind == FlowNodeKind.ActionGroup);
        FlowGraphValidator.ValidatePublished(graph).Valid.Should().BeTrue();
        var duplicate = group.Actions![0] with { Id = Guid.NewGuid() };
        FlowGraphValidator.ValidateDraft(graph with { Nodes = graph.Nodes.Select(node => node.Id == group.Id
            ? group with { Actions = [.. group.Actions!, duplicate] } : node).ToArray() }).Issues.Should().Contain(issue => issue.Code == "duplicate-action-target");
        FlowGraphValidator.ValidateDraft(graph with { Nodes = graph.Nodes.Select(node => node.Id == group.Id
            ? group with { Actions = [group.Actions![0] with { Id = graph.Nodes[0].Id }] } : node).ToArray() }).Issues.Should().Contain(issue => issue.Code == "action-id");
    }

    [Fact]
    public async Task A_slow_group_target_does_not_prevent_its_siblings_from_starting_and_partial_results_are_visible()
    {
        var lease = FlowTestData.GroupLease(); var started = new System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await new VeloxFlowRuntimeAdapter().ExecuteAsync(lease, async (draft, ct) =>
        {
            started.TryAdd(draft.ActionNodeId, true);
            if (started.Count == 3) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            draft.IdempotencyKey.Should().Be(FlowActionKeys.Create(lease, draft.ActionNodeId));
            return draft.Kind == FlowActionKind.Discord
                ? new(FlowIncidentActionResultKind.Failed, "disabled-target")
                : new(FlowIncidentActionResultKind.Succeeded, "delivered", new("accepted"));
        });
        started.Should().HaveCount(3); result.Status.Should().Be(FlowRunStatus.PartiallySucceeded);
    }

    [Fact]
    public void Offline_events_require_a_valid_explicit_aggregate_and_share_the_bounded_summary_with_existing_templates()
    {
        var input = FlowTestData.EventData("Monitored clients", 3) with
        { AgentId = Guid.Empty, Resource = MonitoringOfflinePresentation.ResourceKey, Metric = "ClientOffline", OfflineSummary = new(3, 8, "hv01, hv02, dns", false) };
        FlowPureEvaluation.ValidEventData(input).Should().BeTrue();
        FlowPureEvaluation.ValidEventData(input with { OfflineSummary = null }).Should().BeFalse();
        FlowPureEvaluation.ValidEventData(input with { Metric = "CpuUsagePercent" }).Should().BeFalse();
        var mapped = FlowPureEvaluation.Map(FlowTestData.Graph().Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Mapping!, input);
        mapped.Description.Should().Contain("3 clients are offline: hv01, hv02, dns.");
    }
}

internal static class FlowTestData
{
    public static readonly Guid ConnectorId = Guid.Parse("c654819a-aa09-45e1-a464-e3e9edc6e325");
    public static readonly Guid DiscordConnectorId = Guid.Parse("15357d3a-0c25-4e61-b4f6-8ce4f5157e96");
    public static readonly Guid SlackConnectorId = Guid.Parse("a008f41f-d546-49c8-9f52-f7258b7a17fb");
    public static readonly FlowExecutionAuthorityDto Authority = new("test-configuring-principal");
    public static FlowGraphDto Graph(bool withCondition = false)
    {
        var graph = FlowGraphTemplates.IncidentFromAlert();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident ? node with { ConnectorId = ConnectorId, ConnectorRevision = 1 } : node).ToArray() };
        if (!withCondition) return graph;
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Numeric condition", new(250, 350), new(FlowValueField.NumericValue, FlowComparison.GreaterThan, 75));
        return graph with { Nodes = [graph.Nodes[0], condition, .. graph.Nodes.Skip(1)],
            Edges = [new(graph.Nodes[0].Id, "out", condition.Id, "in"), new(condition.Id, "out", graph.Nodes[1].Id, "in"), graph.Edges[1]] };
    }
    public static FlowEventDataDto EventData(string name, double value) => new(Guid.NewGuid(), Guid.NewGuid(), "CPU threshold", name, "cpu", "CpuUsagePercent", "Critical", value, null, DateTimeOffset.UtcNow);
    public static FlowGraphDto GroupGraph()
    {
        var graph = Graph();
        return graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident ? node with
        {
            Kind = FlowNodeKind.ActionGroup, Name = "Notify operations", ConnectorId = null, ConnectorRevision = null,
            Actions = [new(Guid.NewGuid(), FlowActionKind.RatelDeskIncident, ConnectorId, 1),
                new(Guid.NewGuid(), FlowActionKind.Discord, DiscordConnectorId, 1), new(Guid.NewGuid(), FlowActionKind.Slack, SlackConnectorId, 1)]
        } : node).ToArray() };
    }
    public static FlowRunLease GroupLease()
    {
        var lease = Lease("group", 90); var graph = GroupGraph();
        return lease with { Version = lease.Version with { Graph = graph,
            ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } };
    }
    public static FlowRunLease Lease(string name, double value, bool condition = false)
    {
        var graph = Graph(condition); var versionId = Guid.NewGuid(); var flowId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var hash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var version = new FlowVersionDto(versionId, flowId, 17, 1, graph, hash, Authority.PrincipalId, now);
        var input = new FlowEventEnvelope(17, Guid.NewGuid(), Guid.NewGuid(), versionId, now, EventData(name, value), Authority);
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, now.AddMinutes(1), Guid.NewGuid(), version, input, 1);
    }
    public static FlowIncidentActionRequest Prepared(FlowIncidentActionDraft draft, bool safeReplay = false)
    {
        var request = new FlowIncidentActionRequest(draft.TenantId, draft.RunId, draft.ActionNodeId, draft.ConnectorId, draft.ConnectorRevision, draft.SourceInstanceId,
            draft.IdempotencyKey, draft.Event, draft.Fields with { Priority = draft.Fields.Priority ?? 2 }, new("organization-17", "customer-17", null, []), safeReplay, "");
        return request with { SemanticFingerprint = FlowContractValidation.Fingerprint(request) };
    }
}
