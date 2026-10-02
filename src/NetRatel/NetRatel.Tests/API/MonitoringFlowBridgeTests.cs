using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Monitoring;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class MonitoringFlowBridgeTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("authority")]
    [InlineData("resource")]
    [InlineData("time")]
    [InlineData("client")]
    public void CanonicalEnvelopeRetainsImmutableIntentAndRejectsAlteration(string field)
    {
        var rig = new Rig();
        var canonical = MonitoringFlowEventFactory.Create(rig.Intent)!;
        rig.Clock.Now = rig.Clock.Now.AddHours(1);
        MonitoringFlowEventFactory.Create(rig.Intent).Should().Be(canonical);
        canonical.Data.ClientName.Should().Be(rig.Client.AgentId.ToString("D"));
        canonical.OccurredAtUtc.Should().Be(rig.Intent.AtUtc);
        var altered = field switch
        {
            "tenant" => canonical with { TenantId = canonical.TenantId + 1 },
            "authority" => canonical with { Authority = canonical.Authority with { PrincipalId = "other" } },
            "resource" => canonical with { Data = canonical.Data with { Resource = "other" } },
            "time" => canonical with { OccurredAtUtc = canonical.OccurredAtUtc.AddSeconds(1) },
            _ => canonical with { Data = canonical.Data with { ClientName = "un-pinned friendly name" } }
        };
        MonitoringFlowEventFactory.Matches(altered, rig.Intent).Should().BeFalse();
    }

    [Fact]
    public async Task NewLeasePersistsRealHandoffBeforePendingAndStopsWhenLeaseIsLost()
    {
        var rig = new Rig();
        (await rig.Processor.ProcessLeaseAsync(rig.Lease, default)).Should().BeTrue();
        rig.Operations.Should().Equal("enqueue", "handoff", "complete:Pending");
        rig.CapturedEnvelope.Should().Be(MonitoringFlowEventFactory.Create(rig.Intent));
        rig.Completion!.Lease.FlowRunId.Should().Be(rig.Run.Id);
        rig.Completion.Outcome.Should().BeNull();

        var lost = new Rig { HandoffAccepted = false };
        (await lost.Processor.ProcessLeaseAsync(lost.Lease, default)).Should().BeFalse();
        lost.Operations.Should().Equal("enqueue", "handoff");
        lost.Completion.Should().BeNull();
    }

    [Theory]
    [InlineData(FlowRunStatus.Queued)]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.RetryWaiting)]
    public async Task KnownRunClaimsOnlyPollAndNeverFabricateACompletedOutcome(FlowRunStatus status)
    {
        var rig = new Rig();
        rig.Run = rig.Run with { Status = status };
        (await rig.Processor.ProcessLeaseAsync(rig.Lease with { FlowRunId = rig.Run.Id }, default)).Should().BeTrue();
        rig.Operations.Should().Equal("complete:Pending");
        rig.Completion!.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task LostMarkerFindsAlreadyCompletedRunWithoutEnqueueAndUsesMonotonicReceiptTime()
    {
        var rig = new Rig();
        rig.SetSuccessfulRun(rig.Clock.Now.AddSeconds(-10));
        var unknownAt = rig.Clock.Now.AddSeconds(-5);
        rig.State = rig.Evaluator.RecordFlowOutcome(rig.State, rig.Intent.OccurrenceId, rig.Intent.EventId,
            new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, unknownAt, "lost_handoff_marker")).State;
        rig.Clock.Now = rig.Clock.Now.AddSeconds(5);
        (await rig.Processor.ReconcileReceiptAsync(new(rig.Lease.OutboxId, rig.Intent, null), default)).Should().BeTrue();
        rig.FindCalls.Should().Be(1);
        rig.Operations.Should().Equal("reconcile");
        rig.Reconciled!.OccurredAtUtc.Should().Be(rig.Clock.Now).And.BeAfter(unknownAt);
        rig.Reconciled.Receipt!.IncidentId.Should().Be("incident-123");
        rig.State.Occurrence!.FlowOutcome.Should().Be(rig.Reconciled);
        rig.Run.CompletedAtUtc.Should().BeBefore(unknownAt);
    }

    [Fact]
    public async Task UnknownWithoutAnExactExistingRunRemainsLookupOnly()
    {
        var rig = new Rig { ReturnRun = false };
        (await rig.Processor.ReconcileReceiptAsync(new(rig.Lease.OutboxId, rig.Intent, null), default)).Should().BeFalse();
        rig.FindCalls.Should().Be(1);
        rig.Operations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("event")]
    [InlineData("occurrence")]
    [InlineData("version")]
    public async Task ForeignFlowReceiptCannotCompleteMonitoringEpisode(string field)
    {
        var rig = new Rig();
        rig.SetSuccessfulRun(rig.Clock.Now);
        rig.Run = field switch
        {
            "event" => rig.Run with { EventId = Guid.NewGuid() },
            "occurrence" => rig.Run with { OccurrenceId = Guid.NewGuid() },
            _ => rig.Run with { FlowVersionId = Guid.NewGuid() }
        };
        (await rig.Processor.ReconcileReceiptAsync(new(rig.Lease.OutboxId, rig.Intent, null), default)).Should().BeFalse();
        rig.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulPersistedReceiptCannotBeDiscardedByContradictoryFailureMetadata()
    {
        var rig = new Rig();
        rig.SetSuccessfulRun(rig.Clock.Now);
        rig.Run = rig.Run with { Status = FlowRunStatus.Failed };
        (await rig.Processor.ProcessLeaseAsync(rig.Lease with { FlowRunId = rig.Run.Id }, default)).Should().BeTrue();
        rig.Completion!.Status.Should().Be(MonitoringOutboxStatus.Pending);
        rig.Completion.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task ImmutablePublishedVersionRemainsValidWhenDefinitionIsDisabledAndHashCorruptionFails()
    {
        var rig = new Rig { DefinitionEnabled = false };
        var provider = new PublishedMonitoringFlowProvider(rig.Flows);
        (await provider.IsPublishedAsync(rig.Client.TenantId, rig.Version.Id, default)).Should().BeTrue();
        (await provider.ListPublishedAsync(rig.Client.TenantId, 200, default)).Should().BeEmpty();
        rig.Version = rig.Version with { ConfigurationHash = new string('0', 64) };
        (await provider.IsPublishedAsync(rig.Client.TenantId, rig.Version.Id, default)).Should().BeFalse();
    }

    [Fact]
    public async Task ExactPersistedGuardDeniesForeignTenantAlteredEnvelopeAndClearDuringAuthorityAwait()
    {
        var rig = new Rig();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.MonitoringFlowOutbox.Add(new()
        {
            OutboxId = rig.Lease.OutboxId, TenantId = rig.Client.TenantId, EventId = rig.Intent.EventId,
            OccurrenceId = rig.Intent.OccurrenceId, StableFlowDispatchKey = rig.Intent.StableFlowDispatchKey,
            IntentJson = JsonSerializer.Serialize(rig.Intent)
        });
        db.MonitoringEvidenceStreams.Add(new()
        {
            TenantId = rig.Client.TenantId, AgentId = rig.Client.AgentId, ConnectionId = rig.Fence.ConnectionId,
            ConnectionEpoch = rig.Fence.ConnectionEpoch, EvidenceStreamId = rig.Fence.EvidenceStreamId, Active = true
        });
        db.ClientConnectionEpochs.Add(new() { TenantId = rig.Client.TenantId, AgentId = rig.Client.AgentId, LastIssuedEpoch = rig.Fence.ConnectionEpoch });
        await db.SaveChangesAsync();
        var configurations = Proxy<IMonitoringConfigurationStore>((method, _) => method.Name == nameof(IMonitoringConfigurationStore.GetAsync)
            ? Task.FromResult(new MonitoringConfigurationSnapshot(rig.Client.TenantId, 1, [rig.Rule], [], [], rig.Clock.Now)) : throw new NotSupportedException());
        var directory = Proxy<IMonitoringClientDirectory>((method, _) => method.Name switch
        {
            nameof(IMonitoringClientDirectory.GetCurrentEvidenceAsync) => Task.FromResult<MonitoringEvidenceFence?>(rig.Fence),
            nameof(IMonitoringAgentEligibility.IsEligibleAsync) => Task.FromResult(true),
            _ => throw new NotSupportedException()
        });
        var clearWhileAwaiting = false;
        var verifier = Proxy<IFlowExecutionAuthorityVerifier>((_, _) =>
        {
            if (clearWhileAwaiting) rig.State = rig.State with
            {
                StateRevision = rig.State.StateRevision + 1,
                Occurrence = rig.State.Occurrence! with { EndedAtUtc = rig.Clock.Now, ClosureDisposition = MonitoringClosureDisposition.ManuallyCleared }
            };
            return Task.FromResult(true);
        });
        var guard = new MonitoringFlowDispatchGuard(db, rig.Store, configurations, directory, verifier, rig.Clock);
        var input = MonitoringFlowEventFactory.Create(rig.Intent)!;
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeTrue();
        (await guard.CanDispatchAsync(input with { TenantId = input.TenantId + 1 })).Allowed.Should().BeFalse();
        (await guard.CanDispatchAsync(input with { Authority = input.Authority with { PrincipalId = "forged" } })).Allowed.Should().BeFalse();
        var allocator = await db.ClientConnectionEpochs.SingleAsync();
        allocator.LastIssuedEpoch++;
        await db.SaveChangesAsync();
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeFalse("the issued epoch fences an old presence read model");
        allocator.LastIssuedEpoch--;
        await db.SaveChangesAsync();
        clearWhileAwaiting = true;
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeFalse();
    }

    private sealed class Rig
    {
        public Clock Clock { get; } = new();
        public ClientKey Client { get; } = new(12, Guid.NewGuid());
        public MonitoringSeriesEvaluator Evaluator { get; }
        public MonitoringRuleDto Rule { get; }
        public MonitoringSeriesState State { get; set; }
        public MonitoringOutboxIntent Intent { get; }
        public MonitoringEvidenceFence Fence { get; }
        public MonitoringOutboxLease Lease { get; }
        public FlowVersionDto Version { get; set; }
        public FlowRunSummaryDto Run { get; set; }
        public bool DefinitionEnabled { get; set; } = true;
        public bool ReturnRun { get; set; } = true;
        public bool HandoffAccepted { get; set; } = true;
        public int FindCalls { get; private set; }
        public List<string> Operations { get; } = [];
        public MonitoringOutboxCompletion? Completion { get; private set; }
        public MonitoringFlowOutcomeDto? Reconciled { get; private set; }
        public FlowEventEnvelope? CapturedEnvelope { get; private set; }
        public FlowActionReceiptDto? Receipt { get; private set; }
        public IMonitoringStore Store { get; }
        public IFlowDefinitionService Flows { get; }
        public MonitoringFlowOutboxProcessor Processor { get; }

        public Rig()
        {
            var template = FlowGraphTemplates.IncidentFromAlert();
            var graph = template with { Nodes = template.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident
                ? node with { ConnectorId = Guid.NewGuid(), ConnectorRevision = 1 } : node).ToArray() };
            Version = new(Guid.NewGuid(), Guid.NewGuid(), Client.TenantId, 1, graph,
                FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                Guid.NewGuid().ToString("D"), Clock.Now);
            Rule = new(Client.TenantId, Guid.NewGuid(), 1, 1, "CPU high", true, MonitoringSeverity.Critical,
                new(MonitoringTargetMode.Selected, [Client.AgentId], []),
                new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 90, 75, null, null, []),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), Version.Id, Version.PublishedBy);
            var key = new MonitoringSeriesKey(Client.TenantId, Rule.RuleId, Client.AgentId, "cpu");
            Fence = new(Client, Guid.NewGuid(), 1, Guid.NewGuid());
            Evaluator = new(Clock);
            var initial = Evaluator.CreateInitial(key, Rule);
            Clock.Now = Clock.Now.AddSeconds(1);
            MonitoringObservationDto Observation(ulong sequence) => new(key, new(1, sequence), Fence.EvidenceStreamId,
                Clock.Now, Clock.Now, true, true, NumericValue: 95);
            var pending = Evaluator.Evaluate(initial, Rule, Observation(1), 1, Fence.EvidenceStreamId, []);
            Clock.Now = Clock.Now.AddSeconds(1);
            var raised = Evaluator.Evaluate(pending.State, Rule, Observation(2), 1, Fence.EvidenceStreamId, []);
            State = raised.State;
            Intent = raised.Outbox.Single();
            Clock.Now = Clock.Now.AddSeconds(20);
            Lease = new(Guid.NewGuid(), Intent, Guid.NewGuid(), Guid.NewGuid(), 1, Clock.Now.AddSeconds(60), 1);
            Run = new(Guid.NewGuid(), Version.FlowId, Version.Id, Intent.EventId, Intent.OccurrenceId,
                FlowRunStatus.Queued, Intent.AtUtc, null);
            Store = Proxy<IMonitoringStore>((method, arguments) => method.Name switch
            {
                nameof(IMonitoringStore.LoadSeriesAsync) => Task.FromResult<MonitoringSeriesState?>(State),
                nameof(IMonitoringStore.MarkOutboxHandedOffAsync) => Mark(),
                nameof(IMonitoringStore.CompleteOutboxAsync) => Complete((MonitoringOutboxCompletion)arguments![0]!),
                nameof(IMonitoringStore.ReconcileOutboxOutcomeAsync) => Reconcile((MonitoringFlowOutcomeDto)arguments![1]!),
                _ => throw new NotSupportedException(method.Name)
            });
            Flows = Proxy<IFlowDefinitionService>((method, _) => method.Name switch
            {
                nameof(IFlowDefinitionService.GetVersionAsync) => Task.FromResult<FlowVersionDto?>(Version),
                nameof(IFlowDefinitionService.GetRunByIdAsync) => Task.FromResult<FlowRunDetailDto?>(new(Run,
                    MonitoringFlowEventFactory.Create(Intent)!.Data, Receipt is null ? [] :
                    [new(Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident).Id, "persisted-action-key", FlowActionStatus.Succeeded, 1, 1, "incident-created", Receipt)])),
                nameof(IFlowDefinitionService.ListAsync) => Task.FromResult<IReadOnlyList<FlowDefinitionDto>>([Definition()]),
                nameof(IFlowDefinitionService.GetAsync) => Task.FromResult<FlowDefinitionDto?>(Definition()),
                _ => throw new NotSupportedException(method.Name)
            });
            var ingress = Proxy<IFlowEventIngress>((method, arguments) => method.Name switch
            {
                nameof(IFlowEventIngress.EnqueueAsync) => Enqueue((FlowEventEnvelope)arguments![0]!),
                nameof(IFlowEventIngress.GetOutcomeAsync) => Task.FromResult<FlowRunSummaryDto?>(ReturnRun ? Run : null),
                nameof(IFlowEventIngress.FindOutcomeAsync) => Find(),
                _ => throw new NotSupportedException(method.Name)
            });
            Processor = new(Store, ingress, Flows, Clock);
        }

        public void SetSuccessfulRun(DateTimeOffset completed)
        {
            Run = Run with { Status = FlowRunStatus.Succeeded, CompletedAtUtc = completed, Code = "execution-completed" };
            Receipt = new("incident-123", "tracking-9", "https://desk.example/incidents/123");
        }
        private FlowDefinitionDto Definition() => new(Version.FlowId, Client.TenantId, "Real published flow", 1,
            DefinitionEnabled, Version.Graph, Version.Id, Version.VersionNumber, Clock.Now);
        private Task<FlowIngressResult> Enqueue(FlowEventEnvelope input)
        {
            Operations.Add("enqueue"); CapturedEnvelope = input;
            return Task.FromResult(new FlowIngressResult(FlowIngressDisposition.Enqueued, Run.Id));
        }
        private Task<bool> Mark() { Operations.Add("handoff"); return Task.FromResult(HandoffAccepted); }
        private Task<bool> Complete(MonitoringOutboxCompletion completion)
        {
            Operations.Add($"complete:{completion.Status}"); Completion = completion; return Task.FromResult(true);
        }
        private Task<bool> Reconcile(MonitoringFlowOutcomeDto outcome)
        {
            Operations.Add("reconcile");
            State = Evaluator.RecordFlowOutcome(State, Intent.OccurrenceId, Intent.EventId, outcome).State;
            Reconciled = outcome; return Task.FromResult(true);
        }
        private Task<FlowRunSummaryDto?> Find() { FindCalls++; return Task.FromResult<FlowRunSummaryDto?>(ReturnRun ? Run : null); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)value).Handler = handler;
        return value;
    }
}
