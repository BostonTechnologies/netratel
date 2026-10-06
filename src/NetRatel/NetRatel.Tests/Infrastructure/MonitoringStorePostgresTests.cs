using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using Akka.Actor;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.Monitoring;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Notifications;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class MonitoringStorePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    /// <summary>Real provider persistence with deterministic unit clocks; this is not the physical DriveInfo stimulus proof.</summary>
    [Fact]
    public async Task Genuine_disk_collection_marker_survives_provider_restart_and_cached_frames_cannot_complete_or_rearm_hold()
    {
        await using var rig = await CreateRigAsync();
        var rule = rig.Rule with
        {
            RuleId = Guid.NewGuid(), Name = "genuine disk collections", BreachHold = TimeSpan.FromSeconds(30),
            RecoveryHold = TimeSpan.FromSeconds(30), FreshnessBudget = TimeSpan.FromMinutes(1),
            Condition = new(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.Bytes, 200, 500, null, null, [])
        };
        (await rig.Provider.GetRequiredService<IMonitoringConfigurationStore>().SaveRuleAsync(
            new(rule, 1, Guid.NewGuid(), "disk sample regression"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        var key = new MonitoringSeriesKey(rig.Client.TenantId, rule.RuleId, rig.Client.AgentId, "disk:/");
        var state = rig.Evaluator.CreateInitial(key, rule);
        MonitoringObservationDto Sample(ulong sequence, MonitoringDiskCollectionStamp stamp) =>
            new(key, new(rig.Fence.ConnectionEpoch, sequence), rig.Fence.EvidenceStreamId,
                stamp.CollectedAtUtc, rig.Time.GetUtcNow(), true, true, 100, DiskCollection: stamp);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var firstStamp = new MonitoringDiskCollectionStamp(Guid.NewGuid(), rig.Time.GetUtcNow(), new string('A', 64));
        var first = rig.Evaluator.Evaluate(state, rule, Sample(1, firstStamp), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        first.State.Phase.Should().Be(MonitoringPhase.Pending);
        (await rig.Store.CommitAsync(new(first, 2, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        var copied = rig.Evaluator.Evaluate(first.State, rule, Sample(2, firstStamp), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        copied.State.LatestEvidence.Should().Be(first.State.LatestEvidence);
        copied.State.WindowStartedAtUtc.Should().Be(first.State.WindowStartedAtUtc);
        copied.State.WindowStartedObservedAtUtc.Should().Be(first.State.WindowStartedObservedAtUtc);
        copied.Events.Should().BeEmpty(); copied.Outbox.Should().BeEmpty();
        (await rig.Store.CommitAsync(new(copied, 2, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);

        await using var restarted = CreateProvider(rig.Connection, rig.Directory, rig.Time);
        var store = restarted.GetRequiredService<IMonitoringStore>();
        var durable = (await store.LoadSeriesAsync(key, default))!;
        durable.LastDiskCollection.Should().Be(firstStamp);
        durable.LatestEvidence.Should().Be(first.State.LatestEvidence);
        durable.WindowStartedObservedAtUtc.Should().Be(first.State.WindowStartedObservedAtUtc);
        durable.Cursor!.Sequence.Should().Be(2);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        var secondStamp = new MonitoringDiskCollectionStamp(Guid.NewGuid(), rig.Time.GetUtcNow(), new string('B', 64));
        var raised = rig.Evaluator.Evaluate(durable, rule, Sample(3, secondStamp), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        raised.State.Phase.Should().Be(MonitoringPhase.Firing); raised.Events.Should().ContainSingle();
        (await store.CommitAsync(new(raised, 2, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        var occurrence = raised.State.Occurrence!.OccurrenceId;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var cleared = rig.Evaluator.Clear(raised.State, rule, Guid.NewGuid(), "cached samples must not rearm");
        (await store.CommitAsync(new(cleared, 2), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        durable = (await store.LoadSeriesAsync(key, default))!;
        durable.LastDiskCollection.Should().Be(secondStamp);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        var afterClear = rig.Evaluator.Evaluate(durable, rule, Sample(4, secondStamp), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        afterClear.State.Phase.Should().Be(MonitoringPhase.Cleared);
        afterClear.State.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
        afterClear.State.Occurrence!.OccurrenceId.Should().Be(occurrence);
        afterClear.State.WindowStartedAtUtc.Should().BeNull();
        afterClear.Events.Should().BeEmpty(); afterClear.Outbox.Should().BeEmpty();
        (await store.CommitAsync(new(afterClear, 2, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        await using var scope = restarted.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.MonitoringOccurrences.CountAsync(row => row.RuleId == rule.RuleId)).Should().Be(1);
        (await db.MonitoringEvents.CountAsync(row => row.OccurrenceId == occurrence)).Should().Be(2);
    }

    [Fact]
    public async Task Migration_and_concurrent_episode_commit_have_one_atomic_winner_and_scoped_history()
    {
        await using var rig = await CreateRigAsync(flow: true);
        await using (var scope = rig.Provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.HasPendingModelChanges().Should().BeFalse();
        var initial = rig.Evaluator.CreateInitial(rig.Key, rig.Rule);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var first = rig.Evaluator.Evaluate(initial, rig.Rule, rig.Observation(1), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(first, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var raised = rig.Evaluator.Evaluate(first.State, rig.Rule, rig.Observation(2), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        var outcomes = await Task.WhenAll(rig.Store.CommitAsync(new(raised, 1, rig.Fence), default), rig.Store.CommitAsync(new(raised, 1, rig.Fence), default));
        outcomes.Count(result => result.Disposition == MonitoringStoreWriteDisposition.Stored).Should().Be(1);
        outcomes.Count(result => result.Disposition == MonitoringStoreWriteDisposition.Conflict).Should().Be(1);
        await using var scope2 = rig.Provider.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.MonitoringOccurrences.CountAsync()).Should().Be(1);
        (await db.MonitoringEvents.CountAsync()).Should().Be(1);
        (await db.MonitoringFlowOutbox.CountAsync()).Should().Be(1);
        var mirror = await db.OutboxMessages.SingleAsync();
        mirror.Id.Should().Be(raised.Events.Single().EventId);
        mirror.Type.Should().StartWith(MonitoringLimits.NotificationEventPrefix);
        (await rig.Store.ReadTenantEventsAsync(rig.Client.TenantId + 1, 20, null, default)).Items.Should().BeEmpty();
        (await rig.Store.ReadTenantSummaryAsync(rig.Client.TenantId, default)).ActiveOccurrences.Should().Be(1);
        var acknowledgement = rig.Evaluator.Acknowledge(raised.State, rig.Rule, Guid.NewGuid());
        (await rig.Store.CommitAsync(new(acknowledgement, 0), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Conflict);
        (await db.MonitoringAudits.CountAsync()).Should().Be(1, "only the configuration audit exists after a rejected mutation");
    }

    [Fact]
    public async Task Lease_reclaim_preserves_action_identity_and_rejects_stale_worker_completion()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var firing = await FireAsync(rig);
        var first = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromSeconds(1)), default)).Single();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await using var restarted = CreateProvider(rig.Connection, rig.Directory, rig.Time);
        var store = restarted.GetRequiredService<IMonitoringStore>();
        var second = (await store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromMinutes(1)), default)).Single();
        second.OutboxId.Should().Be(first.OutboxId);
        second.Intent.StableFlowDispatchKey.Should().Be(first.Intent.StableFlowDispatchKey);
        second.LeaseFence.Should().BeGreaterThan(first.LeaseFence);
        var outcome = new MonitoringFlowOutcomeDto(Guid.NewGuid(), MonitoringFlowOutcomeKind.Succeeded, rig.Time.GetUtcNow());
        (await store.CompleteOutboxAsync(new(first, MonitoringOutboxStatus.Completed, outcome), default)).Should().BeFalse();
        (await store.CompleteOutboxAsync(new(second, MonitoringOutboxStatus.Completed, outcome), default)).Should().BeTrue();
        (await store.CompleteOutboxAsync(new(second, MonitoringOutboxStatus.Completed, outcome), default)).Should().BeFalse();
        var state = await store.LoadSeriesAsync(rig.Key, default);
        state!.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        state.Occurrence.FlowOutcome.Should().Be(outcome);
        (await store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Old_delayed_begin_cannot_overwrite_new_same_epoch_registration_and_old_end_is_rejected()
    {
        await using var rig = await CreateRigAsync();
        var old = (await rig.Store.ReserveEvidenceRegistrationAsync(rig.Client, rig.Fence.ConnectionId,
            rig.Fence.ConnectionEpoch, Guid.NewGuid(), default))!;
        // The old process has reserved its ordinal but its physical Begin is delayed. A genuinely independent directory models replica B.
        var otherDirectory = new TestMonitoringDirectory { Client = rig.Client };
        await using var otherProvider = CreateProvider(rig.Connection, otherDirectory, rig.Time);
        var other = otherProvider.GetRequiredService<IMonitoringStore>();
        var current = (await other.ReserveEvidenceRegistrationAsync(rig.Client, rig.Fence.ConnectionId,
            rig.Fence.ConnectionEpoch, Guid.NewGuid(), default))!;
        current.RegistrationOrdinal.Should().BeGreaterThan(old.RegistrationOrdinal);
        otherDirectory.Current = current;
        (await other.BeginEvidenceStreamAsync(current, default)).Should().BeTrue();
        rig.Directory.Current = old;
        (await rig.Store.BeginEvidenceStreamAsync(old, default)).Should().BeFalse();
        (await rig.Store.EndEvidenceStreamAsync(old, default)).Should().BeFalse();
        (await other.BeginEvidenceStreamAsync(current, default)).Should().BeTrue();
        var state = rig.Evaluator.CreateInitial(rig.Key, rig.Rule);
        var invalid = rig.Evaluator.Evaluate(state, rig.Rule, rig.Observation(1) with { EvidenceStreamId = old.EvidenceStreamId },
            rig.Fence.ConnectionEpoch, old.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(invalid, 1, old), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.StaleEvidence);
    }

    [Fact]
    public async Task Pending_registration_cancelled_before_Begin_commit_preserves_old_durable_stream_and_ingress()
    {
        await using var rig = await CreateRigAsync();
        var pending = (await rig.Store.ReserveEvidenceRegistrationAsync(rig.Client, rig.Fence.ConnectionId,
            rig.Fence.ConnectionEpoch, Guid.NewGuid(), default))!;
        rig.Directory.Current = pending;
        await using var blocker = new Npgsql.NpgsqlConnection(rig.Connection); await blocker.OpenAsync();
        await using var held = await blocker.BeginTransactionAsync();
        await using (var command = new Npgsql.NpgsqlCommand("SELECT * FROM \"MonitoringEvidenceStreams\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent FOR UPDATE", blocker, held))
        {
            command.Parameters.AddWithValue("tenant", rig.Client.TenantId); command.Parameters.AddWithValue("agent", rig.Client.AgentId);
            await using var read = await command.ExecuteReaderAsync(); (await read.ReadAsync()).Should().BeTrue();
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = new CancellationTokenSource();
        var begin = rig.Store.BeginEvidenceStreamAsync(pending, cancel.Token);
        try
        {
            await using var observer = new Npgsql.NpgsqlConnection(rig.Connection); await observer.OpenAsync(budget.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (true)
            {
                await using var command = new Npgsql.NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=@database AND cardinality(pg_blocking_pids(pid))>0 AND query LIKE '%MonitoringEvidenceStreams%')", observer);
                command.Parameters.AddWithValue("database", observer.Database);
                if ((bool)(await command.ExecuteScalarAsync(budget.Token))!) break;
                begin.IsCompleted.Should().BeFalse(); await timer.WaitForNextTickAsync(budget.Token);
            }
            await using var inspect = rig.Provider.CreateAsyncScope();
            var row = await inspect.ServiceProvider.GetRequiredService<OrchestratorDbContext>().MonitoringEvidenceStreams.AsNoTracking().SingleAsync(budget.Token);
            row.Active.Should().BeTrue(); row.EvidenceStreamId.Should().Be(rig.Fence.EvidenceStreamId);
            row.CommittedRegistrationOrdinal.Should().Be(rig.Fence.RegistrationOrdinal);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await begin);
        }
        finally
        {
            cancel.Cancel(); await held.RollbackAsync();
            try { await begin; } catch (OperationCanceledException) { }
        }
        (await rig.Store.EndEvidenceStreamAsync(pending, default)).Should().BeFalse();
        var initial = rig.Evaluator.CreateInitial(rig.Key, rig.Rule);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var admitted = rig.Evaluator.Evaluate(initial, rig.Rule, rig.Observation(1), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(admitted, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
    }

    [Fact]
    public async Task Issued_new_epoch_fences_old_ingress_and_unstarted_action_before_new_monitoring_evidence()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var firing = await FireAsync(rig);
        var ownership = rig.Provider.GetRequiredService<IClientConnectionEpochStore>();
        var candidate = (await ownership.ReserveAsync(new(rig.Client, Guid.NewGuid(), Guid.NewGuid(), 0,
            rig.Time.GetUtcNow(), rig.Time.GetUtcNow().AddSeconds(30), rig.Time.GetUtcNow().AddMinutes(10),
            new("replacement", [], null)), default)).Reservation!;
        candidate.Owner.Epoch.Should().BeGreaterThan(rig.Fence.ConnectionEpoch);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var old = rig.Evaluator.Evaluate(firing, rig.Rule, rig.Observation(3), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(old, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored,
            "reservation alone must not fence a valid committed owner");
        var eligible = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromSeconds(1)), default)).Single();
        eligible.Intent.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        (await ownership.CommitAsync(candidate, new(candidate.Owner, 1, rig.Time.GetUtcNow()), default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var after = rig.Evaluator.Evaluate(old.State, rig.Rule, rig.Observation(4), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(after, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.StaleEvidence);
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Cursor!.Sequence.Should().Be(3);
    }

    [Fact]
    public async Task Legacy_notification_reads_and_mutations_cannot_touch_monitoring_mirror_or_episode()
    {
        await using var rig = await CreateRigAsync();
        var firing = await FireAsync(rig);
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var service = new NetRatelNotificationService(db, new NetRatelNotificationDisplaySanitizer(new TestDisplayNames()));
        var eventId = firing.Occurrence!.RaisedEventId;
        (await service.GetByIdAsync(eventId, "foreign-audit-reader", default)).Should().BeNull();
        (await service.GetSummaryAsync("foreign-audit-reader", default)).TotalCount.Should().Be(0);
        (await service.GetUnreadErrorsAsync("foreign-audit-reader", 100, default)).Should().BeEmpty();
        (await service.MarkReadAsync("foreign-audit-reader", [eventId], default)).Should().Be(0);
        Func<Task> retry = () => service.RetryAsync(eventId, default);
        Func<Task> disable = () => service.DisableAsync(eventId, default);
        await retry.Should().ThrowAsync<InvalidOperationException>();
        await disable.Should().ThrowAsync<InvalidOperationException>();
        (await db.OutboxReadReceipts.CountAsync()).Should().Be(0);
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.EndedAtUtc.Should().BeNull();
        (await db.OutboxMessages.SingleAsync()).Status.Should().Be(OutboxStatuses.Published);
    }

    [Fact]
    public async Task Retention_preserves_latest_closed_occurrence_and_next_sample_remains_accepted()
    {
        await using var rig = await CreateRigAsync();
        var firing = await FireAsync(rig);
        var clear = rig.Evaluator.Clear(firing, rig.Rule, Guid.NewGuid(), "manual validation");
        (await rig.Store.CommitAsync(new(clear, 1), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        rig.Time.Advance(MonitoringLimits.HistoryRetention + TimeSpan.FromDays(1));
        await rig.ReconnectCommittedOwnerAsync();
        // Configuration audit invokes cleanup after the horizon, even while no new occurrence is raised.
        var renamed = rig.Rule with { Revision = 2, Name = "renamed after retention" };
        (await rig.Provider.GetRequiredService<IMonitoringConfigurationStore>().SaveRuleAsync(new(renamed, 1, Guid.NewGuid(), "rename"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        var current = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        var next = rig.Evaluator.Evaluate(current, renamed, rig.Observation(3) with { Complete = false }, rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        (await rig.Store.CommitAsync(new(next, 2, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        await using var scope = rig.Provider.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().MonitoringOccurrences.SingleAsync();
        history.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        history.EndedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Real_actor_restart_resets_pending_continuity_preserves_firing_episode_and_requires_new_window()
    {
        await using var rig = await CreateRigAsync();
        var system = ActorSystem.Create("monitoring-pg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var actor = system.ActorOf(ClientMonitoringRouterActor.Props(rig.Store, rig.Provider.GetRequiredService<IMonitoringConfigurationStore>(), rig.Directory, rig.Time));
            (await actor.Ask<MonitoringInputResult>(new BeginMonitoringStream(rig.Fence))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(1)))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single().EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(2)))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single().Phase.Should().Be(MonitoringPhase.Pending);
            await actor.GracefulStop(TimeSpan.FromSeconds(3));
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            actor = system.ActorOf(ClientMonitoringRouterActor.Props(rig.Store, rig.Provider.GetRequiredService<IMonitoringConfigurationStore>(), rig.Directory, rig.Time));
            var afterRestart = (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single();
            afterRestart.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
            afterRestart.Occurrence.Should().BeNull();
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(3)))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            var pending = (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single();
            pending.Phase.Should().Be(MonitoringPhase.Pending); pending.Occurrence.Should().BeNull();
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(4)))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            var firing = (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single();
            firing.Phase.Should().Be(MonitoringPhase.Firing);
            await actor.GracefulStop(TimeSpan.FromSeconds(3));
            actor = system.ActorOf(ClientMonitoringRouterActor.Props(rig.Store, rig.Provider.GetRequiredService<IMonitoringConfigurationStore>(), rig.Directory, rig.Time));
            var restarted = (await actor.Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(rig.Client))).Single();
            restarted.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
            restarted.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
            (await rig.Store.ReadTenantEventsAsync(rig.Client.TenantId, 20, null, default)).Items.Count(item => item.Kind == MonitoringEventKind.AlertRaised).Should().Be(1);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Disable_and_bypass_changes_are_atomic_with_active_episode_and_unstarted_actions()
    {
        await using var rig = await CreateRigAsync(flow: true);
        await FireAsync(rig);
        var bypass = new MonitoringBypassDto(Guid.NewGuid(), rig.Client.TenantId, rig.Rule.RuleId, null, null, Guid.NewGuid(), "maintenance", rig.Time.GetUtcNow(), rig.Time.GetUtcNow().AddMinutes(1));
        var configurations = rig.Provider.GetRequiredService<IMonitoringConfigurationStore>();
        (await configurations.SaveBypassAsync(new(bypass, 1), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Suppressed.Should().BeTrue();
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 10, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        (await configurations.SaveRuleAsync(new(rig.Rule with { Revision = 2, Enabled = false }, 2, Guid.NewGuid(), "disable"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        var state = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        state.Phase.Should().Be(MonitoringPhase.Suspended);
        state.Occurrence!.ClosureDisposition.Should().Be(MonitoringClosureDisposition.RuleDisabled);
        await using var scope = rig.Provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().MonitoringFlowOutbox.SingleAsync()).Status.Should().Be(MonitoringOutboxStatus.Cancelled);
    }

    [Fact]
    public async Task Byte_budgeted_tenant_pages_have_lossless_continuation_for_large_pinned_rules()
    {
        await using var rig = await CreateRigAsync();
        var firing = await FireAsync(rig);
        var largeRule = rig.Rule with { Targets = new(MonitoringTargetMode.Selected, Enumerable.Range(0, 4096).Select(_ => Guid.NewGuid()).ToImmutableArray(), []) };
        await using (var scope = rig.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            for (var index = 0; index < 35; index++)
            {
                var key = rig.Key with { AgentId = Guid.NewGuid() };
                var occurrence = firing.Occurrence! with { OccurrenceId = Guid.NewGuid(), RaisedEventId = Guid.NewGuid(), PinnedRule = largeRule };
                var state = firing with { Series = key, Occurrence = occurrence };
                db.MonitoringSeries.Add(new() { TenantId = key.TenantId, AgentId = key.AgentId, RuleId = key.RuleId, ResourceKey = key.ResourceKey,
                    StateRevision = state.StateRevision, Phase = state.Phase, EvidenceQuality = state.EvidenceQuality, ActiveOccurrenceId = occurrence.OccurrenceId,
                    LatestOccurrenceId = occurrence.OccurrenceId, StateJson = JsonSerializer.Serialize(state), UpdatedAtUtc = rig.Time.GetUtcNow() });
                db.MonitoringOccurrences.Add(new() { OccurrenceId = occurrence.OccurrenceId, TenantId = key.TenantId, AgentId = key.AgentId, RuleId = key.RuleId, ResourceKey = key.ResourceKey,
                    RaisedEventId = occurrence.RaisedEventId, RaisedAtUtc = occurrence.RaisedAtUtc, OccurrenceJson = JsonSerializer.Serialize(occurrence) });
                var intent = new MonitoringEventIntent(occurrence.RaisedEventId, MonitoringEventKind.AlertRaised, key, occurrence.OccurrenceId, rig.Time.GetUtcNow(), largeRule, occurrence.RaisedEvidence);
                db.MonitoringEvents.Add(new() { EventId = intent.EventId, OccurrenceId = intent.OccurrenceId, TenantId = key.TenantId, AtUtc = intent.AtUtc, EventJson = JsonSerializer.Serialize(intent) });
            }
            await db.SaveChangesAsync();
        }
        var page = await rig.Store.ReadTenantSeriesAsync(rig.Client.TenantId, 200, null, default);
        page.NextCursor.Should().NotBeNull(); JsonSerializer.SerializeToUtf8Bytes(page).Length.Should().BeLessThan(4 * 1024 * 1024);
        var second = await rig.Store.ReadTenantSeriesAsync(rig.Client.TenantId, 200, page.NextCursor, default);
        page.Items.Concat(second.Items).Select(state => state.Series).Distinct().Count().Should().Be(36);
        var events = await rig.Store.ReadTenantEventsAsync(rig.Client.TenantId, 200, null, default);
        events.NextCursor.Should().NotBeNull(); JsonSerializer.SerializeToUtf8Bytes(events).Length.Should().BeLessThan(4 * 1024 * 1024);
        var nextEvents = await rig.Store.ReadTenantEventsAsync(rig.Client.TenantId, 200, events.NextCursor, default);
        events.Items.Concat(nextEvents.Items).Select(item => item.EventId).Distinct().Count().Should().Be(36);
        Func<Task> foreignCursor = async () => await rig.Store.ReadTenantSeriesAsync(rig.Client.TenantId + 1, 200, page.NextCursor, default);
        await foreignCursor.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Configuration_reconciliation_crosses_batches_atomically_and_store_rejects_foreign_group_members()
    {
        await using var rig = await CreateRigAsync();
        await using (var scope = rig.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            for (var index = 0; index < 65; index++)
            {
                var agent = Guid.NewGuid(); var key = rig.Key with { AgentId = agent };
                var state = rig.Evaluator.CreateInitial(key, rig.Rule) with { StateRevision = 1 };
                db.MonitoringSeries.Add(new() { TenantId = key.TenantId, AgentId = agent, RuleId = key.RuleId, ResourceKey = key.ResourceKey,
                    StateRevision = state.StateRevision, Phase = state.Phase, EvidenceQuality = state.EvidenceQuality, StateJson = JsonSerializer.Serialize(state), UpdatedAtUtc = rig.Time.GetUtcNow() });
            }
            await db.SaveChangesAsync();
        }
        var configurations = rig.Provider.GetRequiredService<IMonitoringConfigurationStore>();
        var renamed = rig.Rule with { Revision = 2, Name = "batched rename" };
        (await configurations.SaveRuleAsync(new(renamed, 1, Guid.NewGuid(), "rename"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        var after = await configurations.GetAsync(rig.Client.TenantId, default);
        after.Revision.Should().Be(2); after.Rules.Single().Name.Should().Be("batched rename");
        await using var verify = rig.Provider.CreateAsyncScope();
        var db2 = verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db2.MonitoringSeries.CountAsync(row => row.Phase == MonitoringPhase.Suspended || row.Phase == MonitoringPhase.NotApplicable)).Should().Be(65);
        var foreign = new MonitoringGroupDto(rig.Client.TenantId, Guid.NewGuid(), 1, "foreign", [Guid.NewGuid()]);
        Func<Task> save = () => configurations.SaveGroupAsync(new(foreign, 2, Guid.NewGuid(), "group"), default);
        await save.Should().ThrowAsync<ArgumentException>();
        (await configurations.GetAsync(rig.Client.TenantId, default)).Revision.Should().Be(2);
    }

    [Fact]
    public async Task Missing_pinned_flow_and_exhausted_safe_retries_are_terminal_without_inventing_run_ids()
    {
        await using var rig = await CreateRigAsync(flow: true);
        await FireAsync(rig);
        ((TestPublishedFlows)rig.Provider.GetRequiredService<IMonitoringPublishedFlowProvider>()).Available = false;
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        var state = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        state.Occurrence!.FlowDispatchDisposition.Should().Be(MonitoringFlowDispatchDisposition.Failed);
        state.Occurrence.FlowOutcome!.FlowRunId.Should().BeNull();
        state.Occurrence.FlowOutcome.Code.Should().Be("published_flow_unavailable");
        await using var retries = await CreateRigAsync(flow: true);
        await FireAsync(retries);
        for (var attempt = 1; attempt <= MonitoringLimits.MaximumOutboxAttempts; attempt++)
        {
            var lease = (await retries.Store.ClaimOutboxAsync(new(retries.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
            lease.Attempt.Should().Be(attempt);
            (await retries.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "capacity"), default)).Should().BeTrue();
            await AdvanceSafeRetryBackoffWithHeartbeatsAsync(retries, TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt))));
            if (attempt < MonitoringLimits.MaximumOutboxAttempts)
            {
                var current = (await retries.Store.LoadSeriesAsync(retries.Key, default))!;
                var result = retries.Evaluator.Evaluate(current, retries.Rule, retries.Observation((ulong)attempt + 2), retries.Fence.ConnectionEpoch, retries.Fence.EvidenceStreamId, []);
                await retries.Store.CommitAsync(new(result, 1, retries.Fence), default);
            }
        }
        (await retries.Store.ClaimOutboxAsync(new(retries.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        (await retries.Store.LoadSeriesAsync(retries.Key, default))!.Occurrence!.FlowOutcome!.Code.Should().Be("attempt_limit");
    }

    [Fact]
    public async Task Actor_rejects_unbounded_backlog_and_failed_persistence_before_returning_accepted_state()
    {
        await using var rig = await CreateRigAsync();
        var control = new ControlSeriesWrite();
        await using var provider = CreateProvider(rig.Connection, rig.Directory, rig.Time, control);
        var system = ActorSystem.Create("monitoring-bounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = provider.GetRequiredService<IMonitoringStore>();
            var actor = system.ActorOf(ClientMonitoringRouterActor.Props(store, provider.GetRequiredService<IMonitoringConfigurationStore>(), rig.Directory, rig.Time, maximumClients: 1));
            control.FailNext = true;
            (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(1)))).Disposition.Should().Be(MonitoringInputDisposition.PersistenceUnavailable);
            (await rig.Store.LoadClientAsync(rig.Client, default)).Should().BeEmpty();
            control.Pause = true;
            var first = actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(1)), TimeSpan.FromSeconds(15));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await control.Entered.Task.WaitAsync(timeout.Token);
            var queued = Enumerable.Range(2, ClientMonitoringRouterActor.MaximumOutstandingPerClient - 1).Select(index =>
                actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry((ulong)index)), TimeSpan.FromSeconds(15))).ToArray();
            try
            {
                (await actor.Ask<MonitoringRouteDiagnostics>(new GetMonitoringRouteDiagnostics())).OutstandingRequests.Should().Be(16);
                (await actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(rig.Telemetry(100)))).Disposition.Should().Be(MonitoringInputDisposition.CapacityExceeded);
                var other = rig.Fence with { Client = new(rig.Client.TenantId + 1, rig.Client.AgentId) };
                (await actor.Ask<MonitoringInputResult>(new BeginMonitoringStream(other))).Disposition.Should().Be(MonitoringInputDisposition.CapacityExceeded);
            }
            finally { control.Pause = false; control.Resume.TrySetResult(); }
            (await first).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            (await Task.WhenAll(queued)).Should().OnlyContain(result => result.Disposition == MonitoringInputDisposition.Accepted);
        }
        finally { control.Pause = false; control.Resume.TrySetResult(); await system.Terminate(); }
    }

    [Fact]
    public async Task PostgreSql_microsecond_lease_roundtrip_completes_with_submicrosecond_clock_ticks()
    {
        await using var rig = await CreateRigAsync(flow: true);
        rig.Time.Advance(TimeSpan.FromTicks(7));
        await FireAsync(rig);
        var lease = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(3)), default)).Single();
        (lease.LeaseExpiresAtUtc.UtcTicks % 10).Should().Be(0);
        var outcome = new MonitoringFlowOutcomeDto(Guid.NewGuid(), MonitoringFlowOutcomeKind.Succeeded, rig.Time.GetUtcNow());
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Completed, outcome), default)).Should().BeTrue();
    }

    [Fact]
    public async Task Durable_known_run_survives_clear_offline_restart_and_reconciles_only_exact_historical_episode()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var first = await FireAsync(rig);
        var lease = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
        var runId = Guid.NewGuid();
        (await rig.Store.MarkOutboxHandedOffAsync(lease, runId, default)).Should().BeTrue();
        (await rig.Store.MarkOutboxHandedOffAsync(lease, runId, default)).Should().BeTrue();
        (await rig.Store.MarkOutboxHandedOffAsync(lease, Guid.NewGuid(), default)).Should().BeFalse();
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "flow_running"), default)).Should().BeTrue();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var cleared = rig.Evaluator.Clear(first, rig.Rule, Guid.NewGuid(), "clear while flow runs");
        await rig.Store.CommitAsync(new(cleared, 1), default);
        rig.Directory.Current = null;
        await using var restarted = CreateProvider(rig.Connection, rig.Directory, rig.Time);
        var store = restarted.GetRequiredService<IMonitoringStore>();
        var recovery = (await store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
        recovery.FlowRunId.Should().Be(runId); recovery.OutboxId.Should().Be(lease.OutboxId);
        recovery.Attempt.Should().Be(lease.Attempt, "polling a known run never consumes a dispatch retry or creates a new run");
        var uncertain = new MonitoringFlowOutcomeDto(runId, MonitoringFlowOutcomeKind.DeliveryUnknown, rig.Time.GetUtcNow(), "flow_running");
        (await store.CompleteOutboxAsync(new(recovery, MonitoringOutboxStatus.DeliveryUnknown, uncertain), default)).Should().BeTrue();
        var receipts = await store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default);
        receipts.Should().ContainSingle();
        (await store.ListUnsettledFlowRunsAsync(rig.Client.TenantId + 1, 10, default)).Should().BeEmpty();
        var receipt = receipts.Single();
        rig.Directory.Current = rig.Fence;
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var current = (await store.LoadSeriesAsync(rig.Key, default))!;
        var pending = rig.Evaluator.Evaluate(current, rig.Rule, rig.Observation(3), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        await store.CommitAsync(new(pending, 1, rig.Fence), default);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var next = rig.Evaluator.Evaluate(pending.State, rig.Rule, rig.Observation(4), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        await store.CommitAsync(new(next, 1, rig.Fence), default);
        rig.Directory.Current = null;
        var outcome = new MonitoringFlowOutcomeDto(runId, MonitoringFlowOutcomeKind.Succeeded, rig.Time.GetUtcNow());
        var forged = receipt with { Intent = receipt.Intent with { EventId = Guid.NewGuid() } };
        (await store.ReconcileOutboxOutcomeAsync(forged, outcome, default)).Should().BeFalse();
        (await store.ReconcileOutboxOutcomeAsync(receipt, outcome, default)).Should().BeTrue();
        (await store.ReconcileOutboxOutcomeAsync(receipt, outcome, default)).Should().BeTrue();
        var latest = (await store.LoadSeriesAsync(rig.Key, default))!;
        latest.Occurrence!.OccurrenceId.Should().Be(next.State.Occurrence!.OccurrenceId);
        latest.Occurrence.FlowOutcome.Should().BeNull();
        await using var scope = restarted.CreateAsyncScope();
        var historical = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().MonitoringOccurrences.SingleAsync(row => row.OccurrenceId == first.Occurrence!.OccurrenceId);
        JsonSerializer.Deserialize<MonitoringOccurrenceDto>(historical.OccurrenceJson)!.FlowOutcome.Should().Be(outcome);
        (await store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_handoff_lease_cannot_attach_a_run_after_worker_fence_changes()
    {
        await using var rig = await CreateRigAsync(flow: true);
        await FireAsync(rig);
        var old = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromSeconds(1)), default)).Single();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var current = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
        (await rig.Store.MarkOutboxHandedOffAsync(old, Guid.NewGuid(), default)).Should().BeFalse();
        var runId = Guid.NewGuid();
        (await rig.Store.MarkOutboxHandedOffAsync(current, runId, default)).Should().BeTrue();
        (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Single().FlowRunId.Should().Be(runId);
    }

    [Fact]
    public async Task Clear_after_flow_enqueue_but_before_handoff_marker_keeps_lookup_only_unknown_receipt()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var firing = await FireAsync(rig);
        var lease = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromSeconds(1)), default)).Single();
        // The real flow store accepted a run, then the worker died before MarkHandedOff. No new run may be dispatched by this receipt.
        var persistedRunId = Guid.NewGuid();
        await rig.Store.CommitAsync(new(rig.Evaluator.Clear(firing, rig.Rule, Guid.NewGuid(), "clear during lost handoff"), 1), default);
        rig.Time.Advance(TimeSpan.FromSeconds(2)); rig.Directory.Current = null;
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Single();
        receipt.OutboxId.Should().Be(lease.OutboxId); receipt.FlowRunId.Should().BeNull();
        var unknown = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        unknown.Phase.Should().Be(MonitoringPhase.Cleared);
        unknown.Occurrence!.FlowOutcome!.Outcome.Should().Be(MonitoringFlowOutcomeKind.DeliveryUnknown);
        unknown.Occurrence.FlowOutcome.FlowRunId.Should().BeNull();
        (await rig.Store.MarkOutboxHandedOffAsync(lease, persistedRunId, default)).Should().BeFalse();
        var terminal = new MonitoringFlowOutcomeDto(persistedRunId, MonitoringFlowOutcomeKind.Succeeded, rig.Time.GetUtcNow());
        var forged = receipt with { Intent = receipt.Intent with { PublishedFlowVersionId = Guid.NewGuid() } };
        (await rig.Store.ReconcileOutboxOutcomeAsync(forged, terminal, default)).Should().BeFalse();
        (await rig.Store.ReconcileOutboxOutcomeAsync(receipt, terminal, default)).Should().BeTrue();
        var final = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        final.Phase.Should().Be(MonitoringPhase.Cleared); final.Occurrence!.FlowOutcome.Should().Be(terminal);
        (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Known_run_pending_age_expires_to_unknown_with_real_identity_and_lookup_only_reconciliation()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var firing = await FireAsync(rig);
        var lease = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
        var runId = Guid.NewGuid();
        (await rig.Store.MarkOutboxHandedOffAsync(lease, runId, default)).Should().BeTrue();
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "flow_running"), default)).Should().BeTrue();
        await rig.Store.CommitAsync(new(rig.Evaluator.Clear(firing, rig.Rule, Guid.NewGuid(), "clear during pending flow"), 1), default);
        rig.Directory.Current = null;
        rig.Time.Advance(TimeSpan.FromMinutes(16));
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        var unknown = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        unknown.Phase.Should().Be(MonitoringPhase.Cleared);
        unknown.Occurrence!.FlowOutcome!.Outcome.Should().Be(MonitoringFlowOutcomeKind.DeliveryUnknown);
        unknown.Occurrence.FlowOutcome.FlowRunId.Should().Be(runId);
        unknown.Occurrence.FlowOutcome.Code.Should().Be("handed_off_outcome_unknown");
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Single();
        receipt.OutboxId.Should().Be(lease.OutboxId); receipt.FlowRunId.Should().Be(runId);
        (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Should().BeEmpty();
        var outcome = new MonitoringFlowOutcomeDto(runId, MonitoringFlowOutcomeKind.Succeeded, rig.Time.GetUtcNow());
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Completed, outcome), default)).Should().BeFalse();
        (await rig.Store.ReconcileOutboxOutcomeAsync(receipt, outcome, default)).Should().BeTrue();
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome.Should().Be(outcome);
        (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Exhausted_pending_completion_after_a_new_episode_preserves_unknown_historical_delivery()
    {
        await using var rig = await CreateRigAsync(flow: true);
        var first = await FireAsync(rig);
        // Earlier safe retries are covered separately; recover the durable last permitted attempt here.
        await using (var scope = rig.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.MonitoringFlowOutbox.SingleAsync(item => item.OccurrenceId == first.Occurrence!.OccurrenceId);
            row.Attempts = MonitoringLimits.MaximumOutboxAttempts - 1;
            await db.SaveChangesAsync();
        }
        var lease = (await rig.Store.ClaimOutboxAsync(new(rig.Client.TenantId, Guid.NewGuid(), 1, TimeSpan.FromMinutes(1)), default)).Single();
        lease.Attempt.Should().Be(MonitoringLimits.MaximumOutboxAttempts);
        var cleared = rig.Evaluator.Clear(first, rig.Rule, Guid.NewGuid(), "clear while last handoff completes");
        await rig.Store.CommitAsync(new(cleared, 1), default);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var pending = rig.Evaluator.Evaluate(cleared.State, rig.Rule, rig.Observation(3), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        await rig.Store.CommitAsync(new(pending, 1, rig.Fence), default);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var next = rig.Evaluator.Evaluate(pending.State, rig.Rule, rig.Observation(4), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        await rig.Store.CommitAsync(new(next, 1, rig.Fence), default);
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.Pending, Code: "capacity"), default)).Should().BeTrue();
        var latest = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        latest.Occurrence!.OccurrenceId.Should().Be(next.State.Occurrence!.OccurrenceId);
        latest.Occurrence.FlowOutcome.Should().BeNull();
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 10, default)).Single();
        receipt.OutboxId.Should().Be(lease.OutboxId); receipt.FlowRunId.Should().BeNull();
        await using var verify = rig.Provider.CreateAsyncScope();
        var history = await verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>().MonitoringOccurrences.SingleAsync(row => row.OccurrenceId == first.Occurrence!.OccurrenceId);
        var outcome = JsonSerializer.Deserialize<MonitoringOccurrenceDto>(history.OccurrenceJson)!.FlowOutcome!;
        outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.DeliveryUnknown);
        outcome.Code.Should().Be("attempt_limit");
    }

    [Fact]
    public async Task Optimized_point_eligibility_is_exact_tenant_and_excludes_revoked_deleted_or_superseded_agents()
    {
        await using var rig = await CreateRigAsync();
        var eligibility = rig.Provider.GetRequiredService<IMonitoringAgentEligibility>();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeTrue();
        (await eligibility.IsEligibleAsync(new(rig.Client.TenantId + 1, rig.Client.AgentId), default)).Should().BeFalse();
        (await eligibility.IsEligibleAsync(default, default)).Should().BeFalse();
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var agent = await db.Agents.SingleAsync(row => row.Id == rig.Client.AgentId);
        agent.RevokedAtUtc = rig.Time.GetUtcNow(); await db.SaveChangesAsync();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeFalse();
        agent.RevokedAtUtc = null; agent.DeletedAtUtc = rig.Time.GetUtcNow(); await db.SaveChangesAsync();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeFalse();
        agent.DeletedAtUtc = null; agent.SupersededAtUtc = rig.Time.GetUtcNow(); await db.SaveChangesAsync();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeFalse();
        agent.SupersededAtUtc = null; agent.IsEnabled = false; await db.SaveChangesAsync();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeFalse();
        agent.IsEnabled = true; agent.Status = AgentStatus.Disabled; await db.SaveChangesAsync();
        (await eligibility.IsEligibleAsync(rig.Client, default)).Should().BeFalse();
    }

    private async Task<Rig> CreateRigAsync(bool flow = false)
    {
        var connection = await fixture.CreateDatabaseAsync();
        var time = new MonitoringTestTime(); var directory = new TestMonitoringDirectory();
        var provider = CreateProvider(connection, directory, time);
        var client = new ClientKey(80, Guid.NewGuid());
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync();
            db.Agents.Add(new() { Id = client.AgentId, TenantId = client.TenantId, CreatedAtUtc = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        time.ResetToSystem();
        directory.Client = client;
        var ownership = provider.GetRequiredService<IClientConnectionEpochStore>();
        var request = new AdmissionRequest(client, Guid.NewGuid(), Guid.NewGuid(), 0, time.GetUtcNow(),
            time.GetUtcNow().AddSeconds(30), time.GetUtcNow().AddMinutes(10), new("monitoring-provider", ["presence"], null));
        var reservation = (await ownership.ReserveAsync(request, default)).Reservation!;
        (await ownership.CommitAsync(reservation, new(reservation.Owner, 1, time.GetUtcNow()), default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        var fence = (await provider.GetRequiredService<IMonitoringStore>().ReserveEvidenceRegistrationAsync(client,
            reservation.Owner.ConnectionId, reservation.Owner.Epoch, Guid.NewGuid(), default))!;
        directory.Current = fence;
        var rule = new MonitoringRuleDto(client.TenantId, Guid.NewGuid(), 1, 1, "high CPU", true, MonitoringSeverity.Warning,
            new(MonitoringTargetMode.Selected, [client.AgentId], []), new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 80, 70, null, null, []),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), flow ? Guid.NewGuid() : null, flow ? "operator:monitoring" : null);
        await provider.GetRequiredService<IMonitoringConfigurationStore>().SaveRuleAsync(new(rule, 0, Guid.NewGuid(), "initial rule"), default);
        var store = provider.GetRequiredService<IMonitoringStore>();
        (await store.BeginEvidenceStreamAsync(fence, default)).Should().BeTrue();
        return new(connection, provider, directory, time, client, fence, rule, store);
    }
    private static ServiceProvider CreateProvider(string connection, TestMonitoringDirectory directory, MonitoringTestTime time, DbCommandInterceptor? interceptor = null) =>
        new ServiceCollection().AddDbContext<OrchestratorDbContext>(options =>
        { options.UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); })
        .AddSingleton<TimeProvider>(time)
        .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)))
        .AddSingleton<IMonitoringClientDirectory>(directory).AddSingleton<IMonitoringPublishedFlowProvider, TestPublishedFlows>()
        .AddNetRatelClientServicesPersistence().AddNetRatelMonitoringPersistence().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    private static async Task<MonitoringSeriesState> FireAsync(Rig rig)
    {
        var initial = rig.Evaluator.CreateInitial(rig.Key, rig.Rule);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var pending = rig.Evaluator.Evaluate(initial, rig.Rule, rig.Observation(1), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        pending.State.Phase.Should().Be(MonitoringPhase.Pending);
        (await rig.Store.CommitAsync(new(pending, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var firing = rig.Evaluator.Evaluate(pending.State, rig.Rule, rig.Observation(2), rig.Fence.ConnectionEpoch, rig.Fence.EvidenceStreamId, []);
        firing.State.Phase.Should().Be(MonitoringPhase.Firing);
        (await rig.Store.CommitAsync(new(firing, 1, rig.Fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
        return firing.State;
    }
    private static async Task AdvanceSafeRetryBackoffWithHeartbeatsAsync(Rig rig, TimeSpan backoff)
    {
        // Only the safe-retry exhaustion fixture stays online during its original
        // backoff. Negative expiry cases continue to advance directly without it.
        var ownership = rig.Provider.GetRequiredService<IClientConnectionEpochStore>();
        var original = await ownership.GetCurrentAsync(rig.Client, default)
            ?? throw new InvalidOperationException("The retry fixture has no committed owner.");
        original.Owner.ConnectionId.Should().Be(rig.Fence.ConnectionId);
        original.Owner.Epoch.Should().Be(rig.Fence.ConnectionEpoch);
        original.AcceptanceGuardAtUtc.Should().NotBeNull();
        var current = original;
        while (backoff > TimeSpan.Zero)
        {
            var elapsed = backoff > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : backoff;
            rig.Time.Advance(elapsed);
            var sequence = checked(current.Sequence + 1);
            var renewed = await ownership.RecordHeartbeatAsync(new(original.Owner, sequence, rig.Time.GetUtcNow()), default);
            renewed.Disposition.Should().Be(OwnershipDisposition.Accepted);
            current = await ownership.GetCurrentAsync(rig.Client, default)
                ?? throw new InvalidOperationException("The retry fixture lost its committed owner.");
            current.Owner.Should().Be(original.Owner);
            current.Sequence.Should().Be(sequence);
            current.AcceptanceGuardAtUtc.Should().Be(original.AcceptanceGuardAtUtc);
            current.AuthenticationExpiresAtUtc.Should().Be(original.AuthenticationExpiresAtUtc);
            current.IsEffective(rig.Time.GetUtcNow()).Should().BeTrue();
            backoff -= elapsed;
        }
    }
    private sealed record Rig(string Connection, ServiceProvider Provider, TestMonitoringDirectory Directory, MonitoringTestTime Time,
        ClientKey Client, MonitoringEvidenceFence InitialFence, MonitoringRuleDto Rule, IMonitoringStore Store) : IAsyncDisposable
    {
        public MonitoringEvidenceFence Fence { get; private set; } = InitialFence;
        public MonitoringSeriesEvaluator Evaluator { get; } = new(Time);
        public async Task ReconnectCommittedOwnerAsync()
        {
            var store = Provider.GetRequiredService<IClientConnectionEpochStore>(); var now = Time.GetUtcNow();
            var reservation = (await store.ReserveAsync(new(Client, Guid.NewGuid(), Guid.NewGuid(), 0, now,
                now.AddSeconds(30), now.AddMinutes(10), new("retention-reconnect", [], null)), default)).Reservation!;
            (await store.CommitAsync(reservation, new(reservation.Owner, 1, now), default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
            Fence = (await Store.ReserveEvidenceRegistrationAsync(Client, reservation.Owner.ConnectionId, reservation.Owner.Epoch,
                Guid.NewGuid(), default))!;
            Directory.Current = Fence;
            (await Store.BeginEvidenceStreamAsync(Fence, default)).Should().BeTrue();
        }
        public MonitoringSeriesKey Key => new(Client.TenantId, Rule.RuleId, Client.AgentId, "cpu");
        public MonitoringObservationDto Observation(ulong sequence) => new(Key, new(Fence.ConnectionEpoch, sequence), Fence.EvidenceStreamId, Time.GetUtcNow(), Time.GetUtcNow(), true, true, 95);
        public MonitoringTelemetryInput Telemetry(ulong sequence) => new(Fence, new(Client, Fence.ConnectionEpoch, sequence, Time.GetUtcNow(), Time.GetUtcNow(), new(95, null, null), null, [], [], null, IsAuthoritative: true));
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
    private sealed class MonitoringTestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void ResetToSystem() => _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class TestMonitoringDirectory : IMonitoringClientDirectory
    {
        public ClientKey Client { get; set; }
        public MonitoringEvidenceFence? Current { get; set; }
        public Task<bool> IsPresentedEvidenceAsync(MonitoringEvidenceFence fence, CancellationToken ct) => Task.FromResult(fence == Current);
        public Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken ct) => Task.FromResult(client == Client ? Current : null);
        public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenant, CancellationToken ct) => Task.FromResult(tenant == Client.TenantId ? ImmutableArray.Create(Client.AgentId) : []);
    }
    private sealed class TestPublishedFlows : IMonitoringPublishedFlowProvider
    {
        public bool Available { get; set; } = true;
        public Task<bool> IsPublishedAsync(int tenant, Guid flow, CancellationToken ct) => Task.FromResult(Available);
        public Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenant, int count, CancellationToken ct) => Task.FromResult(ImmutableArray<MonitoringPublishedFlowDto>.Empty);
    }
    private sealed class TestDisplayNames : IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities) => new Dictionary<string, string>();
    }
    private sealed class ControlSeriesWrite : DbCommandInterceptor
    {
        public bool FailNext { get; set; }
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"MonitoringSeries\"", StringComparison.Ordinal))
            {
                if (FailNext) { FailNext = false; throw new IOException("durability unavailable"); }
                if (Pause) { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
            }
            return result;
        }
    }
}
