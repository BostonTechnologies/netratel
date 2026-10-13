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
    public async Task Ai_action_is_inert_in_preview_and_preserves_its_kind_pinned_target_and_stable_action_key_in_execution()
    {
        var original = FlowTestData.Lease("investigate-ai", 94);
        var graph = original.Version.Graph with { Nodes = original.Version.Graph.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident
            ? node with { Kind = FlowNodeKind.InvestigateWithAi, Name = "Investigate with AI" } : node).ToArray() };
        FlowGraphValidator.ValidatePublished(graph).Valid.Should().BeTrue();
        var target = FlowGraphValidator.ActionTargets(graph).Single();
        target.Kind.Should().Be(FlowActionKind.NetclawInvestigation);
        var preview = FlowPureEvaluation.DryRun(new(graph, original.Event.Data));
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("inert-preview");
        var lease = original with { Version = original.Version with { Graph = graph, ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } }; var calls = 0;
        var result = await new VeloxFlowRuntimeAdapter().ExecuteAsync(lease, (draft, _) =>
        {
            calls++; draft.Kind.Should().Be(FlowActionKind.NetclawInvestigation);
            draft.ConnectorId.Should().Be(target.ConnectorId!.Value); draft.ConnectorRevision.Should().Be(target.ConnectorRevision!.Value);
            draft.IdempotencyKey.Should().Be(FlowActionKeys.Create(lease, target.Id));
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "ai-investigation-completed", new("investigation-reference"))
            { AiResult = new(17, lease.RunId, lease.Version.Id, target.Id, Guid.NewGuid(), lease.Event.EventId, FlowAiResultStatus.Completed, lease.Event.OccurredAtUtc, lease.Event.OccurredAtUtc, "Completed evidence.") });
        });
        calls.Should().Be(1); result.Status.Should().Be(FlowRunStatus.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ai_chain_waits_for_one_correlated_completion_and_resumes_only_the_checkpoint_path(bool beforeMapping)
    {
        var initial = FlowTestData.Lease("hypervisor-01", 94);
        var graph = FlowTestData.AiGraph(beforeMapping);
        var lease = initial with { Version = initial.Version with { Graph = graph,
            ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } };
        FlowGraphValidator.ValidatePublished(graph).Valid.Should().BeTrue();
        var preview = FlowPureEvaluation.DryRun(new(graph, lease.Event.Data));
        preview.Valid.Should().BeTrue(); preview.Code.Should().Be("inert-preview"); preview.VisitedNodeIds.Should().HaveCount(4);
        var ai = graph.Nodes.Single(node => node.Kind == FlowNodeKind.InvestigateWithAi);
        var discovery = new FlowAiResultDto(17, lease.RunId, lease.Version.Id, ai.Id, Guid.NewGuid(), lease.Event.EventId,
            FlowAiResultStatus.Processing, lease.Event.OccurredAtUtc);
        var incidents = new List<FlowIncidentActionDraft>(); var automatedTurns = 0;
        var runtime = new VeloxFlowRuntimeAdapter();
        var waiting = await runtime.ExecuteAsync(lease, (draft, _) =>
        {
            if (draft.Kind == FlowActionKind.NetclawInvestigation)
            {
                automatedTurns++; return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Waiting, "ai-investigation-processing", new(discovery.InvestigationId.ToString())) { AiResult = discovery });
            }
            incidents.Add(draft); throw new InvalidOperationException("No incident before completion.");
        });
        waiting.Status.Should().Be(FlowRunStatus.WaitingForAi); waiting.Continuation!.ResumeNodeId.Should().Be(ai.Id);
        incidents.Should().BeEmpty(); automatedTurns.Should().Be(1);
        var completed = discovery with { Status = FlowAiResultStatus.Completed, CompletedAtUtc = lease.Event.OccurredAtUtc.AddMinutes(1),
            Summary = "Read-only storage evidence shows a full volume.", Evidence = "Disk probe: completed", Recommendations = "Contact storage owner." };
        var resumed = lease with { Continuation = waiting.Continuation };
        var result = await runtime.ExecuteAsync(resumed, (draft, _) =>
        {
            if (draft.Kind == FlowActionKind.NetclawInvestigation)
                return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "ai-investigation-completed", new(completed.InvestigationId.ToString())) { AiResult = completed });
            incidents.Add(draft); return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created", new("incident-1")));
        });
        result.Status.Should().Be(FlowRunStatus.Succeeded); incidents.Should().ContainSingle(); automatedTurns.Should().Be(1);
        var incident = incidents.Single();
        incident.Fields.Description.Should().Contain("Value: 94").And.Contain("AI investigation").And.Contain(completed.Summary!).And.Contain(completed.Evidence!)
            .And.Contain($"/ai/ops?tenantId=17&investigation={completed.InvestigationId:D}");
        incident.Event.Data.Should().Be(lease.Event.Data); incident.IdempotencyKey.Should().Be(FlowActionKeys.Create(lease, incident.ActionNodeId));
    }

    [Theory]
    [InlineData(FlowAiFailureBehavior.ContinueWithAlert, FlowAiResultStatus.Timeout, true)]
    [InlineData(FlowAiFailureBehavior.ContinueWithAlert, FlowAiResultStatus.Failed, true)]
    [InlineData(FlowAiFailureBehavior.ContinueWithAlert, FlowAiResultStatus.DeliveryUnknown, true)]
    [InlineData(FlowAiFailureBehavior.StopFlow, FlowAiResultStatus.Timeout, false)]
    public async Task Ai_failure_disposition_keeps_original_facts_and_labels_unsuccessful_outcomes(FlowAiFailureBehavior behavior, FlowAiResultStatus status, bool deliver)
    {
        var initial = FlowTestData.Lease("original-alert", 97); var graph = FlowTestData.AiGraph();
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.InvestigateWithAi
            ? node with { AiSettings = new(2, behavior) } : node).ToArray() };
        var lease = initial with { Version = initial.Version with { Graph = graph,
            ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } };
        var incidents = 0;
        var result = await new VeloxFlowRuntimeAdapter().ExecuteAsync(lease, (draft, _) =>
        {
            if (draft.Kind == FlowActionKind.NetclawInvestigation)
                return Task.FromResult(new FlowIncidentActionResult(status == FlowAiResultStatus.DeliveryUnknown ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed, "ai-investigation-failed")
                { AiResult = new(17, lease.RunId, lease.Version.Id, draft.ActionNodeId, Guid.NewGuid(), lease.Event.EventId, status, lease.Event.OccurredAtUtc, lease.Event.OccurredAtUtc, "Unconfirmed text must not become successful findings") });
            incidents++; draft.Fields.Description.Should().Contain("Value: 97").And.Contain("Outcome: " + status)
                .And.NotContain("Unconfirmed text");
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created", new("incident-1")));
        });
        incidents.Should().Be(deliver ? 1 : 0); result.Status.Should().Be(deliver ? FlowRunStatus.Succeeded : FlowRunStatus.Failed);
    }

    [Fact]
    public void Ai_templates_are_allowlisted_and_bounded_findings_never_remove_original_facts_or_the_reference()
    {
        var input = FlowTestData.EventData("original", 99);
        var result = new FlowAiResultDto(17, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FlowAiResultStatus.Completed,
            input.ObservedAtUtc, input.ObservedAtUtc, new string('s', 3000), new string('e', 1000));
        FlowGraphValidator.ValidTemplate("{aiSummary} {aiStatus} {aiObservedAt}", 4096).Should().BeTrue();
        var mapped = FlowPureEvaluation.Map(new("{ruleName}", new string('a', 4000) + "\n{value} {resource}"), input, result);
        mapped.Description.Should().HaveLength(FlowLimits.MaximumDescriptionLength).And.Contain("99 cpu").And.Contain("Additional AI findings omitted")
            .And.Contain($"/ai/ops?tenantId=17&investigation={result.InvestigationId:D}").And.Contain("Outcome: Completed");
        var denseInput = input with { RuleName = new string('r', 512) };
        // A valid legacy mapping can expand almost to the receiver limit despite a short template.
        // Leave less room than even the intact AI reference header, so this exercises the canonical-facts fallback.
        var denseMapping = new FlowIncidentMappingDto("Monitoring alert", string.Concat(Enumerable.Repeat("{ruleName}", 15)) + new string('m', 200));
        FlowPureEvaluation.Map(denseMapping, denseInput).Description.Should().HaveLength(7880);
        var fallback = FlowPureEvaluation.Map(denseMapping, denseInput, result);
        fallback.Description.Length.Should().BeLessThanOrEqualTo(FlowLimits.MaximumDescriptionLength);
        fallback.Description.Should().Contain("Original alert facts").And.Contain("Rule ID: " + input.RuleId.ToString("D"))
            .And.Contain("Agent ID: " + input.AgentId.ToString("D")).And.Contain("Severity: Critical").And.Contain("Value: 99")
            .And.Contain("Resource: cpu").And.Contain("Full original alert and mapping retained")
            .And.Contain($"/ai/ops?tenantId=17&investigation={result.InvestigationId:D}");
        var offline = denseInput with { AgentId = Guid.Empty, Resource = MonitoringOfflinePresentation.ResourceKey, Metric = "ClientOffline", OfflineSummary = new(3, 8, "hv01, hv02, dns", false, 1) };
        var offlineFallback = FlowPureEvaluation.Map(denseMapping, offline, result);
        offlineFallback.Description.Should().Contain("Offline count: 3").And.Contain("Monitored count: 8").And.Contain("Suppressed count: 1");
    }

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
    public async Task Parallel_ai_action_groups_wait_without_enriching_or_reordering_their_independent_siblings()
    {
        var initial = FlowTestData.GroupLease(); var graph = initial.Version.Graph;
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.ActionGroup
            ? node with { Actions = node.Actions!.Select(action => action.Kind == FlowActionKind.Slack
                ? action with { Kind = FlowActionKind.NetclawInvestigation, ConnectorId = FlowTestData.AiConnectorId } : action).ToArray() } : node).ToArray() };
        var lease = initial with { Version = initial.Version with { Graph = graph,
            ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } };
        var runtime = new VeloxFlowRuntimeAdapter();
        var result = await runtime.ExecuteAsync(lease, (draft, _) =>
        {
            draft.Fields.Description.Should().NotContain("AI investigation");
            return Task.FromResult(draft.Kind == FlowActionKind.NetclawInvestigation
                ? new FlowIncidentActionResult(FlowIncidentActionResultKind.Waiting, "ai-investigation-processing", new(Guid.NewGuid().ToString()))
                    { AiResult = new(17, lease.RunId, lease.Version.Id, draft.ActionNodeId, Guid.NewGuid(), lease.Event.EventId, FlowAiResultStatus.Processing, lease.Event.OccurredAtUtc) }
                : new(FlowIncidentActionResultKind.Succeeded, "delivered", new("receipt")));
        });
        result.Status.Should().Be(FlowRunStatus.WaitingForAi); result.Continuation!.AiResult.Should().BeNull();
        var completed = await runtime.ExecuteAsync(lease with { Continuation = result.Continuation }, (draft, _) =>
        {
            draft.Fields.Description.Should().NotContain("AI investigation");
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "delivered", new("receipt")));
        });
        completed.Status.Should().Be(FlowRunStatus.Succeeded);
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
    public static readonly Guid AiConnectorId = Guid.Parse("d3cb241e-5322-4496-bd60-e2864ee7137f");
    public static FlowGraphDto AiGraph(bool beforeMapping = false)
    {
        var graph = Graph(); var trigger = graph.Nodes[0]; var map = graph.Nodes[1]; var incident = graph.Nodes[2];
        var ai = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "AI: Investigate alert", new(500, 160), ConnectorId: AiConnectorId, ConnectorRevision: 1);
        return graph with { Nodes = [trigger, map, ai, incident], Edges = beforeMapping
            ? [new(trigger.Id, "out", ai.Id, "in"), new(ai.Id, "out", map.Id, "in"), new(map.Id, "out", incident.Id, "in")]
            : [new(trigger.Id, "out", map.Id, "in"), new(map.Id, "out", ai.Id, "in"), new(ai.Id, "out", incident.Id, "in")] };
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
