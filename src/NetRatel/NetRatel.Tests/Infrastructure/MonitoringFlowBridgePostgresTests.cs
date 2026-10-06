using System.Collections.Immutable;
using System.Text.Json;
using Akka.Actor;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.API.Services.Monitoring;
using NetRatel.Akka.Monitoring;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Tests.Flows;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class MonitoringFlowBridgePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task AdmittedActorRaiseExecutesActualVeloxOnceAndPersistsMatchingIncidentReceipt()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        rig.Ingress.Enqueues.Should().Be(1);
        var run = (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Single();
        run.Status.Should().Be(FlowRunStatus.Queued);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Sends.Should().Be(1);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var state = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        state.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        state.Occurrence.FlowOutcome!.FlowRunId.Should().Be(run.Id);
        state.Occurrence.FlowOutcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Succeeded);
        state.Occurrence.FlowOutcome.Receipt.Should().Be(new MonitoringIncidentReceiptDto("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge"));
        rig.Dispatcher.LastRequest!.Event.Authority.Should().Be(rig.Authority);
        rig.Dispatcher.LastRequest.Event.Data.ClientName.Should().Be(rig.Client.AgentId.ToString("D"));
        await rig.RecordAsync(4);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedRunWithLostHandoffReceiptRecoversByLookupOnlyAcrossProviderRestart(bool knownMarker)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        var admitted = await rig.Ingress.EnqueueAsync(MonitoringFlowEventFactory.Create(lease.Intent)!);
        admitted.Disposition.Should().Be(FlowIngressDisposition.Enqueued);
        if (knownMarker) (await rig.Store.MarkOutboxHandedOffAsync(lease, admitted.RunId!.Value, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        var completed = (await rig.Ingress.GetOutcomeAsync(rig.Client.TenantId, admitted.RunId!.Value))!;
        completed.Status.Should().Be(FlowRunStatus.Succeeded);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        if (!knownMarker)
            (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
                new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, rig.Clock.GetUtcNow(), "lost_handoff_marker"), "lost_handoff_marker"), default)).Should().BeTrue();
        // The known case crashes after recording its real run ID and before
        // releasing its lease. The unknown case lost that marker completely.
        await using var restarted = rig.NewProvider();
        var restartedIngress = restarted.GetRequiredService<CountingIngress>();
        var restartedStore = restarted.GetRequiredService<IMonitoringStore>();
        var receipt = (await restartedStore.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        receipt.FlowRunId.Should().Be(knownMarker ? admitted.RunId : null);
        (await restarted.GetRequiredService<MonitoringFlowOutboxProcessor>().ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        restartedIngress.Enqueues.Should().Be(0);
        restartedIngress.Finds.Should().Be(knownMarker ? 0 : 1);
        restartedIngress.Gets.Should().Be(knownMarker ? 1 : 0);
        var state = (await restartedStore.LoadSeriesAsync(rig.Key, default))!;
        state.Occurrence!.FlowOutcome!.FlowRunId.Should().Be(admitted.RunId);
        state.Occurrence.FlowOutcome.Receipt!.IncidentId.Should().Be("bridge-incident");
        state.Occurrence.FlowOutcome.OccurredAtUtc.Should().Be(rig.Clock.GetUtcNow());
        state.Occurrence.FlowOutcome.OccurredAtUtc.Should().BeAfter(completed.CompletedAtUtc!.Value);
        (await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("bypass")]
    [InlineData("principal")]
    [InlineData("credential")]
    [InlineData("credential-owner")]
    [InlineData("credential-expiry")]
    [InlineData("credential-grants")]
    public async Task RealCurrentClearBypassAndExecutionGrantsAreRecheckedAfterActionPreparation(string change)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        await rig.Bridge.ProcessLeaseAsync(lease, default);
        rig.Dispatcher.BeforePrepare = async () =>
        {
            if (change == "clear")
            {
                var command = new MonitoringOperatorCommand(rig.Key, firing.Occurrence!.OccurrenceId,
                    Guid.Parse(rig.Authority.PrincipalId), "clear during connector preparation");
                (await rig.Actor.Ask<MonitoringStoreWriteResult>(new ClearMonitoringOccurrence(command))).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
            }
            else if (change == "bypass")
            {
                var bypass = new MonitoringBypassDto(Guid.NewGuid(), rig.Client.TenantId, rig.Rule.RuleId, rig.Client.AgentId, "cpu",
                    Guid.Parse(rig.Authority.PrincipalId), "suppress during connector preparation", rig.Clock.GetUtcNow(), rig.Clock.GetUtcNow().AddMinutes(1));
                (await rig.Configuration.SaveBypassAsync(new(bypass, 1), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
            }
            else
            {
                await using var scope = rig.Provider.CreateAsyncScope();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                if (change == "principal")
                {
                    var roleId = await identity.PrincipalRoleAssignments.Where(row => row.PrincipalId == rig.Authority.PrincipalId)
                        .Select(row => row.RoleId).SingleAsync();
                    identity.AccessRolePermissions.Remove(await identity.AccessRolePermissions.SingleAsync(row => row.RoleId == roleId && row.Permission == NetRatelPermissions.FlowExecute));
                }
                else
                {
                    var credential = await identity.IntegrationCredentials.Include(item => item.Grants).SingleAsync();
                    if (change == "credential-owner")
                    {
                        var otherPrincipal = ApplicationPrincipal.CreateId();
                        identity.ApplicationPrincipals.Add(new() { Id = otherPrincipal });
                        credential.OwnerPrincipalId = otherPrincipal;
                    }
                    else if (change == "credential-expiry") credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    else if (change == "credential-grants")
                        credential.Grants.Remove(credential.Grants.Single(grant => grant.Permission == NetRatelPermissions.FlowExecute));
                    else credential.RevokedAtUtc = DateTimeOffset.UtcNow;
                }
                await identity.SaveChangesAsync();
            }
        };
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Preparations.Should().Be(1);
        rig.Dispatcher.Sends.Should().Be(0);
        var run = (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Single();
        run.Status.Should().Be(FlowRunStatus.Failed);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!.FlowRunId.Should().Be(run.Id);
        await using var verify = rig.Provider.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>().FlowActions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RepeatedUnknownReceiptObservationsPreserveHistoryAndLaterVerifiedSuccessReconciles()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        var admitted = await rig.Ingress.EnqueueAsync(MonitoringFlowEventFactory.Create(lease.Intent)!);
        var runId = admitted.RunId!.Value;
        (await rig.Store.MarkOutboxHandedOffAsync(lease, runId, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        // The remote effect and FlowRun succeeded, but its monitoring receipt
        // has not yet been verified after the handoff response was interrupted.
        var unknown = new MonitoringFlowOutcomeDto(runId, MonitoringFlowOutcomeKind.DeliveryUnknown,
            rig.Clock.GetUtcNow(), "receipt_verification_unknown");
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
            unknown, unknown.Code), default)).Should().BeTrue();
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        var before = await CaptureAsync();
        for (var observation = 0; observation < 3; observation++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            (await rig.Store.ReconcileOutboxOutcomeAsync(receipt,
                unknown with { OccurredAtUtc = rig.Clock.GetUtcNow() }, default)).Should().BeTrue();
            (await CaptureAsync()).Should().Be(before);
        }
        foreach (var invalidTime in new[] { default(DateTimeOffset), rig.Clock.GetUtcNow().AddSeconds(1) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Store.ReconcileOutboxOutcomeAsync(receipt,
                unknown with { OccurredAtUtc = invalidTime }, default));
            (await CaptureAsync()).Should().Be(before);
        }

        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var after = await CaptureAsync();
        after.StateRevision.Should().Be(before.StateRevision + 1);
        after.LeaseFence.Should().Be(before.LeaseFence + 1);
        after.Status.Should().Be(MonitoringOutboxStatus.Completed);
        var outcome = (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!;
        outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Succeeded);
        outcome.FlowRunId.Should().Be(runId);
        outcome.Receipt.Should().Be(new MonitoringIncidentReceiptDto("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge"));
        rig.Ingress.Enqueues.Should().Be(1);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();

        async Task<DurableReceiptSnapshot> CaptureAsync()
        {
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var series = await db.MonitoringSeries.AsNoTracking().SingleAsync();
            var occurrence = await db.MonitoringOccurrences.AsNoTracking().SingleAsync();
            var outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync();
            return new(series.StateRevision, series.StateJson, series.UpdatedAtUtc, occurrence.OccurrenceJson,
                outbox.LeaseFence, outbox.OutcomeJson, outbox.Status);
        }
    }

    [Fact]
    public async Task DisabledImmutablePinProducesItsRealFailedRunAndCannotResumeAfterEnable()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var definition = (await rig.Definitions.GetAsync(rig.Client.TenantId, rig.Version.FlowId))!;
        var disabled = (await rig.Definitions.SetEnabledAsync(rig.Client.TenantId, definition.Id, new(definition.Revision, false), rig.Authority.PrincipalId)).Definition!;
        (await rig.Provider.GetRequiredService<IMonitoringPublishedFlowProvider>().IsPublishedAsync(rig.Client.TenantId, rig.Version.Id, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<IMonitoringPublishedFlowProvider>().ListPublishedAsync(rig.Client.TenantId, 200, default)).Should().BeEmpty();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        var outcome = (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!;
        outcome.FlowRunId.Should().NotBeNull(); outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Failed);
        (await rig.Ingress.GetOutcomeAsync(rig.Client.TenantId, outcome.FlowRunId!.Value))!.Status.Should().Be(FlowRunStatus.Failed);
        await rig.Definitions.SetEnabledAsync(rig.Client.TenantId, definition.Id, new(disabled.Revision, true), rig.Authority.PrincipalId);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        rig.Dispatcher.Sends.Should().Be(0);
        (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Should().ContainSingle();
    }

    [Fact]
    public async Task ExactPersistedGuardDeniesForeignTenantAlteredEnvelopeAndClearDuringAuthorityAwait()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync(); var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        var input = MonitoringFlowEventFactory.Create(lease.Intent)!;
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var actual = scope.ServiceProvider.GetRequiredService<IFlowExecutionAuthorityVerifier>();
        var guard = new MonitoringFlowDispatchGuard(db, actual, rig.Clock);
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeTrue();
        (await guard.CanDispatchAsync(input with { TenantId = input.TenantId + 1 })).Allowed.Should().BeFalse();
        (await guard.CanDispatchAsync(input with { Data = input.Data with { Resource = "foreign" } })).Allowed.Should().BeFalse();
        var pause = new PauseAuthority(actual);
        var delayed = new MonitoringFlowDispatchGuard(db, pause, rig.Clock);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var decision = delayed.CanDispatchAsync(input, budget.Token);
        await pause.Entered.Task.WaitAsync(budget.Token);
        try
        {
            var current = (await rig.Store.LoadSeriesAsync(rig.Key, budget.Token))!;
            var evaluator = new MonitoringSeriesEvaluator(rig.Clock);
            (await rig.Store.CommitAsync(new(evaluator.Clear(current, rig.Rule, Guid.NewGuid(), "clear during authority read"), 1), budget.Token))
                .Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        }
        finally { pause.Resume.TrySetResult(); }
        (await decision).Allowed.Should().BeFalse();
        rig.Dispatcher.Sends.Should().Be(0);
        (await rig.Store.LoadSeriesAsync(rig.Key, budget.Token))!.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameScopedGuardDeniesEndedEvidenceWithoutRetiringPresenceOrSendingIncident(bool throughActor)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        var input = MonitoringFlowEventFactory.Create(lease.Intent)!;
        var fence = rig.Evidence.Current!;
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var guard = new MonitoringFlowDispatchGuard(db,
            scope.ServiceProvider.GetRequiredService<IFlowExecutionAuthorityVerifier>(), rig.Clock);
        var originalRun = await db.FlowRuns.AsNoTracking().SingleAsync();
        var originalIntent = (await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync()).IntentJson;
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeTrue();

        if (throughActor)
            (await rig.Actor.Ask<MonitoringInputResult>(new EndMonitoringStream(fence))).Disposition
                .Should().Be(MonitoringInputDisposition.Accepted);
        else
            (await rig.Store.EndEvidenceStreamAsync(fence, default)).Should().BeTrue();

        var denied = await guard.CanDispatchAsync(input);
        denied.Allowed.Should().BeFalse();
        denied.Code.Should().Be("monitoring-evidence-unavailable");
        (await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync()).Active.Should().BeFalse();
        var owner = (await rig.Provider.GetRequiredService<IClientConnectionEpochStore>()
            .GetCurrentAsync(rig.Client, default))!;
        owner.Owner.Should().Be(new OwnerKey(rig.Client, fence.ConnectionId, fence.ConnectionEpoch));
        owner.IsEffective(rig.Clock.GetUtcNow()).Should().BeTrue();

        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Preparations.Should().Be(0);
        rig.Dispatcher.Sends.Should().Be(0);
        rig.Ingress.Enqueues.Should().Be(1);
        var failedRun = await db.FlowRuns.AsNoTracking().SingleAsync();
        failedRun.Id.Should().Be(originalRun.Id);
        failedRun.Status.Should().Be(FlowRunStatus.Failed);
        failedRun.EventJson.Should().Be(originalRun.EventJson);
        failedRun.EventFingerprint.Should().Be(originalRun.EventFingerprint);
        (await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync()).IntentJson.Should().Be(originalIntent);
        var action = await db.FlowActions.AsNoTracking().SingleAsync();
        action.Status.Should().Be(FlowActionStatus.Failed);
        action.PreparedJson.Should().BeNull();
        action.ReceiptJson.Should().BeNull();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SameScopedGuardDeniesTerminalOutboxAfterRealReceiptReconciliationAndPreservesV1History()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        var input = MonitoringFlowEventFactory.Create(lease.Intent)!;
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var guard = new MonitoringFlowDispatchGuard(db,
            scope.ServiceProvider.GetRequiredService<IFlowExecutionAuthorityVerifier>(), rig.Clock);
        (await guard.CanDispatchAsync(input)).Allowed.Should().BeTrue();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Sends.Should().Be(1);
        var originalHistory = await CaptureV1HistoryAsync();
        originalHistory.PreparedJson.Should().NotBeNull();
        originalHistory.ReceiptJson.Should().NotBeNull();
        (await db.FlowRuns.AsNoTracking().SingleAsync()).Status.Should().Be(FlowRunStatus.Succeeded);
        (await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync()).Status.Should().Be(MonitoringOutboxStatus.Pending);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        receipt.FlowRunId.Should().Be(originalHistory.RunId);
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync();
        outbox.Status.Should().Be(MonitoringOutboxStatus.Completed);
        outbox.FlowRunId.Should().Be(originalHistory.RunId);
        var outcome = (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!;
        outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Succeeded);
        outcome.FlowRunId.Should().Be(originalHistory.RunId);
        var persistedReceipt = JsonSerializer.Deserialize<FlowActionReceiptDto>(originalHistory.ReceiptJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        outcome.Receipt.Should().Be(new MonitoringIncidentReceiptDto(persistedReceipt.IncidentId,
            persistedReceipt.TrackingId, persistedReceipt.SafeLink));

        var denied = await guard.CanDispatchAsync(input);
        denied.Allowed.Should().BeFalse();
        denied.Code.Should().Be("monitoring-outbox-changed");
        (await CaptureV1HistoryAsync()).Should().Be(originalHistory);
        // Re-observing the genuine completed receipt cannot reopen the outbox or create another effect.
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var deniedAgain = await guard.CanDispatchAsync(input);
        deniedAgain.Allowed.Should().BeFalse();
        deniedAgain.Code.Should().Be("monitoring-outbox-changed");
        (await CaptureV1HistoryAsync()).Should().Be(originalHistory);
        (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Should().BeEmpty();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        rig.Ingress.Enqueues.Should().Be(1);
        await rig.AssertOneEffectAsync();

        async Task<BridgeV1HistorySnapshot> CaptureV1HistoryAsync()
        {
            var run = await db.FlowRuns.AsNoTracking().SingleAsync();
            var action = await db.FlowActions.AsNoTracking().SingleAsync();
            var currentOutbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync();
            return new(run.Id, run.EventJson, run.EventFingerprint, currentOutbox.IntentJson,
                action.NodeId, action.IdempotencyKey, action.DraftJson, action.PreparedJson,
                action.ConnectorRevision, action.SemanticFingerprint, action.ReceiptJson);
        }
    }

    private sealed record BridgeV1HistorySnapshot(Guid RunId, string EventJson, string EventFingerprint, string IntentJson,
        Guid ActionNodeId, string IdempotencyKey, string DraftJson, string? PreparedJson,
        long? ConnectorRevision, string? SemanticFingerprint, string? ReceiptJson);

    [Theory]
    [InlineData("clear")]
    [InlineData("end")]
    [InlineData("owner-expiry")]
    public async Task DirectStartActionRechecksMonitoringAfterSuccessfulExternalReadinessWithoutMutatingV1Action(string change)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync();
        var outboxLease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(outboxLease, budget.Token)).Should().BeTrue();
        var store = rig.Provider.GetRequiredService<IFlowExecutionStore>();
        var lease = (await store.ClaimAsync(Guid.NewGuid(), budget.Token))!;
        lease.Should().NotBeNull();
        FlowIncidentActionDraft? draft = null;
        var captured = await rig.Provider.GetRequiredService<IFlowRuntimeAdapter>().ExecuteAsync(lease, (value, token) =>
        {
            token.ThrowIfCancellationRequested();
            draft = value;
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Failed, "capture-only"));
        }, budget.Token);
        captured.Status.Should().Be(FlowRunStatus.Failed);
        draft.Should().NotBeNull();
        (await store.GetOrCreateActionAsync(lease, draft!, budget.Token))!.Status.Should().Be(FlowActionStatus.Pending);
        var preparation = await rig.Dispatcher.PrepareAsync(draft!, budget.Token);
        preparation.Status.Should().Be(FlowIncidentPreparationStatus.Ready);
        preparation.Action.Should().NotBeNull();
        (await store.SavePreparedActionAsync(lease, draft!.ActionNodeId, preparation.Action!, budget.Token)).Should().BeTrue();
        await using (var readiness = rig.Provider.CreateAsyncScope())
            (await readiness.ServiceProvider.GetRequiredService<IFlowDispatchGuard>()
                .CanDispatchAsync(lease.Event, budget.Token)).Allowed.Should().BeTrue();
        var original = await CaptureAsync();
        original.ActionStatus.Should().Be(FlowActionStatus.Pending);
        original.ActionAttempts.Should().Be(0);
        original.PreparedJson.Should().NotBeNull();
        original.ReceiptJson.Should().BeNull();
        var ownership = rig.Provider.GetRequiredService<IClientConnectionEpochStore>();
        var owner = (await ownership.GetCurrentAsync(rig.Client, budget.Token))!;
        owner.IsEffective(rig.Clock.GetUtcNow()).Should().BeTrue();
        if (change == "clear")
        {
            var command = new MonitoringOperatorCommand(rig.Key, firing.Occurrence!.OccurrenceId,
                Guid.Parse(rig.Authority.PrincipalId), "clear after successful external readiness");
            (await rig.Actor.Ask<MonitoringStoreWriteResult>(new ClearMonitoringOccurrence(command), TimeSpan.FromSeconds(30), budget.Token))
                .Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
            var cleared = (await rig.Store.LoadSeriesAsync(rig.Key, budget.Token))!;
            cleared.Phase.Should().Be(MonitoringPhase.Cleared);
            cleared.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        }
        else if (change == "end")
        {
            (await rig.Actor.Ask<MonitoringInputResult>(new EndMonitoringStream(rig.Evidence.Current!), TimeSpan.FromSeconds(30), budget.Token))
                .Disposition.Should().Be(MonitoringInputDisposition.Accepted);
        }
        else
        {
            change.Should().Be("owner-expiry");
            // Construct an expired persisted owner deadline in a fresh provider
            // scope without ending evidence, retiring the owner or expiring the Flow lease.
            await using var expiry = rig.Provider.CreateAsyncScope();
            var db = expiry.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.ClientConnectionOwners.SingleAsync(item => item.TenantId == rig.Client.TenantId &&
                item.AgentId == rig.Client.AgentId && item.ConnectionId == owner.Owner.ConnectionId &&
                item.ConnectionEpoch == (long)owner.Owner.Epoch, budget.Token);
            var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(budget.Token);
            row.PresenceExpiresAtUtc = databaseNow - TimeSpan.FromSeconds(1);
            await db.SaveChangesAsync(budget.Token);
        }
        var currentOwner = (await ownership.GetCurrentAsync(rig.Client, budget.Token))!;
        currentOwner.Owner.Should().Be(owner.Owner);
        currentOwner.IsEffective(rig.Clock.GetUtcNow()).Should().Be(change != "owner-expiry");
        await using (var current = rig.Provider.CreateAsyncScope())
        {
            var db = current.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var evidence = await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync(budget.Token);
            evidence.Active.Should().Be(change != "end");
            (await db.ClientConnectionOwners.AsNoTracking().SingleAsync(budget.Token)).Active.Should().BeTrue();
        }
        var beforeStart = await CaptureAsync();
        beforeStart.Should().Be(original);
        beforeStart.RunStatus.Should().Be(FlowRunStatus.Running);
        beforeStart.LeaseToken.Should().Be(lease.Token);
        beforeStart.LeaseOwner.Should().Be(lease.WorkerId);
        beforeStart.RunFence.Should().Be(lease.Fence);
        beforeStart.LeaseExpiresAtUtc.Should().Be(lease.ExpiresAtUtc);
        lease.ExpiresAtUtc.Should().BeAfter(rig.Clock.GetUtcNow());
        // Invoke the persistence boundary directly: repeating the worker's
        // external readiness check could hide a missing StartAction gate.
        var started = await store.StartActionAsync(lease, draft.ActionNodeId, budget.Token);
        if (started is { Status: FlowActionStatus.Dispatching, Request: { } request })
            await rig.Dispatcher.DispatchAsync(request, budget.Token);
        started.Should().BeNull();
        rig.Dispatcher.Sends.Should().Be(0);
        rig.Dispatcher.Preparations.Should().Be(1);
        rig.Ingress.Enqueues.Should().Be(1);
        (await CaptureAsync()).Should().Be(beforeStart);

        async Task<DirectStartActionV1Snapshot> CaptureAsync()
        {
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(budget.Token);
            var version = await db.FlowVersions.AsNoTracking().SingleAsync(budget.Token);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(budget.Token);
            var outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync(budget.Token);
            var source = await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync(budget.Token);
            return new(run.Id, run.EventJson, run.EventFingerprint, version.GraphJson, version.ConfigurationHash,
                source.SourceInstanceId, run.Status, run.Fence, run.LeaseToken, run.LeaseOwner, run.LeaseExpiresAtUtc,
                run.Attempts, outbox.IntentJson, action.NodeId, action.IdempotencyKey, action.DraftJson,
                action.PreparedJson, action.ConnectorRevision, action.SemanticFingerprint, action.ReceiptJson,
                action.Status, action.Attempts, action.LeaseFence, action.Code, action.NextAttemptAtUtc);
        }
    }

    private sealed record DirectStartActionV1Snapshot(Guid RunId, string EventJson, string EventFingerprint,
        string GraphJson, string ConfigurationHash, Guid SourceInstanceId, FlowRunStatus RunStatus, long RunFence,
        Guid? LeaseToken, Guid? LeaseOwner, DateTimeOffset? LeaseExpiresAtUtc, int RunAttempts, string IntentJson,
        Guid ActionNodeId, string IdempotencyKey, string DraftJson, string? PreparedJson, long? ConnectorRevision,
        string? SemanticFingerprint, string? ReceiptJson, FlowActionStatus ActionStatus, int ActionAttempts,
        long ActionLeaseFence, string? ActionCode, DateTimeOffset? ActionNextAttemptAtUtc);

    [Fact]
    public async Task DirectStartActionRejectsPostgresExpiredLeaseWithSlowInjectedClockAndLiveMonitoring()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var outboxLease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(outboxLease, budget.Token)).Should().BeTrue();
        var store = rig.Provider.GetRequiredService<IFlowExecutionStore>();
        var lease = (await store.ClaimAsync(Guid.NewGuid(), budget.Token))!;
        lease.Should().NotBeNull();
        FlowIncidentActionDraft? draft = null;
        var captured = await rig.Provider.GetRequiredService<IFlowRuntimeAdapter>().ExecuteAsync(lease, (value, token) =>
        {
            token.ThrowIfCancellationRequested();
            draft = value;
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Failed, "capture-only"));
        }, budget.Token);
        captured.Status.Should().Be(FlowRunStatus.Failed);
        draft.Should().NotBeNull();
        (await store.GetOrCreateActionAsync(lease, draft!, budget.Token))!.Status.Should().Be(FlowActionStatus.Pending);
        var preparation = await rig.Dispatcher.PrepareAsync(draft!, budget.Token);
        preparation.Status.Should().Be(FlowIncidentPreparationStatus.Ready);
        preparation.Action.Should().NotBeNull();
        (await store.SavePreparedActionAsync(lease, draft!.ActionNodeId, preparation.Action!, budget.Token)).Should().BeTrue();
        await using (var readiness = rig.Provider.CreateAsyncScope())
            (await readiness.ServiceProvider.GetRequiredService<IFlowDispatchGuard>()
                .CanDispatchAsync(lease.Event, budget.Token)).Allowed.Should().BeTrue();

        var fixedInjectedNow = rig.Clock.GetUtcNow();
        DateTimeOffset expiredDeadline;
        await using (var expiry = rig.Provider.CreateAsyncScope())
        {
            var db = expiry.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            using var pollBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            pollBudget.CancelAfter(TimeSpan.FromSeconds(10));
            DateTimeOffset databaseNow;
            // Let the real database clock pass the fixed fixture clock without
            // making current evidence future-dated or changing owner deadlines.
            do
            {
                databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
                    .SingleAsync(pollBudget.Token);
                if (databaseNow > fixedInjectedNow + TimeSpan.FromMilliseconds(500)) break;
                await Task.Delay(TimeSpan.FromMilliseconds(25), pollBudget.Token);
            } while (true);
            expiredDeadline = databaseNow - TimeSpan.FromMilliseconds(250);
            expiredDeadline.Should().BeAfter(fixedInjectedNow);
            expiredDeadline.Should().BeBefore(databaseNow);
            var run = await db.FlowRuns.SingleAsync(row => row.TenantId == lease.Event.TenantId && row.Id == lease.RunId, budget.Token);
            run.LeaseToken.Should().Be(lease.Token);
            run.LeaseOwner.Should().Be(lease.WorkerId);
            run.Fence.Should().Be(lease.Fence);
            run.LeaseExpiresAtUtc = expiredDeadline;
            await db.SaveChangesAsync(budget.Token);
        }
        lease = lease with { ExpiresAtUtc = expiredDeadline };
        rig.Clock.GetUtcNow().Should().Be(fixedInjectedNow);
        lease.ExpiresAtUtc.Should().BeAfter(rig.Clock.GetUtcNow());

        // Prove the owner/evidence/occurrence/outbox remain genuinely admissible
        // in a caller transaction; refusal must come from the Flow lease deadline.
        await using (var current = rig.Provider.CreateAsyncScope())
        {
            var db = current.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            (await current.ServiceProvider.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, budget.Token)).Should().BeTrue();
            (await MonitoringStore.LockAndAdmitFlowAsync(db, lease.Event, lease.RunId, rig.Clock, budget.Token))
                .Allowed.Should().BeTrue();
            var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(budget.Token);
            lease.ExpiresAtUtc.Should().BeBefore(databaseNow);
            (await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync(budget.Token)).Active.Should().BeTrue();
            (await db.ClientConnectionOwners.AsNoTracking().SingleAsync(budget.Token)).Active.Should().BeTrue();
            await transaction.RollbackAsync(budget.Token);
        }
        var beforeStart = await CaptureAsync();
        beforeStart.RunStatus.Should().Be(FlowRunStatus.Running);
        beforeStart.LeaseToken.Should().Be(lease.Token);
        beforeStart.LeaseOwner.Should().Be(lease.WorkerId);
        beforeStart.RunFence.Should().Be(lease.Fence);
        beforeStart.LeaseExpiresAtUtc.Should().Be(lease.ExpiresAtUtc);
        beforeStart.ActionStatus.Should().Be(FlowActionStatus.Pending);
        beforeStart.ActionAttempts.Should().Be(0);
        beforeStart.PreparedJson.Should().NotBeNull();
        beforeStart.ReceiptJson.Should().BeNull();
        var started = await store.StartActionAsync(lease, draft!.ActionNodeId, budget.Token);
        if (started is { Status: FlowActionStatus.Dispatching, Request: { } request })
            await rig.Dispatcher.DispatchAsync(request, budget.Token);
        started.Should().BeNull();
        rig.Dispatcher.Sends.Should().Be(0);
        rig.Dispatcher.Preparations.Should().Be(1);
        rig.Ingress.Enqueues.Should().Be(1);
        rig.Clock.GetUtcNow().Should().Be(fixedInjectedNow);
        (await CaptureAsync()).Should().Be(beforeStart);

        async Task<DirectStartActionV1Snapshot> CaptureAsync()
        {
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(budget.Token);
            var version = await db.FlowVersions.AsNoTracking().SingleAsync(budget.Token);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(budget.Token);
            var outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync(budget.Token);
            var source = await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync(budget.Token);
            return new(run.Id, run.EventJson, run.EventFingerprint, version.GraphJson, version.ConfigurationHash,
                source.SourceInstanceId, run.Status, run.Fence, run.LeaseToken, run.LeaseOwner, run.LeaseExpiresAtUtc,
                run.Attempts, outbox.IntentJson, action.NodeId, action.IdempotencyKey, action.DraftJson,
                action.PreparedJson, action.ConnectorRevision, action.SemanticFingerprint, action.ReceiptJson,
                action.Status, action.Attempts, action.LeaseFence, action.Code, action.NextAttemptAtUtc);
        }
    }

    [Fact]
    public async Task DirectStartActionRollsBackSavedDispatchingWhenOriginalOwnerDeadlineExpiresBeforeCommit()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var outboxLease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(outboxLease, budget.Token)).Should().BeTrue();
        var store = rig.Provider.GetRequiredService<IFlowExecutionStore>();
        var lease = (await store.ClaimAsync(Guid.NewGuid(), budget.Token))!;
        lease.Should().NotBeNull();
        FlowIncidentActionDraft? draft = null;
        var captured = await rig.Provider.GetRequiredService<IFlowRuntimeAdapter>().ExecuteAsync(lease, (value, token) =>
        {
            token.ThrowIfCancellationRequested();
            draft = value;
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Failed, "capture-only"));
        }, budget.Token);
        captured.Status.Should().Be(FlowRunStatus.Failed);
        draft.Should().NotBeNull();
        (await store.GetOrCreateActionAsync(lease, draft!, budget.Token))!.Status.Should().Be(FlowActionStatus.Pending);
        var preparation = await rig.Dispatcher.PrepareAsync(draft!, budget.Token);
        preparation.Status.Should().Be(FlowIncidentPreparationStatus.Ready);
        preparation.Action.Should().NotBeNull();
        (await store.SavePreparedActionAsync(lease, draft!.ActionNodeId, preparation.Action!, budget.Token)).Should().BeTrue();
        var ownership = rig.Provider.GetRequiredService<IClientConnectionEpochStore>();
        var originalOwner = (await ownership.GetCurrentAsync(rig.Client, budget.Token))!;
        originalOwner.IsEffective(rig.Clock.GetUtcNow()).Should().BeTrue();
        originalOwner.PresenceExpiresAtUtc.Should().Be(originalOwner.LastReceivedAtUtc.AddSeconds(60));
        originalOwner.AuthenticationExpiresAtUtc.Should().Be(originalOwner.LastReceivedAtUtc.AddMinutes(10));
        var afterOriginalDeadline = originalOwner.PresenceExpiresAtUtc.AddTicks(TimeSpan.TicksPerMicrosecond);
        // FireAsync advanced the existing clock before the Flow lease was claimed:
        // the unmodified 60-second lease still outlives this original owner deadline.
        afterOriginalDeadline.Should().BeBefore(lease.ExpiresAtUtc);
        afterOriginalDeadline.Should().BeBefore(originalOwner.AuthenticationExpiresAtUtc);
        afterOriginalDeadline.Should().BeBefore(outboxLease.LeaseExpiresAtUtc);
        await using (var readiness = rig.Provider.CreateAsyncScope())
        {
            var db = readiness.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            (await readiness.ServiceProvider.GetRequiredService<IFlowTransactionAdmission>()
                .CanStartActionAsync(db, lease, budget.Token)).Allowed.Should().BeTrue();
            await transaction.RollbackAsync(budget.Token);
        }
        var original = await CaptureAsync();
        var pause = new PauseSavedDispatching(rig.Client.TenantId, lease.RunId, draft.ActionNodeId);
        await using var writer = rig.NewProvider(pause);
        var starting = writer.GetRequiredService<IFlowExecutionStore>()
            .StartActionAsync(lease, draft.ActionNodeId, budget.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(budget.Token);
            pause.TransactionId.Should().NotBe(Guid.Empty);
            pause.SavedAttempts.Should().Be(1);
            starting.IsCompleted.Should().BeFalse();
            // A separate context sees only the committed Pending action while
            // the real SavedChangesAsync continuation still owns its transaction.
            (await CaptureAsync()).Should().Be(original);
            rig.Clock.Advance(afterOriginalDeadline - rig.Clock.GetUtcNow());
            rig.Clock.GetUtcNow().Should().Be(afterOriginalDeadline);
            lease.ExpiresAtUtc.Should().BeAfter(rig.Clock.GetUtcNow());
            outboxLease.LeaseExpiresAtUtc.Should().BeAfter(rig.Clock.GetUtcNow());
            originalOwner.AuthenticationExpiresAtUtc.Should().BeAfter(rig.Clock.GetUtcNow());
        }
        finally
        {
            pause.Resume.TrySetResult();
            // Observe the actual continuation before disposing its scoped provider,
            // including when an assertion above fails or the finite budget cancels.
            await starting;
        }
        var started = await starting;
        if (started is { Status: FlowActionStatus.Dispatching, Request: { } request })
            await rig.Dispatcher.DispatchAsync(request, budget.Token);
        started.Should().BeNull();
        pause.Calls.Should().Be(1);
        rig.Dispatcher.Preparations.Should().Be(1);
        rig.Dispatcher.Sends.Should().Be(0);
        rig.Ingress.Enqueues.Should().Be(1);
        (await CaptureAsync()).Should().Be(original);
        var currentOwner = (await ownership.GetCurrentAsync(rig.Client, budget.Token))!;
        currentOwner.Should().BeEquivalentTo(originalOwner with { Active = false });
        currentOwner.Active.Should().BeFalse();
        currentOwner.IsEffective(rig.Clock.GetUtcNow()).Should().BeFalse();
        await using (var current = rig.Provider.CreateAsyncScope())
        {
            var db = current.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var action = await db.FlowActions.AsNoTracking().SingleAsync(budget.Token);
            action.Status.Should().Be(FlowActionStatus.Pending);
            action.Attempts.Should().Be(0);
            action.ReceiptJson.Should().BeNull();
            (await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync(budget.Token)).Active.Should().BeTrue();
            (await db.ClientConnectionOwners.AsNoTracking().SingleAsync(budget.Token)).Active.Should().BeTrue();
            (await current.ServiceProvider.GetRequiredService<IFlowExecutionAuthorityVerifier>()
                .AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, budget.Token)).Should().BeTrue();
            await using var transaction = await db.Database.BeginTransactionAsync(budget.Token);
            var denial = await current.ServiceProvider.GetRequiredService<IFlowTransactionAdmission>()
                .CanStartActionAsync(db, lease, budget.Token);
            denial.Allowed.Should().BeFalse();
            denial.Code.Should().Be("monitoring-owner-unavailable");
            await transaction.RollbackAsync(budget.Token);
        }

        async Task<string> CaptureAsync()
        {
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // Fresh independent reads compare all persisted scalar columns,
            // including original deadlines, run/action attempts and receipt/history.
            return JsonSerializer.Serialize(new
            {
                Definition = await db.FlowDefinitions.AsNoTracking().SingleAsync(budget.Token),
                Version = await db.FlowVersions.AsNoTracking().SingleAsync(budget.Token),
                Run = await db.FlowRuns.AsNoTracking().SingleAsync(budget.Token),
                Action = await db.FlowActions.AsNoTracking().SingleAsync(budget.Token),
                Source = await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync(budget.Token),
                Outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync(budget.Token),
                Series = await db.MonitoringSeries.AsNoTracking().SingleAsync(budget.Token),
                Event = await db.MonitoringEvents.AsNoTracking().SingleAsync(budget.Token),
                Occurrence = await db.MonitoringOccurrences.AsNoTracking().SingleAsync(budget.Token),
                Audits = await db.FlowAudits.AsNoTracking().OrderBy(row => row.Id).ToListAsync(budget.Token),
                Evidence = await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync(budget.Token),
                Owner = await db.ClientConnectionOwners.AsNoTracking().SingleAsync(budget.Token)
            });
        }
    }

    private sealed class PauseSavedDispatching(int tenantId, Guid runId, Guid nodeId) : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid TransactionId { get; private set; }
        public int SavedAttempts { get; private set; }
        public int Calls { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not OrchestratorDbContext db ||
                !db.ChangeTracker.Entries<FlowActionRecord>().Any(entry => entry.Entity.TenantId == tenantId &&
                    entry.Entity.RunId == runId && entry.Entity.NodeId == nodeId &&
                    entry.Entity.Status == FlowActionStatus.Dispatching)) return result;
            var transaction = db.Database.CurrentTransaction;
            transaction.Should().NotBeNull();
            TransactionId = transaction!.TransactionId;
            result.Should().Be(1);
            // Read the actual row after PostgreSQL accepted UPDATE, within the
            // very same still-open transaction: an in-memory mutation is insufficient.
            var saved = await db.FlowActions.AsNoTracking().SingleAsync(row => row.TenantId == tenantId &&
                row.RunId == runId && row.NodeId == nodeId, cancellationToken);
            saved.Status.Should().Be(FlowActionStatus.Dispatching);
            saved.Attempts.Should().Be(1);
            saved.ReceiptJson.Should().BeNull();
            SavedAttempts = saved.Attempts;
            Calls++;
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class PauseAuthority(IFlowExecutionAuthorityVerifier actual) : IFlowExecutionAuthorityVerifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> AuthorizeAsync(int tenant, FlowExecutionAuthorityDto authority, CancellationToken ct = default)
        {
            var authorized = await actual.AuthorizeAsync(tenant, authority, ct);
            Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); return authorized;
        }
    }

    private sealed record DurableReceiptSnapshot(ulong StateRevision, string StateJson, DateTimeOffset UpdatedAtUtc,
        string OccurrenceJson, long LeaseFence, string? OutcomeJson, MonitoringOutboxStatus Status);

    private sealed class Rig : IAsyncDisposable
    {
        public required string Connection { get; init; }
        public Clock Clock { get; } = new();
        public ClientKey Client { get; } = new(17, Guid.NewGuid());
        public FlowExecutionAuthorityDto Authority { get; } = new(ApplicationPrincipal.CreateId(), ApplicationPrincipal.CreateId());
        public EvidenceSource Evidence { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        public ServiceProvider Provider { get; private set; } = null!;
        public ActorSystem System { get; private set; } = null!;
        public IActorRef Actor { get; private set; } = null!;
        public FlowVersionDto Version { get; private set; } = null!;
        public MonitoringRuleDto Rule { get; private set; } = null!;
        public IMonitoringStore Store => Provider.GetRequiredService<IMonitoringStore>();
        public IMonitoringConfigurationStore Configuration => Provider.GetRequiredService<IMonitoringConfigurationStore>();
        public IFlowDefinitionService Definitions => Provider.GetRequiredService<IFlowDefinitionService>();
        public CountingIngress Ingress => Provider.GetRequiredService<CountingIngress>();
        public MonitoringFlowOutboxProcessor Bridge => Provider.GetRequiredService<MonitoringFlowOutboxProcessor>();
        public MonitoringSeriesKey Key => new(Client.TenantId, Rule.RuleId, Client.AgentId, "cpu");

        public static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var rig = new Rig { Connection = await postgres.CreateDatabaseAsync() };
            rig.Provider = rig.NewProvider();
            await using (var scope = rig.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = rig.Client.TenantId, Name = "Bridge tenant" });
                db.Agents.Add(new() { Id = rig.Client.AgentId, TenantId = rig.Client.TenantId, CreatedAtUtc = rig.Clock.GetUtcNow() });
                await db.SaveChangesAsync();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                await identity.Database.MigrateAsync();
                identity.ApplicationPrincipals.Add(new() { Id = rig.Authority.PrincipalId });
                var role = new AccessRole { Name = "Exact tenant flow execution" };
                role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowRead });
                role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowExecute });
                identity.AccessRoles.Add(role);
                identity.PrincipalRoleAssignments.Add(new() { PrincipalId = rig.Authority.PrincipalId, RoleId = role.Id, TenantId = rig.Client.TenantId });
                identity.IntegrationCredentials.Add(new()
                {
                    Id = rig.Authority.IntegrationCredentialId!, PublicId = "bridge-fixture-public-id",
                    OwnerPrincipalId = rig.Authority.PrincipalId, Name = "Pinned bridge credential", Purpose = IntegrationCredentialPurpose.Api,
                    TokenPrefix = "nrt_ic_fixture", SecretHash = "deterministic-test-only", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(2),
                    Grants = [new() { CredentialId = rig.Authority.IntegrationCredentialId!, TenantId = rig.Client.TenantId, Permission = NetRatelPermissions.FlowRead },
                        new() { CredentialId = rig.Authority.IntegrationCredentialId!, TenantId = rig.Client.TenantId, Permission = NetRatelPermissions.FlowExecute }]
                });
                await identity.SaveChangesAsync();
            }
            var flow = (await rig.Definitions.CreateAsync(rig.Client.TenantId, new("Actual bridge flow"), rig.Authority.PrincipalId)).Definition!;
            var draft = (await rig.Definitions.SaveDraftAsync(rig.Client.TenantId, flow.Id,
                new(flow.Revision, flow.Name, FlowTestData.Graph()), rig.Authority.PrincipalId)).Definition!;
            var published = await rig.Definitions.PublishAsync(rig.Client.TenantId, flow.Id, new(draft.Revision), rig.Authority);
            published.Disposition.Should().Be(FlowWriteDisposition.Stored); rig.Version = published.Version!;
            rig.Clock.ResetToSystem();
            var ownership = rig.Provider.GetRequiredService<IClientConnectionEpochStore>();
            var now = rig.Clock.GetUtcNow();
            var reservation = (await ownership.ReserveAsync(new(rig.Client, Guid.NewGuid(), Guid.NewGuid(), 0, now,
                now.AddSeconds(30), now.AddMinutes(10), new("bridge-provider", ["presence"], null)), default)).Reservation!;
            (await ownership.CommitAsync(reservation, new(reservation.Owner, 1, rig.Clock.GetUtcNow()), default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
            rig.Evidence.Current = (await rig.Store.ReserveEvidenceRegistrationAsync(rig.Client, reservation.Owner.ConnectionId,
                reservation.Owner.Epoch, Guid.NewGuid(), default))!;
            rig.Rule = new(rig.Client.TenantId, Guid.NewGuid(), 1, 1, "Bridge CPU threshold", true, MonitoringSeverity.Critical,
                new(MonitoringTargetMode.Selected, [rig.Client.AgentId], []),
                new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 90, 75, null, null, []),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), rig.Version.Id,
                rig.Authority.PrincipalId, rig.Authority.IntegrationCredentialId);
            (await rig.Configuration.SaveRuleAsync(new(rig.Rule, 0, Guid.Parse(rig.Authority.PrincipalId), "Configure actual bridge proof"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
            rig.System = ActorSystem.Create("bridge-pg-" + Guid.NewGuid().ToString("N"));
            rig.Actor = rig.System.ActorOf(ClientMonitoringRouterActor.Props(rig.Store, rig.Configuration,
                rig.Provider.GetRequiredService<IMonitoringClientDirectory>(), rig.Clock));
            (await rig.Actor.Ask<MonitoringInputResult>(new BeginMonitoringStream(rig.Evidence.Current))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            return rig;
        }

        public ServiceProvider NewProvider(IInterceptor? interceptor = null)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddDbContext<OrchestratorDbContext>(options =>
            {
                options.UseNpgsql(Connection);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(Connection));
            services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
            services.AddSingleton(Evidence);
            services.AddSingleton<IMonitoringClientDirectory, Directory>();
            services.AddNetRatelClientServicesPersistence().AddNetRatelMonitoringPersistence().AddNetRatelFlows();
            services.AddSingleton<IFlowConnectorCatalog, Catalog>();
            services.AddSingleton<IFlowIncidentActionDispatcher>(Dispatcher);
            services.AddMonitoringFlowBridge();
            services.AddSingleton<CountingIngress>(provider => new(provider.GetRequiredService<FlowPersistenceService>()));
            services.Replace(ServiceDescriptor.Singleton<IFlowEventIngress>(provider => provider.GetRequiredService<CountingIngress>()));
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        public async Task<MonitoringSeriesState> FireAsync()
        {
            for (ulong sequence = 1; sequence <= 3; sequence++) await RecordAsync(sequence);
            var state = (await Store.LoadSeriesAsync(Key, default))!;
            state.Phase.Should().Be(MonitoringPhase.Firing);
            return state;
        }
        public async Task RecordAsync(ulong sequence)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var fence = Evidence.Current!;
            var telemetry = new MonitoringTelemetryInput(fence, new(Client, fence.ConnectionEpoch, sequence,
                Clock.GetUtcNow(), Clock.GetUtcNow(), new(95, null, null), null, [], [], null, IsAuthoritative: true));
            (await Actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(telemetry))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
        }
        public async Task<MonitoringOutboxLease> ClaimAsync() =>
            (await Store.ClaimOutboxAsync(new(Client.TenantId, Guid.NewGuid(), 4, TimeSpan.FromMinutes(1)), default)).Single();
        public async Task AssertOneEffectAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.MonitoringEvents.CountAsync()).Should().Be(1);
            (await db.MonitoringFlowOutbox.CountAsync()).Should().Be(1);
            (await db.FlowRuns.CountAsync()).Should().Be(1);
            var action = await db.FlowActions.SingleAsync();
            action.Status.Should().Be(FlowActionStatus.Succeeded);
            JsonSerializer.Deserialize<FlowActionReceiptDto>(action.ReceiptJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.IncidentId.Should().Be("bridge-incident");
            Dispatcher.Sends.Should().Be(1);
        }
        public async ValueTask DisposeAsync()
        {
            if (System is not null) await System.Terminate();
            await Provider.DisposeAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = ReadSystemAtPostgresPrecision();
        public void ResetToSystem() => _now = ReadSystemAtPostgresPrecision();
        private static DateTimeOffset ReadSystemAtPostgresPrecision()
        {
            var now = DateTimeOffset.UtcNow;
            return new(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class EvidenceSource { public MonitoringEvidenceFence? Current { get; set; } }
    private sealed class Directory(IMonitoringAgentEligibility eligibility, EvidenceSource source) : IMonitoringClientDirectory
    {
        public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken) => eligibility.GetEligibleAgentsAsync(tenantId, cancellationToken);
        public Task<bool> IsEligibleAsync(ClientKey client, CancellationToken cancellationToken) => eligibility.IsEligibleAsync(client, cancellationToken);
        public Task<bool> IsPresentedEvidenceAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken) => Task.FromResult(fence == source.Current);
        public Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(source.Current?.Client == client ? source.Current : null);
    }
    private sealed class Catalog : IFlowConnectorCatalog
    {
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            Task.FromResult<FlowConnectorReferenceDto?>(tenantId == 17 && connectorId == FlowTestData.ConnectorId
                ? new(connectorId, tenantId, "Deterministic in-process receiver", true, true, Revision: 1) : null);
        public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            (await GetAsync(tenantId, FlowTestData.ConnectorId, authority, cancellationToken)) is { } item ? [item] : [];
    }
    private sealed class Dispatcher : IFlowIncidentActionDispatcher
    {
        public int Preparations { get; private set; }
        public int Sends { get; private set; }
        public FlowIncidentActionRequest? LastRequest { get; private set; }
        public Func<Task>? BeforePrepare { get; set; }
        public async Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
        {
            Preparations++;
            if (BeforePrepare is not null) await BeforePrepare();
            return new(FlowIncidentPreparationStatus.Ready, FlowTestData.Prepared(draft));
        }
        public Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Sends++; LastRequest = action;
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created",
                new("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge")));
        }
    }
    private sealed class CountingIngress(IFlowEventIngress inner) : IFlowEventIngress
    {
        public int Enqueues { get; private set; }
        public int Finds { get; private set; }
        public int Gets { get; private set; }
        public Task<FlowIngressResult> EnqueueAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default)
        { Enqueues++; return inner.EnqueueAsync(input, cancellationToken); }
        public Task<FlowRunSummaryDto?> GetOutcomeAsync(int tenantId, Guid runId, CancellationToken cancellationToken = default)
        { Gets++; return inner.GetOutcomeAsync(tenantId, runId, cancellationToken); }
        public Task<FlowRunSummaryDto?> FindOutcomeAsync(int tenantId, Guid eventId, Guid flowVersionId, CancellationToken cancellationToken = default)
        { Finds++; return inner.FindOutcomeAsync(tenantId, eventId, flowVersionId, cancellationToken); }
    }
}
