using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowNodeCheckpointTests
{
    [Fact]
    public async Task Actual_Velox_boundary_runs_only_one_selected_node_and_keeps_parallel_run_contexts_isolated()
    {
        var runtime = new VeloxFlowRuntimeAdapter();
        var node = FlowNodeTestData.Graph().Nodes[0];
        var calls = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            runtime.ExecuteNodeAsync(Guid.NewGuid(), node, async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref calls);
                await Task.Yield();
                return "run-" + index;
            })));
        results.Should().Equal("run-0", "run-1", "run-2", "run-3");
        calls.Should().Be(4);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ExecuteNodeAsync(Guid.NewGuid(),
            node with { Kind = (FlowNodeKind)999 }, _ => Task.FromResult("unreachable")));
        calls.Should().Be(4);
    }

    [Fact]
    public void Historical_AI_continuation_wire_bytes_do_not_gain_a_null_node_checkpoint()
    {
        const string value = """{"resumeNodeId":"11111111-1111-1111-1111-111111111111","fields":null,"aiResult":null}""";
        var restored = JsonSerializer.Deserialize<FlowContinuationDto>(value, FlowNodeTestData.Json)!;
        restored.Node.Should().BeNull();
        JsonSerializer.Serialize(restored, FlowNodeTestData.Json).Should().Be(value);
    }

    [Fact]
    public void A_waiting_receipt_cannot_choose_success_change_execution_identity_or_replay_a_predecessor()
    {
        var lease = FlowNodeTestData.Lease();
        var trigger = FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[0], 1, 1);
        var completed = FlowNodeTestData.Completed(lease, trigger);
        FlowNodeCheckpointRules.CanAdvance(lease, trigger, completed).Should().BeTrue();
        var script = FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[1], 3, 2) with
        { Outputs = completed.Outputs, Trace = completed.Trace };
        FlowNodeCheckpointRules.CanAdvance(lease, completed, script).Should().BeTrue();
        var dispatching = script with { Revision = 4, Progress = FlowNodeProgress.Dispatching };
        FlowNodeCheckpointRules.CanAdvance(lease, script, dispatching).Should().BeTrue();
        var waiting = FlowNodeTestData.Waiting(dispatching);
        FlowNodeCheckpointRules.CanAdvance(lease, dispatching, waiting).Should().BeTrue();
        FlowNodeCheckpointRules.Valid(lease, waiting with { SelectedPort = FlowNodeCatalogue.Succeeded }).Should().BeFalse();
        FlowNodeCheckpointRules.CanAdvance(lease, waiting, waiting with
            { Revision = waiting.Revision + 1, AcceptedExecutionId = "different-attempt" }).Should().BeFalse();
        FlowNodeCheckpointRules.CanAdvance(lease, completed, trigger with { Revision = 3, Sequence = 2 }).Should().BeFalse();
        var json = JsonSerializer.Serialize(waiting, FlowNodeTestData.Json);
        var restored = JsonSerializer.Deserialize<FlowNodeCheckpointDto>(json, FlowNodeTestData.Json)!;
        restored.AcceptedExecutionId.Should().Be("18446744073709551615");
        restored.Progress.Should().Be(FlowNodeProgress.Waiting);
        JsonSerializer.Serialize(restored, FlowNodeTestData.Json).Should().Be(json);
    }
}

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowNodeCheckpointPostgreSqlTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Wait_survives_restart_yields_the_worker_and_reclaimed_fence_rejects_stale_receipts()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var guard = new Guard();
        await using var first = Provider(connection, clock, guard);
        await SeedAsync(first, clock);
        var execution = first.GetRequiredService<IFlowExecutionStore>();
        var nodes = first.GetRequiredService<IFlowNodeExecutionStore>();
        var lease = (await execution.ClaimAsync(Guid.NewGuid()))!;
        var prepared = FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[0], 1, 1);
        (await nodes.SaveNodeCheckpointAsync(lease, 0, prepared)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        var completed = FlowNodeTestData.Completed(lease, prepared);
        (await nodes.SaveNodeCheckpointAsync(lease, 1, completed)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        var script = FlowNodeTestData.Prepared(lease, lease.Version.Graph.Nodes[1], 3, 2) with
        { Outputs = completed.Outputs, Trace = completed.Trace };
        (await nodes.SaveNodeCheckpointAsync(lease, 2, script)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        var dispatching = script with { Revision = 4, Progress = FlowNodeProgress.Dispatching };
        guard.Allowed = false;
        (await nodes.SaveNodeCheckpointAsync(lease, 3, dispatching)).Should().Be(FlowCheckpointWriteDisposition.Denied);
        guard.Allowed = true;
        (await nodes.SaveNodeCheckpointAsync(lease, 3, dispatching)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        var waiting = FlowNodeTestData.Waiting(dispatching);
        (await nodes.SaveNodeCheckpointAsync(lease, 4, waiting)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        (await nodes.YieldNodeAsync(lease, waiting.Revision)).Should().BeTrue();
        (await execution.ClaimAsync(Guid.NewGuid())).Should().BeNull();

        await using var restarted = Provider(connection, clock, guard);
        clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        var reclaimed = (await restarted.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid()))!;
        reclaimed.Token.Should().NotBe(lease.Token);
        reclaimed.Fence.Should().BeGreaterThan(lease.Fence);
        reclaimed.Attempts.Should().Be(lease.Attempts);
        var restored = reclaimed.Continuation!.Node!;
        restored.AcceptedExecutionId.Should().Be("18446744073709551615");
        restored.Attempt.Should().Be(1);
        restored.Trace!.Steps.Should().ContainSingle(step => step.NodeId == prepared.NodeId);
        (await nodes.SaveNodeCheckpointAsync(lease, waiting.Revision, waiting with { Revision = waiting.Revision + 1 }))
            .Should().Be(FlowCheckpointWriteDisposition.LeaseLost);
        var activeNodes = restarted.GetRequiredService<IFlowNodeExecutionStore>();
        var terminal = FlowNodeTestData.Completed(reclaimed, restored);
        (await activeNodes.SaveNodeCheckpointAsync(reclaimed, restored.Revision, terminal)).Should().Be(FlowCheckpointWriteDisposition.Stored);
        (await activeNodes.SaveNodeCheckpointAsync(reclaimed, restored.Revision, terminal)).Should().Be(FlowCheckpointWriteDisposition.Conflict);
        clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        var afterCrash = (await restarted.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid()))!;
        afterCrash.Continuation!.Node!.SelectedPort.Should().Be(FlowNodeCatalogue.Succeeded);
        afterCrash.Continuation.Node.AcceptedExecutionId.Should().Be(restored.AcceptedExecutionId);
        afterCrash.Continuation.Node.Trace!.Steps.Should().HaveCount(2);
    }

    private static ServiceProvider Provider(string connection, Clock clock, Guard guard)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection));
        services.AddSingleton<TimeProvider>(clock);
        services.AddNetRatelFlows();
        services.AddSingleton<IFlowTransactionAdmission>(guard);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task SeedAsync(ServiceProvider provider, Clock clock)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.MigrateAsync();
        clock.CaptureSystemTime();
        var graph = FlowNodeTestData.Graph();
        FlowGraphV2Validator.ValidatePublished(graph).Valid.Should().BeTrue();
        var flowId = Guid.NewGuid(); var versionId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var graphJson = JsonSerializer.Serialize(graph, FlowNodeTestData.Json);
        var input = new FlowEventEnvelope(17, Guid.NewGuid(), Guid.NewGuid(), versionId, now,
            FlowTestData.EventData("Synthetic checkpoint endpoint", 90) with { ObservedAtUtc = now }, FlowTestData.Authority);
        var eventJson = JsonSerializer.Serialize(input, FlowNodeTestData.Json);
        db.Tenants.Add(new Tenant { Id = 17, Name = "Checkpoint synthetic tenant" });
        db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 17, Name = "Checkpoint fixture", Revision = 1,
            Enabled = true, DraftJson = graphJson, PublishedVersionId = versionId, PublishedVersionNumber = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 17, VersionNumber = 1,
            GraphJson = graphJson, ConfigurationHash = FlowContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(graphJson)),
            PublishedBy = "fixture", PublishedAtUtc = now });
        db.FlowRuns.Add(new() { Id = Guid.NewGuid(), TenantId = 17, FlowId = flowId, FlowVersionId = versionId,
            EventId = input.EventId, OccurrenceId = input.OccurrenceId, EventJson = eventJson,
            EventFingerprint = FlowContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(eventJson)),
            Status = FlowRunStatus.Queued, CreatedAtUtc = now });
        await db.SaveChangesAsync();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
        public void CaptureSystemTime() => _now = new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
    }
    private sealed class Guard : IFlowTransactionAdmission
    {
        public bool Allowed { get; set; } = true;
        public Task<FlowDispatchDecision> CanStartActionAsync(OrchestratorDbContext db, FlowRunLease lease,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FlowDispatchDecision(Allowed && db.Database.IsNpgsql() &&
                db.Database.CurrentTransaction is not null, Allowed ? "allowed" : "source-revoked"));
        }
    }
}

internal static class FlowNodeTestData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static FlowGraphDto Graph()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.AlertRaised, "Alert event", new(0, 0));
        var script = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Approved diagnostic", new(300, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Script, new("synthetic-script", "Synthetic diagnostic", "7", new string('a', 64)))
            { PinnedSnapshotReference = "snapshot:synthetic:7", Parameters = [] }
        };
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(600, 0));
        return new(2, [trigger, script, finish],
            [new(trigger.Id, FlowNodeCatalogue.Event, script.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
             .. FlowNodeCatalogue.Find(script.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                 .Select(port => new FlowEdgeDto(script.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() })], new());
    }

    internal static FlowRunLease Lease()
    {
        var original = FlowTestData.Lease("Synthetic node", 90);
        var graph = Graph();
        return original with
        {
            Version = original.Version with { Graph = graph,
                ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, Json)) },
            CreatedAtUtc = original.Event.OccurredAtUtc
        };
    }

    internal static FlowNodeCheckpointDto Prepared(FlowRunLease lease, FlowNodeDto node, int revision, int sequence) =>
        new(node.Id, revision, sequence, FlowNodeProgress.Prepared, FlowActionKeys.Create(lease, node.Id),
            lease.CreatedAtUtc, lease.CreatedAtUtc.AddHours(1));

    internal static FlowNodeCheckpointDto Waiting(FlowNodeCheckpointDto dispatching) => dispatching with
    {
        Revision = dispatching.Revision + 1, Progress = FlowNodeProgress.Waiting,
        AcceptedSourceKind = FlowExecutionSourceKind.Script, AcceptedExecutionId = "18446744073709551615",
        AcceptedReceipt = JsonSerializer.SerializeToElement(new { executionId = "18446744073709551615", kind = "script" })
    };

    internal static FlowNodeCheckpointDto Completed(FlowRunLease lease, FlowNodeCheckpointDto previous)
    {
        var node = lease.Version.Graph.Nodes.Single(node => node.Id == previous.NodeId);
        var port = node.Kind == FlowNodeKind.AlertRaised ? FlowNodeCatalogue.Event : FlowNodeCatalogue.Succeeded;
        var edge = lease.Version.Graph.Edges.Single(edge => edge.SourceNodeId == node.Id && edge.SourcePort == port);
        var value = JsonSerializer.SerializeToElement(new { outcome = port, executionId = previous.AcceptedExecutionId });
        var outputs = previous.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        outputs[node.Id] = value;
        var snapshot = new FlowTraceSnapshotDto(FlowTraceDataState.Present) { Value = value };
        var step = new FlowNodeTraceDto(previous.Sequence, node.Id, node.Name, FlowTraceStepStatus.Completed, port,
            FlowTraceProvenance.Actual, new(FlowTraceDataState.Missing), snapshot)
        { Attempt = previous.Attempt, SelectedPort = port, EdgeId = edge.Id, NextNodeId = edge.TargetNodeId };
        return previous with
        {
            Revision = previous.Revision + 1, Progress = FlowNodeProgress.Completed, Output = value, Outputs = outputs,
            SelectedPort = port, SelectedEdgeId = edge.Id, NextNodeId = edge.TargetNodeId,
            Trace = new(2, lease.Version.ConfigurationHash, FlowTraceProvenance.Actual, [.. (previous.Trace?.Steps ?? []), step])
        };
    }
}
