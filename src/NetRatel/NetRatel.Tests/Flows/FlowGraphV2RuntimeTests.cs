using System.Text.Json;
using AwesomeAssertions;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
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
        dispatch.ObservationMayAdmit.Should().ContainSingle().Which.Should().BeFalse();
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

    [Fact]
    public async Task Expired_unstarted_node_never_calls_canonical_dispatch()
    {
        var lease = FlowNodeTestData.Lease();
        var completed = FlowNodeTestData.Completed(lease, FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[0], 1, 1));
        var prepared = FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[1], 3, 2) with
        { Outputs = completed.Outputs, Trace = completed.Trace };
        var store = new Checkpoints { Current = prepared }; var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(), services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(prepared.DeadlineAtUtc.AddSeconds(1)));
        await runtime.ExecuteAsync(lease with { Continuation = new(prepared.NodeId, prepared.Fields) { Node = prepared } }, NoIncident);
        dispatch.Dispatched.Should().BeEmpty(); dispatch.Observed.Should().BeEmpty();
        store.Current!.Trace!.Steps.Single(step => step.NodeId == prepared.NodeId).SelectedPort.Should().Be(FlowNodeCatalogue.Unknown);
        store.Current.Outputs[prepared.NodeId].GetProperty("code").GetString().Should().Be("execution-admission-deadline-expired");
    }

    [Fact]
    public async Task Output_budget_failure_is_terminal_once_and_keeps_recorded_prefix()
    {
        var lease = WithGraph(FlowNodeTestData.Lease(), MapChain(18, 3800, false));
        var store = new Checkpoints(); var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(), services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        var result = await runtime.ExecuteAsync(lease, NoIncident);
        result.Status.Should().Be(FlowRunStatus.Failed); result.Code.Should().Be("node-output-limit");
        store.Current!.Progress.Should().Be(FlowNodeProgress.Failed);
        store.Current.Trace!.Steps.Should().HaveCountGreaterThan(2);
        store.Current.Trace.Steps[^1].ErrorCode.Should().Be("node-output-limit");
        var writes = store.Writes;
        var state = store.Current;
        var repeated = await runtime.ExecuteAsync(lease with { Continuation = new(state.NodeId, state.Fields) { Node = state } }, NoIncident);
        repeated.Should().Be(result); store.Writes.Should().Be(writes); dispatch.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Trace_snapshot_budget_sets_omission_warning_without_preventing_pure_evaluation()
    {
        var lease = WithGraph(FlowNodeTestData.Lease(), MapChain(20, 3700, true));
        var store = new Checkpoints(); var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(), services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        (await runtime.ExecuteAsync(lease, NoIncident)).Status.Should().Be(FlowRunStatus.Succeeded);
        store.Current!.Trace!.Truncated.Should().BeTrue();
        store.Current.Trace.Steps.Should().Contain(step => step.Input.Reference == "snapshot-budget-unavailable");
        store.Current.Trace.Steps.Should().HaveCount(22);
        dispatch.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task Denied_dispatch_chooses_its_explicit_port_and_never_invokes_execution()
    {
        var lease = FlowNodeTestData.Lease(); var store = new Checkpoints { DenyNextDispatch = true };
        var dispatch = new Executions();
        using var services = Services(dispatch);
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(), services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        var first = await runtime.ExecuteAsync(lease, NoIncident);
        first.Status.Should().Be(FlowRunStatus.RetryWaiting);
        store.Current!.SelectedPort.Should().Be(FlowNodeCatalogue.Denied);
        var state = store.Current;
        (await runtime.ExecuteAsync(lease with { Continuation = new(state.NodeId, state.Fields) { Node = state } }, NoIncident))
            .Status.Should().Be(FlowRunStatus.Succeeded);
        dispatch.Dispatched.Should().BeEmpty();
        store.Current!.Trace!.Steps.Should().Contain(step => step.Outcome == FlowNodeCatalogue.Denied);
    }

    [Fact]
    public async Task Group_freezes_final_fields_once_with_independent_reader_scopes_and_reuses_them_after_restart()
    {
        var lease = SourceGroupLease();
        var store = new Checkpoints(); var contexts = new ConcurrentBag<Guid>();
        var registrations = new ServiceCollection();
        var database = Guid.NewGuid().ToString("N");
        registrations.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
        registrations.AddScoped(provider =>
        {
            var db = provider.GetRequiredService<OrchestratorDbContext>(); contexts.Add(db.ContextId.InstanceId);
            return new FlowIncidentEvidenceReader(db);
        });
        using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var runtime = new FlowGraphV2Runtime(store, new VeloxFlowRuntimeAdapter(), services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(lease.CreatedAtUtc.AddSeconds(1)));
        var targets = lease.Version.Graph.Nodes[1].Actions!;
        var calls = new ConcurrentDictionary<Guid, int>(); var writes = new ConcurrentDictionary<Guid, int>();
        var frozen = new ConcurrentDictionary<Guid, string>();
        Task<FlowIncidentActionResult> Action(FlowIncidentActionDraft draft, CancellationToken _)
        {
            store.Current!.PreparedActionFields.Should().HaveCount(2);
            store.Current.PreparedActionFields![draft.ActionNodeId].Should().Be(draft.Fields);
            var serialized = JsonSerializer.Serialize(draft.Fields, FlowNodeTestData.Json);
            frozen.AddOrUpdate(draft.ActionNodeId, serialized, (_, original) =>
            { serialized.Should().Be(original); return original; });
            var count = calls.AddOrUpdate(draft.ActionNodeId, 1, (_, current) => current + 1);
            if (draft.ActionNodeId == targets[0].Id && count == 1)
                return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.RetryableSafe, "synthetic-safe-rejection"));
            writes.TryAdd(draft.ActionNodeId, 1);
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created",
                new("synthetic-" + draft.ActionNodeId.ToString("N"))));
        }
        (await runtime.ExecuteAsync(lease, Action)).Status.Should().Be(FlowRunStatus.WaitingForExecution);
        contexts.Should().HaveCount(2); contexts.Distinct().Should().HaveCount(2);
        var state = store.Current!;
        (await runtime.ExecuteAsync(lease with { Continuation = new(state.NodeId, state.Fields, state.Investigation) { Node = state } }, Action))
            .Status.Should().Be(FlowRunStatus.Succeeded);
        contexts.Should().HaveCount(2); writes.Should().HaveCount(2);
        foreach (var fields in store.Current!.PreparedActionFields!.Values)
        {
            fields.Description.Split("Original execution facts", StringSplitOptions.None).Should().HaveCount(2);
            fields.Description.Should().Contain("Synthetic original request failure");
        }
    }

    private static FlowGraphDto MapChain(int count, int length, bool largeInputs)
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.AlertRaised, "Synthetic alert", new(0, 0));
        var maps = Enumerable.Range(0, count).Select(index => new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident,
            "Map " + index, new(100 + index * 100, 0), Mapping: new("Synthetic mapping", largeInputs ? "Small output" : new string('x', length)))
        { Inputs = largeInputs ? new Dictionary<string, FlowInputBindingDto>
            { ["unused"] = new(FlowInputSource.Literal, FlowDataType.Text, Literal: JsonSerializer.SerializeToElement(new string('i', length))) } : null }).ToArray();
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(4000, 0));
        var nodes = new[] { trigger }.Concat(maps).Append(finish).ToArray();
        return new(2, nodes, nodes.Take(nodes.Length - 1).Select((node, index) =>
            new FlowEdgeDto(node.Id, index == 0 ? FlowNodeCatalogue.Event : FlowNodeCatalogue.Result, nodes[index + 1].Id,
                FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }).ToArray(), new());
    }
    private static FlowRunLease SourceGroupLease()
    {
        var lease = FlowNodeTestData.Lease();
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Request failed", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var group = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ActionGroup, "Two independent incidents", new(300, 0))
        { Actions = [new(Guid.NewGuid(), FlowActionKind.RatelDeskIncident, Guid.NewGuid(), 1),
            new(Guid.NewGuid(), FlowActionKind.RatelDeskIncident, Guid.NewGuid(), 1)] };
        var graph = new FlowGraphDto(2, [trigger, group],
            [new(trigger.Id, FlowNodeCatalogue.Event, group.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), lease.Event.TenantId,
            FlowExecutionSourceKind.Request, new("synthetic-definition", "Synthetic request"), "9007199254740993",
            "9007199254740993", new(lease.Event.Data!.AgentId, "Synthetic target"), FlowExecutionOutcome.Failed, "Failed",
            lease.CreatedAtUtc, lease.CreatedAtUtc)
        { Provenance = FlowExecutionEventProvenance.RequestStateCommit, Reason = "Synthetic original request failure",
            CompletedAtUtc = lease.CreatedAtUtc };
        lease = WithGraph(lease, graph);
        return lease with { Event = new(lease.Event.TenantId, source.EventId, source.OccurrenceId, lease.Version.Id,
            source.OccurredAtUtc, null, lease.Event.Authority)
            { ExecutionEvent = source, SourceAdmissionKey = Guid.NewGuid(), SourceDeliveryId = Guid.NewGuid(), SourceEventIds = [source.EventId] } };
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
        public int Writes { get; private set; }
        public bool DenyNextDispatch { get; set; }
        public Task<FlowCheckpointWriteDisposition> SaveNodeCheckpointAsync(FlowRunLease lease, int revision,
            FlowNodeCheckpointDto next, CancellationToken cancellationToken = default)
        {
            if (DenyNextDispatch && next.Progress == FlowNodeProgress.Dispatching)
            { DenyNextDispatch = false; return Task.FromResult(FlowCheckpointWriteDisposition.Denied); }
            if ((Current?.Revision ?? 0) != revision || !FlowNodeCheckpointRules.CanAdvance(lease, Current, next))
                throw new InvalidOperationException("The runtime proposed an invalid durable transition.");
            Current = next; Writes++;
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
        public List<bool> ObservationMayAdmit { get; } = [];
        public Task<FlowExecutionActionResult> DispatchAsync(FlowExecutionActionRequest request, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(request.NodeId);
            return Task.FromResult(Result(request, TerminalImmediately));
        }
        public Task<FlowExecutionActionResult> ObserveAsync(FlowExecutionActionRequest request, FlowExecutionActionReceipt receipt,
            CancellationToken cancellationToken = default)
        { receipt.ActionKey.Should().Be(request.ActionKey); ObservationMayAdmit.Add(request.AllowUnstartedDispatch); Observed.Add(receipt.ExecutionId); return Task.FromResult(Result(request, true)); }
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
