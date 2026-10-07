using System.Collections.Immutable;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

/// <summary>Real PostgreSQL barriers; source proposal until the append-only owner/evidence migration is integrated and executed.</summary>
[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class MonitoringOutboxOwnerDeadlinePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_candidate_series_lock_wait_rechecks_first_owner_and_uses_current_renewal_deadlines(bool renewFirstOwner)
    {
        await using var rig = await CreateRigAsync();
        var originalDeadline = rig.FirstOwner.AuthenticationExpiresAtUtc;
        if (renewFirstOwner)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            var renewed = await rig.Provider.GetRequiredService<IClientConnectionEpochStore>().RenewAsync(
                new(rig.FirstOwner.Owner, 2, rig.Time.GetUtcNow(), rig.Time.GetUtcNow().AddMinutes(10)), default);
            renewed.Disposition.Should().Be(OwnershipDisposition.Accepted);
            renewed.Current!.Owner.Should().Be(rig.FirstOwner.Owner);
            renewed.Current.Revision.Should().BeGreaterThan(rig.FirstOwner.Revision);
            // The evidence fence is deliberately unchanged: ordinary renewal is not a replacement.
        }
        var barrier = new SecondSeriesBarrier(rig.FirstOutboxId, rig.SecondClient.AgentId);
        await using var claimant = CreateProvider(rig.Connection, rig.Directory, rig.Time, barrier);
        await using var blocker = new NpgsqlConnection(rig.Connection);
        await blocker.OpenAsync();
        await using var held = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT * FROM "MonitoringSeries" WHERE "TenantId"=@tenant AND "RuleId"=@rule AND "AgentId"=@agent AND "ResourceKey"='cpu' FOR UPDATE
            """, blocker, held))
        {
            command.Parameters.AddWithValue("tenant", rig.FirstClient.TenantId);
            command.Parameters.AddWithValue("rule", rig.Rule.RuleId);
            command.Parameters.AddWithValue("agent", rig.SecondClient.AgentId);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var duration = TimeSpan.FromMinutes(1);
        var claiming = claimant.GetRequiredService<IMonitoringStore>().ClaimOutboxAsync(
            new(rig.FirstClient.TenantId, Guid.NewGuid(), 2, duration), budget.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(budget.Token);
            barrier.FirstClaimWasTracked.Should().BeTrue("the first real pending outbox row was already accumulated before the second query");
            await WaitForDatabaseBlockingAsync(rig.Connection, blocker.ProcessID, budget.Token);
            rig.Time.Set(originalDeadline.AddMilliseconds(1));
            rig.SecondOwner.IsEffective(rig.Time.GetUtcNow()).Should().BeTrue("only the first original authentication deadline crossed");
        }
        finally { await held.RollbackAsync(); }
        var claims = await claiming;
        if (!renewFirstOwner)
        {
            claims.Should().BeEmpty();
            await AssertUnclaimedAsync(rig);
        }
        else
        {
            claims.Should().HaveCount(2);
            claims.Select(claim => claim.OutboxId).Should().BeEquivalentTo(new[] { rig.FirstOutboxId, rig.SecondOutboxId });
            foreach (var claim in claims)
            {
                claim.Attempt.Should().Be(1);
                claim.LeaseFence.Should().Be(1);
                claim.LeaseExpiresAtUtc.Should().Be(Normalize(rig.Time.GetUtcNow() + duration),
                    "a later-candidate await cannot reuse the batch's initial lease clock");
            }
            var current = (await rig.Provider.GetRequiredService<IClientConnectionEpochStore>().GetCurrentAsync(rig.FirstClient, default))!;
            current.Owner.Should().Be(rig.FirstOwner.Owner);
            current.IsEffective(rig.Time.GetUtcNow()).Should().BeTrue();
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var evidence = await db.MonitoringEvidenceStreams.AsNoTracking().SingleAsync(row => row.AgentId == rig.FirstClient.AgentId);
            evidence.EvidenceStreamId.Should().Be(rig.Directory.Fences[rig.FirstClient].EvidenceStreamId);
            evidence.CommittedRegistrationOrdinal.Should().Be(rig.Directory.Fences[rig.FirstClient].RegistrationOrdinal);
            var rows = await db.MonitoringFlowOutbox.AsNoTracking().ToListAsync();
            rows.Should().OnlyContain(row => row.Status == MonitoringOutboxStatus.Leased && row.Attempts == 1 && row.LeaseFence == 1);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_barrier_rolls_back_entire_claim_batch_if_first_authentication_or_presence_expires(bool expirePresence)
    {
        await using var rig = await CreateRigAsync(expirePresence);
        var barrier = new SavedClaimsBarrier();
        await using var claimant = CreateProvider(rig.Connection, rig.Directory, rig.Time, barrier);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var claiming = claimant.GetRequiredService<IMonitoringStore>().ClaimOutboxAsync(
            new(rig.FirstClient.TenantId, Guid.NewGuid(), 2, TimeSpan.FromMinutes(1)), budget.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(budget.Token);
            barrier.SavedClaimCount.Should().Be(2, "both real row updates completed inside the uncommitted transaction");
            // An independent connection observes no premature publication of those updates.
            await AssertUnclaimedAsync(rig);
            var deadline = expirePresence ? rig.FirstOwner.PresenceExpiresAtUtc : rig.FirstOwner.AuthenticationExpiresAtUtc;
            rig.Time.Set(deadline.AddMilliseconds(1));
            rig.FirstOwner.IsEffective(rig.Time.GetUtcNow()).Should().BeFalse();
            rig.SecondOwner.IsEffective(rig.Time.GetUtcNow()).Should().BeTrue("the second genuinely committed owner is still current");
        }
        finally { barrier.Resume.TrySetResult(); }
        (await claiming).Should().BeEmpty();
        await AssertUnclaimedAsync(rig);
    }

    private async Task<Rig> CreateRigAsync(bool firstExpiresByPresence = false)
    {
        var connection = await fixture.CreateDatabaseAsync();
        var time = new TestTime();
        var directory = new TestDirectory();
        var provider = CreateProvider(connection, directory, time);
        var first = new ClientKey(80, Guid.NewGuid());
        var second = new ClientKey(first.TenantId, Guid.NewGuid());
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync();
            db.Agents.AddRange(new Agent { Id = first.AgentId, TenantId = first.TenantId, CreatedAtUtc = time.GetUtcNow() },
                new Agent { Id = second.AgentId, TenantId = second.TenantId, CreatedAtUtc = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        time.Set(DateTimeOffset.UtcNow);
        var rule = new MonitoringRuleDto(first.TenantId, Guid.NewGuid(), 1, 1, "CPU claim deadline", true, MonitoringSeverity.Warning,
            new(MonitoringTargetMode.Selected, [first.AgentId, second.AgentId], []),
            new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 80, 70, null, null, []),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), Guid.NewGuid(), "operator:monitoring");
        (await provider.GetRequiredService<IMonitoringConfigurationStore>().SaveRuleAsync(
            new(rule, 0, Guid.NewGuid(), "outbox deadline regression"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
        var firstOwner = await RegisterOwnerAsync(provider, directory, time, first,
            firstExpiresByPresence ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        var firstOutbox = await FireAsync(provider, directory.Fences[first], time, rule);
        // Later legitimate admission gives replica B a genuinely later presence deadline.
        var secondOwner = await RegisterOwnerAsync(provider, directory, time, second, TimeSpan.FromMinutes(10));
        var secondOutbox = await FireAsync(provider, directory.Fences[second], time, rule);
        return new(connection, provider, directory, time, first, second, firstOwner, secondOwner, rule, firstOutbox, secondOutbox);
    }

    private static async Task<OwnerSnapshot> RegisterOwnerAsync(ServiceProvider provider, TestDirectory directory,
        TestTime time, ClientKey client, TimeSpan authenticationLifetime)
    {
        var store = provider.GetRequiredService<IClientConnectionEpochStore>();
        var now = time.GetUtcNow();
        var reserved = await store.ReserveAsync(new(client, Guid.NewGuid(), Guid.NewGuid(), 0, now,
            now.AddSeconds(30), now + authenticationLifetime, new("outbox-deadline-provider", ["presence"], null)), default);
        reserved.Disposition.Should().Be(OwnershipDisposition.Accepted);
        var accepted = await store.CommitAsync(reserved.Reservation!, new(reserved.Reservation!.Owner, 1, now), default);
        accepted.Disposition.Should().Be(OwnershipDisposition.Accepted);
        var monitoring = provider.GetRequiredService<IMonitoringStore>();
        var fence = await monitoring.ReserveEvidenceRegistrationAsync(client, accepted.Current!.Owner.ConnectionId,
            accepted.Current.Owner.Epoch, Guid.NewGuid(), default);
        fence.Should().NotBeNull();
        directory.Fences.Add(client, fence!);
        (await monitoring.BeginEvidenceStreamAsync(fence!, default)).Should().BeTrue();
        return accepted.Current;
    }

    private static async Task<Guid> FireAsync(ServiceProvider provider, MonitoringEvidenceFence fence, TestTime time, MonitoringRuleDto rule)
    {
        var evaluator = new MonitoringSeriesEvaluator(time);
        var key = new MonitoringSeriesKey(fence.Client.TenantId, rule.RuleId, fence.Client.AgentId, "cpu");
        var state = evaluator.CreateInitial(key, rule);
        var store = provider.GetRequiredService<IMonitoringStore>();
        for (ulong sequence = 1; sequence <= 2; sequence++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var observation = new MonitoringObservationDto(key, new(fence.ConnectionEpoch, sequence), fence.EvidenceStreamId,
                time.GetUtcNow(), time.GetUtcNow(), true, true, 95);
            var evaluation = evaluator.Evaluate(state, rule, observation, fence.ConnectionEpoch, fence.EvidenceStreamId, []);
            (await store.CommitAsync(new(evaluation, 1, fence), default)).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
            state = evaluation.State;
        }
        state.Phase.Should().Be(MonitoringPhase.Firing);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync(row => row.OccurrenceId == state.Occurrence!.OccurrenceId)).OutboxId;
    }

    private static async Task AssertUnclaimedAsync(Rig rig)
    {
        await using var scope = rig.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await db.MonitoringFlowOutbox.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(row => row.OutboxId).Should().BeEquivalentTo(new[] { rig.FirstOutboxId, rig.SecondOutboxId });
        rows.Should().OnlyContain(row => row.Status == MonitoringOutboxStatus.Pending && row.Attempts == 0 && row.LeaseFence == 0 &&
            row.LeaseId == null && row.WorkerId == null && row.LeaseExpiresAtUtc == null && row.FlowRunId == null);
    }

    private static async Task WaitForDatabaseBlockingAsync(string connection, int blockerPid, CancellationToken ct)
    {
        await using var observer = new NpgsqlConnection(connection);
        await observer.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @blocker=ANY(pg_blocking_pids(pid)) AND query LIKE '%MonitoringSeries%')
            """, observer);
        command.Parameters.AddWithValue("blocker", blockerPid);
        while (!(bool)(await command.ExecuteScalarAsync(ct))!)
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
    }

    private static ServiceProvider CreateProvider(string connection, TestDirectory directory, TestTime time, IInterceptor? interceptor = null) =>
        new ServiceCollection().AddDbContext<OrchestratorDbContext>(options =>
        { options.UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); })
        .AddSingleton<TimeProvider>(time)
        .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)))
        .AddSingleton<IMonitoringClientDirectory>(directory).AddSingleton<IMonitoringPublishedFlowProvider, PublishedFlows>()
        .AddNetRatelClientServicesPersistence().AddNetRatelMonitoringPersistence().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static DateTimeOffset Normalize(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    private sealed record Rig(string Connection, ServiceProvider Provider, TestDirectory Directory, TestTime Time,
        ClientKey FirstClient, ClientKey SecondClient, OwnerSnapshot FirstOwner, OwnerSnapshot SecondOwner,
        MonitoringRuleDto Rule, Guid FirstOutboxId, Guid SecondOutboxId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
    private sealed class TestTime : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
    private sealed class TestDirectory : IMonitoringClientDirectory
    {
        public Dictionary<ClientKey, MonitoringEvidenceFence> Fences { get; } = [];
        public Task<bool> IsPresentedEvidenceAsync(MonitoringEvidenceFence fence, CancellationToken ct) =>
            Task.FromResult(Fences.GetValueOrDefault(fence.Client) == fence);
        public Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken ct) =>
            Task.FromResult<MonitoringEvidenceFence?>(Fences.GetValueOrDefault(client));
        public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenant, CancellationToken ct) =>
            Task.FromResult(Fences.Keys.Where(client => client.TenantId == tenant).Select(client => client.AgentId).ToImmutableArray());
    }
    private sealed class PublishedFlows : IMonitoringPublishedFlowProvider
    {
        public Task<bool> IsPublishedAsync(int tenant, Guid flow, CancellationToken ct) => Task.FromResult(true);
        public Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenant, int count, CancellationToken ct) => Task.FromResult(ImmutableArray<MonitoringPublishedFlowDto>.Empty);
    }
    private sealed class SecondSeriesBarrier(Guid firstOutboxId, Guid secondAgentId) : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstClaimWasTracked { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT * FROM \"MonitoringSeries\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == secondAgentId))
            {
                var row = eventData.Context!.ChangeTracker.Entries<MonitoringFlowOutboxRecord>().SingleOrDefault(entry => entry.Entity.OutboxId == firstOutboxId)?.Entity;
                FirstClaimWasTracked = row is { Status: MonitoringOutboxStatus.Leased, Attempts: 1, LeaseFence: 1, FlowRunId: null };
                Entered.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class SavedClaimsBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SavedClaimCount { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            SavedClaimCount = eventData.Context!.ChangeTracker.Entries<MonitoringFlowOutboxRecord>()
                .Count(entry => entry.Entity is { Status: MonitoringOutboxStatus.Leased, Attempts: 1, LeaseFence: 1, FlowRunId: null });
            if (SavedClaimCount > 0)
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
