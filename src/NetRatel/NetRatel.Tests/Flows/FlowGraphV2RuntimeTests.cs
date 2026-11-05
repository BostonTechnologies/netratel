using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowGraphV2RuntimeTests
{
    [Fact]
    public async Task An_exclusive_condition_executes_only_the_chosen_branch_and_convergence_once()
    {
        var lease = BranchLease();
        var store = new Checkpoints();
        var dispatch = new Executions { TerminalImmediately = true };
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(),
            services.GetRequiredService<IServiceScopeFactory>(), new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        var result = await runtime.ExecuteAsync(lease, NoIncident);
        result.Status.Should().Be(FlowRunStatus.Succeeded);
        dispatch.Dispatched.Should().ContainSingle().Which.Should().Be(lease.Version.Graph.Nodes[2].Id);
        store.Current!.Trace!.Steps.Select(step => step.NodeId).Should().Equal(lease.Version.Graph.Nodes[0].Id,
            lease.Version.Graph.Nodes[1].Id, lease.Version.Graph.Nodes[2].Id, lease.Version.Graph.Nodes[4].Id);
        store.Current.Trace.Steps[1].SelectedPort.Should().Be(FlowNodeCatalogue.True);
        store.Current.Trace.Steps.Should().OnlyContain(step => step.Provenance == FlowTraceProvenance.Actual);
    }

    [Fact]
    public async Task Accepted_script_yields_and_restart_observes_same_decimal_string_without_replaying_prefix()
    {
        var lease = FlowNodeTestData.Lease();
        var store = new Checkpoints();
        var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(),
            services.GetRequiredService<IServiceScopeFactory>(), new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        var pending = await runtime.ExecuteAsync(lease, NoIncident);
        pending.Status.Should().Be(FlowRunStatus.WaitingForExecution);
        store.Yields.Should().Be(1);
        store.Current!.AcceptedExecutionId.Should().Be(Executions.NativeId);
        store.Current.Trace!.Steps.Should().ContainSingle().Which.NodeId.Should().Be(lease.Version.Graph.Nodes[0].Id);
        var saved = JsonSerializer.Serialize(store.Current, FlowNodeTestData.Json);
        var restored = JsonSerializer.Deserialize<FlowNodeCheckpointDto>(saved, FlowNodeTestData.Json)!;
        store.Current = restored;
        var restarted = lease with { Token = Guid.NewGuid(), Fence = lease.Fence + 1,
            Continuation = new(restored.NodeId, restored.Fields, restored.Investigation) { Node = restored } };
        var completed = await runtime.ExecuteAsync(restarted, NoIncident);
        completed.Status.Should().Be(FlowRunStatus.Succeeded);
        dispatch.Dispatched.Should().ContainSingle();
        dispatch.Observed.Should().ContainSingle().Which.Should().Be(Executions.NativeId);
        store.Current!.Trace!.Steps.Should().HaveCount(3);
        store.Current.Trace.Steps[1].SelectedPort.Should().Be(FlowNodeCatalogue.Succeeded);
        var recorded = store.Current.Outputs[lease.Version.Graph.Nodes[1].Id];
        recorded.GetProperty("executionId").GetString().Should().Be(Executions.NativeId);
        recorded.GetProperty("receipt").GetProperty("executionId").GetString().Should().Be(Executions.NativeId);
        recorded.GetProperty("terminalEvent").GetProperty("executionId").GetString().Should().Be(Executions.NativeId);
    }

    [Fact]
    public async Task A_queued_or_uncorroborated_success_is_pending_and_a_wait_deadline_is_unknown()
    {
        var lease = FlowNodeTestData.Lease();
        var store = new Checkpoints();
        var dispatch = new Executions { TerminalImmediately = true, OmitTerminal = true };
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(),
            services.GetRequiredService<IServiceScopeFactory>(), new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        (await runtime.ExecuteAsync(lease, NoIncident)).Status.Should().Be(FlowRunStatus.WaitingForExecution);
        store.Current!.SelectedPort.Should().BeNull();
        var waiting = store.Current;
        var later = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(),
            services.GetRequiredService<IServiceScopeFactory>(), new FixedClock(waiting.DeadlineAtUtc.AddSeconds(1)));
        var completed = await later.ExecuteAsync(lease with { Continuation = new(waiting.NodeId, waiting.Fields) { Node = waiting } }, NoIncident);
        completed.Status.Should().Be(FlowRunStatus.Succeeded); // The explicitly authored Unknown path ends at Finish.
        store.Current!.Trace!.Steps.Single(step => step.NodeId == lease.Version.Graph.Nodes[1].Id)
            .SelectedPort.Should().Be(FlowNodeCatalogue.Unknown);
        store.Current.Outputs[lease.Version.Graph.Nodes[1].Id].GetProperty("code").GetString().Should().Be("execution-result-wait-expired");
        dispatch.Dispatched.Should().ContainSingle();
    }

    [Fact]
    public async Task Secret_reference_is_explicit_unsupported_and_dispatches_nothing()
    {
        var lease = FlowNodeTestData.Lease();
        var script = lease.Version.Graph.Nodes[1] with
        {
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["credential"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "synthetic-reference") },
            ExecutionAction = lease.Version.Graph.Nodes[1].ExecutionAction! with
            { Parameters = [new("credential", FlowDataType.SecretReference)] }
        };
        var graph = lease.Version.Graph with { Nodes = [lease.Version.Graph.Nodes[0], script, lease.Version.Graph.Nodes[2]] };
        lease = WithGraph(lease, graph);
        var store = new Checkpoints(); var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(),
            services.GetRequiredService<IServiceScopeFactory>(), new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        await runtime.ExecuteAsync(lease, NoIncident);
        dispatch.Dispatched.Should().BeEmpty();
        store.Current!.Trace!.Steps.Single(step => step.NodeId == script.Id).SelectedPort.Should().Be(FlowNodeCatalogue.Unsupported);
        JsonSerializer.Serialize(store.Current, FlowNodeTestData.Json).Should().NotContain("credential-value");
    }

    private static ServiceProvider Services(Executions dispatch) => new ServiceCollection()
        .AddSingleton<IFlowExecutionActionDispatcher>(dispatch).BuildServiceProvider();
    private static Task<FlowIncidentActionResult> NoIncident(FlowIncidentActionDraft _, CancellationToken __) =>
        throw new InvalidOperationException("No incident node is on this selected path.");
    private static FlowRunLease BranchLease()
    {
        var lease = FlowNodeTestData.Lease(); var graph = lease.Version.Graph;
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Choose true", new(150, 0))
        { Predicate = new(new(FlowInputSource.Literal, FlowDataType.Boolean, Literal: JsonSerializer.SerializeToElement(true)),
            FlowComparison.Equal, JsonSerializer.SerializeToElement(true)) };
        var selected = graph.Nodes[1];
        var inactive = selected with { Id = Guid.NewGuid(), Name = "Inactive branch" };
        var finish = graph.Nodes[2];
        var edges = new List<FlowEdgeDto>
        {
            new(graph.Nodes[0].Id, FlowNodeCatalogue.Event, condition.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.True, selected.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.False, inactive.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }
        };
        foreach (var node in new[] { selected, inactive })
            edges.AddRange(FlowNodeCatalogue.Find(node.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                .Select(port => new FlowEdgeDto(node.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        return WithGraph(lease, graph with { Nodes = [graph.Nodes[0], condition, selected, inactive, finish], Edges = edges });
    }
    private static FlowRunLease WithGraph(FlowRunLease lease, FlowGraphDto graph)
    {
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().BeTrue();
        return lease with { Version = lease.Version with { Graph = graph,
            ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, FlowNodeTestData.Json)) } };
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Checkpoints : IFlowNodeExecutionStore
    {
        public FlowNodeCheckpointDto? Current { get; set; }
        public int Yields { get; private set; }
        public Task<FlowCheckpointWriteDisposition> SaveNodeCheckpointAsync(FlowRunLease lease, int revision,
            FlowNodeCheckpointDto next, CancellationToken cancellationToken = default)
        {
            if ((Current?.Revision ?? 0) != revision || !FlowNodeCheckpointRules.CanAdvance(lease, Current, next))
                throw new InvalidOperationException("The runtime proposed an invalid durable transition.");
            Current = next;
            return Task.FromResult(FlowCheckpointWriteDisposition.Stored);
        }
        public Task<bool> YieldNodeAsync(FlowRunLease lease, int revision, CancellationToken cancellationToken = default)
        { Current!.Revision.Should().Be(revision); Yields++; return Task.FromResult(true); }
    }
    private sealed class Executions : IFlowExecutionActionDispatcher
    {
        public const string NativeId = "18446744073709551615";
        public bool TerminalImmediately { get; init; }
        public bool OmitTerminal { get; init; }
        public List<Guid> Dispatched { get; } = [];
        public List<string> Observed { get; } = [];
        public Task<FlowExecutionActionResult> DispatchAsync(FlowExecutionActionRequest request, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(request.NodeId);
            return Task.FromResult(Result(request, TerminalImmediately));
        }
        public Task<FlowExecutionActionResult> ObserveAsync(FlowExecutionActionRequest request, FlowExecutionActionReceipt receipt,
            CancellationToken cancellationToken = default)
        { receipt.ActionKey.Should().Be(request.ActionKey); Observed.Add(receipt.ExecutionId); return Task.FromResult(Result(request, true)); }
        private FlowExecutionActionResult Result(FlowExecutionActionRequest request, bool terminal)
        {
            var pin = request.Lease.Version.Graph.Nodes.Single(node => node.Id == request.NodeId).ExecutionAction!;
            var target = new FlowExecutionTargetDto(request.Lease.Event.Data!.AgentId, "Synthetic target");
            var receipt = new FlowExecutionActionReceipt(request.ActionKey, ExecutionAdmissionKind.LibraryScript, NativeId);
            var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), request.Lease.Event.TenantId,
                FlowExecutionSourceKind.Script, pin.Definition, NativeId, NativeId, target, FlowExecutionOutcome.Succeeded,
                "Completed", request.Lease.CreatedAtUtc.AddSeconds(2), request.Lease.CreatedAtUtc.AddSeconds(2))
            { Provenance = FlowExecutionEventProvenance.ScriptTerminalCommit, ExitCode = 0, CompletedAtUtc = request.Lease.CreatedAtUtc.AddSeconds(2),
                Causation = new(request.Lease.Event.OccurrenceId.ToString("N"), FlowRunId: request.Lease.RunId, NodeId: request.NodeId, HopCount: 1) };
            return new(terminal ? FlowExecutionActionDisposition.Succeeded : FlowExecutionActionDisposition.Pending,
                terminal ? "script-succeeded" : "script-reserved", receipt,
                terminal ? new(pin.Definition, target, NativeId, NativeId, ExitCode: 0, StandardOutput: "", OutputAvailable: true) : null,
                terminal && !OmitTerminal ? source : null);
        }
    }
}
