using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowJobTargetBindingsTests
{
    [Fact]
    public void Low_disk_alert_binds_its_trusted_client_ID_separately_from_script_parameters()
    {
        var expected = Guid.NewGuid();
        var alert = new FlowEventDataDto(expected, Guid.NewGuid(), "Low disk space", "synthetic-node",
            "/", "disk.free.percent", "Warning", 2, null, DateTimeOffset.UtcNow);
        var node = RuntimeNode(JobRuntimeTargetKind.ClientId,
            new(FlowInputSource.Event, FlowDataType.Text, "alert.agentId"));
        var mapped = FlowJobTargetBindings.Mappings(node).ToDictionary(pair => pair.Key,
            pair => FlowValueEvaluation.Resolve(pair.Value, null, alert, new Dictionary<Guid, JsonElement>()).Value!.Value);
        mapped.Add("message", JsonSerializer.SerializeToElement("synthetic diagnostic"));

        var (inputs, target) = FlowJobTargetBindings.Split(node, JsonSerializer.Serialize(mapped));

        Assert.Equal(JobRuntimeTargetKind.ClientId, target!.Kind);
        Assert.Equal(expected.ToString("D"), Assert.Single(target.Values));
        Assert.Equal("synthetic diagnostic", JsonDocument.Parse(inputs).RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain(FlowJobTargetBindings.CheckpointKey, inputs);
        Assert.DoesNotContain(expected.ToString("D"), inputs);
    }

    [Theory]
    [InlineData(".*,!tags:Excluded")]
    [InlineData("quoted \"tag\" + : / literal")]
    public void Runtime_tag_punctuation_remains_one_literal_value(string text)
    {
        var binding = new FlowJobTargetBindingDto(JobRuntimeTargetKind.Tag,
            new(FlowInputSource.Literal, FlowDataType.Text, Literal: JsonSerializer.SerializeToElement(text)));
        var target = FlowJobTargetBindings.Literal(binding, binding.Value.Literal!.Value);
        Assert.Equal(text, Assert.Single(target.Values));
        Assert.False(target.AllOf);
    }

    [Fact]
    public void Typed_ID_lists_remain_unions_and_aggregate_events_do_not_invent_a_singular_client()
    {
        var ids = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
        var binding = new FlowJobTargetBindingDto(JobRuntimeTargetKind.ClientIds,
            new(FlowInputSource.Literal, FlowDataType.Json, Literal: JsonSerializer.SerializeToElement(ids)));
        Assert.Equal(ids, FlowJobTargetBindings.Literal(binding, binding.Value.Literal!.Value).Values);
        var aggregate = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Job,
            new("41"), "123", "123", new(null), FlowExecutionOutcome.Failed, "Failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { Aggregate = new(2, new string('A', 64), new(0, 0, 1, 1, 0, 0, 0, 0)) };
        Assert.Contains("event_aggregate_success_mismatch", FlowExecutionEventValidator.Validate(aggregate with
            { Outcome = FlowExecutionOutcome.Succeeded, RawStatus = "Succeeded" }).Issues);
        var resolved = FlowValueEvaluation.Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.target.agentId"),
            aggregate, null, new Dictionary<Guid, JsonElement>());
        Assert.Equal("2 nodes", FlowExecutionEvidenceFormatter.Values(aggregate)["targetName"]);
        Assert.Contains("Node results: 1 succeeded, 1 failed", FlowExecutionEvidenceFormatter.DescribeOriginal(aggregate));
        Assert.Equal(JsonValueKind.Null, resolved.Value!.Value.ValueKind);
        Assert.Throws<ArgumentException>(() => FlowJobTargetBindings.Literal(
            new(JobRuntimeTargetKind.ClientId, new(FlowInputSource.Event, FlowDataType.Text, "event.target.agentId")), resolved.Value.Value));
    }

    [Fact]
    public void Runtime_target_policy_is_required_and_fixed_jobs_cannot_accept_a_hidden_mapping()
    {
        var runtime = RuntimeNode(JobRuntimeTargetKind.ClientId,
            new(FlowInputSource.Event, FlowDataType.Text, "alert.agentId"));
        Assert.True(FlowGraphV2Validator.ValidateDraft(new(2, [runtime], [], new())).Valid);
        var fixedNode = runtime with { ExecutionAction = runtime.ExecutionAction! with
            { JobTarget = new() { Source = JobTargetSource.ExplicitNodes, AgentIds = [Guid.NewGuid()] } } };
        Assert.Contains(FlowGraphV2Validator.ValidateDraft(new(2, [fixedNode], [], new())).Issues,
            issue => issue.Code == "job-target-binding");
        var invalidLiteral = runtime with { ExecutionAction = runtime.ExecutionAction! with
            { TargetBinding = new(JobRuntimeTargetKind.ClientId, new(FlowInputSource.Literal, FlowDataType.Text,
                Literal: JsonSerializer.SerializeToElement("not-a-client-id"))) } };
        Assert.Contains(FlowGraphV2Validator.ValidateDraft(new(2, [invalidLiteral], [], new())).Issues,
            issue => issue.Code == "job-target-literal");
        var forged = runtime with { Inputs = new Dictionary<string, FlowInputBindingDto>
            { [FlowJobTargetBindings.CheckpointKey] = new(FlowInputSource.Literal, FlowDataType.Text,
                Literal: JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())) } };
        Assert.Contains(FlowGraphV2Validator.ValidateDraft(new(2, [forged], [], new())).Issues,
            issue => issue.Code == "input-binding");
    }

    [Fact]
    public void Pure_preview_evaluates_typed_target_mapping_without_an_execution_service()
    {
        var node = RuntimeNode(JobRuntimeTargetKind.ClientId,
            new(FlowInputSource.Event, FlowDataType.Text, "alert.agentId"));
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.AlertRaised, "Low disk", new(0, 0));
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finished", new(600, 0));
        var edges = FlowNodeCatalogue.Find(FlowNodeKind.RunJob)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => new FlowEdgeDto(node.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }).ToArray();
        var graph = new FlowGraphDto(2, [trigger, node, finish],
            [new(trigger.Id, FlowNodeCatalogue.Event, node.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }, .. edges], new());
        var alert = new FlowEventDataDto(Guid.NewGuid(), Guid.NewGuid(), "Low disk space", "synthetic-node",
            "/", "disk.free.percent", "Warning", 2, null, DateTimeOffset.UtcNow);
        var preview = FlowExecutionPreview.DryRun(new(graph, Alert: alert), 7);
        Assert.True(preview.Valid, string.Join(",", preview.Issues.Select(issue => issue.Code)));
        Assert.Equal("sample-result-unavailable", preview.Code);
        Assert.Equal(FlowTraceProvenance.Simulated, preview.Trace!.Provenance);
        var captured = preview.Trace.Steps.Last().Input.Value!.Value.GetProperty("inputs");
        Assert.Equal(alert.AgentId.ToString("D"), captured.GetProperty(FlowJobTargetBindings.CheckpointKey).GetString());
    }

    [Fact]
    public void Aggregate_terminal_hash_distinguishes_nodes_with_equal_step_ordinals_and_same_counts()
    {
        var now = DateTimeOffset.UtcNow;
        var details = new JobRunDetails(new(100, 41, 7, "", "flow-action", JobRunState.Failed, 0,
            now, now, now, null, null, null)
        { IsAggregate = true, TargetCount = 2, TargetPlanDigest = new string('A', 64), DefinitionDigest = new string('B', 64) }, [], []);
        var first = new JobRunNodeDto(100, Guid.NewGuid(), 1, 101, JobRunNodeState.Failed, "node-a", null, "failure", 1, 2);
        var second = new JobRunNodeDto(100, Guid.NewGuid(), 2, 102, JobRunNodeState.Succeeded, "node-b", null, null, 1, 2);
        var original = JobRunProjectionHash.ComputeAggregate(details, [first, second]);
        Assert.Equal(original, JobRunProjectionHash.ComputeAggregate(details, [second, first]));
        Assert.Equal(original, JobRunProjectionHash.ComputeAggregate(details with
            { Run = details.Run with { EffectiveConcurrency = 100 } }, [first, second]));
        Assert.NotEqual(original, JobRunProjectionHash.ComputeAggregate(details,
            [first with { State = JobRunNodeState.Succeeded, Reason = null }, second with { State = JobRunNodeState.Failed, Reason = "failure" }]));
    }

    [Fact]
    public void Cloned_drafts_remap_target_output_bindings_and_clear_protected_version_references()
    {
        var producer = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Discover hostname", new(0, 0));
        var runtime = RuntimeNode(JobRuntimeTargetKind.Hostname,
            new(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", producer.Id));
        runtime = runtime with { ExecutionAction = runtime.ExecutionAction! with { PinnedSnapshotReference = "immutable-old-version" } };
        var cloned = FlowGraphDraftClone.SchemaTwo(new(2, [producer, runtime], [], new()));
        var selected = cloned.Nodes[1].ExecutionAction!;
        Assert.Equal(cloned.Nodes[0].Id, selected.TargetBinding!.Value.NodeId);
        Assert.NotEqual(producer.Id, selected.TargetBinding.Value.NodeId);
        Assert.Null(selected.PinnedSnapshotReference);
        Assert.Equal(producer.Id, runtime.ExecutionAction!.TargetBinding!.Value.NodeId);
    }

    private static FlowNodeDto RuntimeNode(JobRuntimeTargetKind kind, FlowInputBindingDto binding) =>
        new(Guid.NewGuid(), FlowNodeKind.RunJob, "Investigate low disk space", new(300, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Job, new("41"))
            {
                JobTarget = new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(kind, "ClientId") },
                JobExecutionPolicy = new(), TargetBinding = new(kind, binding), Parameters = []
            }
        };
}
