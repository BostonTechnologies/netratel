using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionSubscriptionWaitTests
{
    [Fact]
    public async Task Combined_admission_waits_for_known_parent_terminal_ready_while_request_only_does_not()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var now = DateTimeOffset.UtcNow;
        db.JobRuns.Add(new() { Id = 41, JobId = 9, TenantId = 7, Status = (int)JobRunState.Failed, CreatedAtUtc = now });
        var control = new JobRunControlRecord { RunId = 41 };
        db.Set<JobRunControlRecord>().Add(control);
        await db.SaveChangesAsync();
        var trigger = Trigger();
        var request = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Request,
            new("9"), "17", "41", new(null), FlowExecutionOutcome.Failed, "Failed", now, now)
            { CorrelationId = "job-run:41", ParentExecutionId = "41" };
        Assert.True(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, trigger, request, default));
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, trigger,
            request with { CorrelationId = "request:17", ParentExecutionId = null }, default));
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db,
            trigger with { Sources = [FlowExecutionSourceKind.Request] }, request, default));
        var scriptAndRequest = trigger with { Sources = [FlowExecutionSourceKind.Script, FlowExecutionSourceKind.Request] };
        var script = request with { SourceKind = FlowExecutionSourceKind.Script, ExecutionId = "native-script",
            AttemptId = "native-script" };
        Assert.True(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, scriptAndRequest, script, default));
        Assert.True(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, scriptAndRequest, request, default));
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db,
            scriptAndRequest with { AggregateCorrelatedEvents = false }, script, default));
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db,
            scriptAndRequest with { Sources = [FlowExecutionSourceKind.Script] }, script, default));
        control.TerminalReadyAtUtc = now;
        control.TerminalResultHash = new string('a', 64);
        await db.SaveChangesAsync();
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, trigger, request, default));
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, scriptAndRequest, script, default));
    }

    internal static FlowExecutionTriggerSettingsDto Trigger() => new(
        [FlowExecutionSourceKind.Job, FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant);
}

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionSubscriptionPostgreSqlTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Source_state_fact_and_pinned_version_intent_roll_back_together_and_survive_publish_swap_and_restart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        var flowId = Guid.NewGuid();
        var firstVersion = Guid.NewGuid();
        var secondVersion = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var active = DateTimeOffset.UtcNow.AddMinutes(-1);
        int requestId;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            var definition = new FlowDefinitionRecord { Id = flowId, TenantId = 7, Name = "Synthetic subscription",
                Revision = 1, Enabled = true, EnabledSinceUtc = active, CreatedAtUtc = active, UpdatedAtUtc = active,
                DraftJson = "{}" };
            db.FlowDefinitions.Add(definition);
            db.FlowVersions.Add(Version(firstVersion, flowId, triggerId, 1, active));
            await db.SaveChangesAsync(ct);
            definition.PublishedVersionId = firstVersion;
            definition.PublishedVersionNumber = 1;
            await db.SaveChangesAsync(ct);
            var requests = new RequestService(db, keys);
            requestId = (await requests.CreateAsync(new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct)).Id;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await requests.UpdateAsync(new(requestId, null, null, null, null, "Failed", null, null, null, null), ct);
            Assert.Equal(2, await db.OutboxMessages.CountAsync(ct));
            await transaction.RollbackAsync(ct);
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            Assert.Equal("New", (await db.Requests.AsNoTracking().SingleAsync(ct)).Status);
            Assert.Empty(await db.OutboxMessages.ToArrayAsync(ct));
            var requests = new RequestService(db, keys);
            await requests.UpdateAsync(new(requestId, null, null, null, null, "Failed", null, null, null, null), ct);
            var source = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct);
            var intent = ExecutionLifecycleSubscriptions.ReadDelivery((await db.OutboxMessages.AsNoTracking()
                .SingleAsync(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType, ct)).PayloadJson)!;
            Assert.Equal(firstVersion, intent.FlowVersionId);
            Assert.Equal(source.Id, intent.SourceEventId);
            Assert.Equal(ExecutionLifecycleSubscriptions.PayloadHash(source.PayloadJson), intent.SourcePayloadHash);
            db.FlowVersions.Add(Version(secondVersion, flowId, triggerId, 2, DateTimeOffset.UtcNow));
            var definition = await db.FlowDefinitions.SingleAsync(ct);
            definition.PublishedVersionId = secondVersion;
            definition.PublishedVersionNumber = 2;
            await db.SaveChangesAsync(ct);
        }
        var ingress = new RecordingIngress();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System)
            .AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(connection))
            .AddSingleton<IFlowExecutionEventIngress>(ingress).BuildServiceProvider();
        await using (services)
        {
            var worker = new ExecutionLifecycleSubscriptionWorker(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ExecutionLifecycleSubscriptionWorker>.Instance, TimeProvider.System);
            await worker.ProcessBatchAsync(ct);
            await worker.ProcessBatchAsync(ct);
        }
        var call = Assert.Single(ingress.Calls);
        Assert.Equal(firstVersion, call.FlowVersionId);
        Assert.NotNull(call.DeliveryId);
        Assert.Contains(call.SourceEventId, call.ContributingEventIds!);
        await using (var db = new OrchestratorDbContext(options))
        {
            var intent = await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType, ct);
            Assert.Equal(OutboxStatuses.Published, intent.Status);
            intent.PayloadJson = "{}";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task Worker_is_inert_without_trusted_ingress_registration()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ExecutionLifecycleSubscriptionWorker(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExecutionLifecycleSubscriptionWorker>.Instance, TimeProvider.System);
        // No DbContext is registered: an inert worker must not even attempt a mutable source scan.
        await worker.ProcessBatchAsync(default);
    }

    [Fact]
    public async Task Source_selection_linearizes_after_enable_interval_lock_and_preserves_native_completion_time()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        var flowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var oldInterval = DateTimeOffset.UtcNow.AddMinutes(-1);
        int requestId;
        await using (var setup = new OrchestratorDbContext(options))
        {
            await setup.Database.MigrateAsync(ct);
            var definition = new FlowDefinitionRecord { Id = flowId, TenantId = 7, Name = "Synthetic interval source",
                Revision = 1, Enabled = true, EnabledSinceUtc = oldInterval, CreatedAtUtc = oldInterval,
                UpdatedAtUtc = oldInterval, DraftJson = "{}" };
            setup.FlowDefinitions.Add(definition);
            setup.FlowVersions.Add(Version(versionId, flowId, triggerId, 1, oldInterval));
            await setup.SaveChangesAsync(ct);
            definition.PublishedVersionId = versionId;
            definition.PublishedVersionNumber = 1;
            await setup.SaveChangesAsync(ct);
            requestId = (await new RequestService(setup, keys).CreateAsync(
                new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct)).Id;
        }
        await using var configuration = new OrchestratorDbContext(options);
        await using var interval = await configuration.Database.BeginTransactionAsync(ct);
        var locked = await configuration.FlowDefinitions.FromSqlInterpolated(
            $"SELECT * FROM \"FlowDefinitions\" WHERE \"Id\" = {flowId} FOR UPDATE").SingleAsync(ct);
        var observer = new SubscriptionSelectionObserver();
        var sourceOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection)
            .AddInterceptors(observer).Options;
        var sourceWrite = Task.Run(async () =>
        {
            await using var source = new OrchestratorDbContext(sourceOptions);
            await new RequestService(source, keys).UpdateAsync(new(requestId, null, null, null, null,
                "Failed", "Synthetic native terminal failure.", null, null, null), ct);
        }, ct);
        await observer.Started.WaitAsync(ct);
        var enabledAt = await configuration.Database.SqlQuery<DateTimeOffset>(
            $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        locked.EnabledSinceUtc = enabledAt;
        locked.Revision++;
        await configuration.SaveChangesAsync(ct);
        await interval.CommitAsync(ct);
        await sourceWrite;
        await using var verify = new OrchestratorDbContext(options);
        var row = await verify.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct);
        var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.NotNull(fact.CompletedAtUtc);
        Assert.True(fact.CompletedAtUtc.Value <= enabledAt);
        Assert.True(fact.OccurredAtUtc >= enabledAt);
        var delivery = ExecutionLifecycleSubscriptions.ReadDelivery((await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType, ct)).PayloadJson)!;
        Assert.Equal(enabledAt, delivery.ActiveSinceUtc);
        Assert.Equal(row.Id, delivery.SourceEventId);
        Assert.Equal(ExecutionLifecycleSubscriptions.PayloadHash(row.PayloadJson), delivery.SourcePayloadHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_same_status_script_result_owns_the_source_transaction_and_selected_intent(bool synchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync(ct);
        var active = DateTimeOffset.UtcNow.AddMinutes(-1);
        var flowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var definition = new FlowDefinitionRecord
        {
            Id = flowId, TenantId = 7, Name = "Synthetic late script result", Enabled = true,
            EnabledSinceUtc = active, Revision = 1, DraftJson = "{}", CreatedAtUtc = active, UpdatedAtUtc = active
        };
        db.FlowDefinitions.Add(definition);
        db.FlowVersions.Add(Version(versionId, flowId, Guid.NewGuid(), 1, active,
            new([FlowExecutionSourceKind.Script], [FlowExecutionOutcome.Unknown, FlowExecutionOutcome.Failed],
                FlowExecutionScope.AllInTenant)));
        db.Agents.Add(new() { Id = agent, TenantId = 7, Name = "synthetic-host", CreatedAtUtc = active });
        await db.SaveChangesAsync(ct);
        definition.PublishedVersionId = versionId;
        definition.PublishedVersionNumber = 1;
        await db.SaveChangesAsync(ct);
        var activity = new JobTaskActivityRecord
        {
            RequestId = "synthetic-late-result", TenantId = 7, AgentId = agent,
            TaskType = TaskKinds.ExecLibraryScript, Status = "Completed", CreatedAtUtc = active, CompletedAtUtc = active,
            ScriptName = "Synthetic library execution",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64)))
        };
        db.JobTaskActivities.Add(activity);
        await SaveWithoutAmbientTransaction();
        var original = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct);
        Assert.Equal(FlowExecutionOutcome.Unknown,
            JsonSerializer.Deserialize(original.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!.Outcome);

        // The native result can commit after the transport marked Completed, without changing status.
        activity.ExitCode = 7;
        activity.ResultJson = "{\"exitCode\":7,\"stderr\":\"Synthetic definitive failure\"}";
        await SaveWithoutAmbientTransaction();
        var sources = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync(ct);
        Assert.Equal(2, sources.Length);
        Assert.Equal(original.PayloadJson, sources.Single(x => x.Id == original.Id).PayloadJson);
        var failed = JsonSerializer.Deserialize(sources.Single(x => x.Id != original.Id).PayloadJson,
            FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.Equal("Completed", failed.RawStatus);
        Assert.Equal(FlowExecutionOutcome.Failed, failed.Outcome);
        Assert.Equal(7, failed.ExitCode);
        Assert.Equal(activity.CompletedAtUtc, failed.CompletedAtUtc);
        var intents = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType).ToArrayAsync(ct);
        Assert.Equal(2, intents.Length);
        Assert.All(intents, row =>
        {
            var intent = ExecutionLifecycleSubscriptions.ReadDelivery(row.PayloadJson)!;
            Assert.Equal(versionId, intent.FlowVersionId);
            Assert.Equal(ExecutionLifecycleSubscriptions.PayloadHash(sources.Single(x => x.Id == intent.SourceEventId).PayloadJson),
                intent.SourcePayloadHash);
        });
        await SaveWithoutAmbientTransaction();
        Assert.Equal(2, await db.OutboxMessages.CountAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct));
        Assert.Equal(2, await db.OutboxMessages.CountAsync(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType, ct));
        db.ChangeTracker.Clear();
        Assert.Equal(7, (await db.JobTaskActivities.AsNoTracking().SingleAsync(ct)).ExitCode);

        async Task SaveWithoutAmbientTransaction()
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (synchronous) db.SaveChanges();
            else await db.SaveChangesAsync(ct);
            Assert.Null(db.Database.CurrentTransaction);
        }
    }

    [Fact]
    public async Task Deferred_old_deliveries_do_not_starve_a_new_due_source_intent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        var flowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var active = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var setup = new OrchestratorDbContext(options))
        {
            await setup.Database.MigrateAsync(ct);
            var definition = new FlowDefinitionRecord
            {
                Id = flowId, TenantId = 7, Name = "Synthetic due work", Revision = 1, Enabled = true,
                EnabledSinceUtc = active, DraftJson = "{}", CreatedAtUtc = active, UpdatedAtUtc = active
            };
            setup.FlowDefinitions.Add(definition);
            setup.FlowVersions.Add(Version(versionId, flowId, Guid.NewGuid(), 1, active));
            await setup.SaveChangesAsync(ct);
            definition.PublishedVersionId = versionId;
            definition.PublishedVersionNumber = 1;
            await setup.SaveChangesAsync(ct);
            var requests = new RequestService(setup, keys);
            for (var index = 0; index < FlowExecutionSubscriptionLimits.MaximumBatch; index++)
            {
                var request = await requests.CreateAsync(new("synthetic-old", string.Empty, "missing-definition", null, 7, null), ct);
                await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", null, null, null, null), ct);
            }
        }
        var clock = new SubscriptionClock(DateTimeOffset.UtcNow.AddMinutes(1));
        var ingress = new CapacityIngress();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock)
            .AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(connection))
            .AddSingleton<IFlowExecutionEventIngress>(ingress).BuildServiceProvider();
        await using (services)
        {
            var worker = new ExecutionLifecycleSubscriptionWorker(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ExecutionLifecycleSubscriptionWorker>.Instance, clock);
            await worker.ProcessBatchAsync(ct);
            Assert.Equal(FlowExecutionSubscriptionLimits.MaximumBatch, ingress.Calls.Count);
            Guid fresh;
            await using (var source = new OrchestratorDbContext(options))
            {
                var requests = new RequestService(source, keys);
                var request = await requests.CreateAsync(new("synthetic-fresh", string.Empty, "missing-definition", null, 7, null), ct);
                await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", null, null, null, null), ct);
                fresh = await source.OutboxMessages.Where(row => row.Type == FlowExecutionEventLimits.OutboxType &&
                        row.EntityId == request.Id.ToString()).Select(row => row.Id).SingleAsync(ct);
            }
            clock.Advance(TimeSpan.FromSeconds(6));
            await worker.ProcessBatchAsync(ct);
            var retriedBatch = ingress.Calls.Skip(FlowExecutionSubscriptionLimits.MaximumBatch).ToArray();
            Assert.Equal(FlowExecutionSubscriptionLimits.MaximumBatch, retriedBatch.Length);
            Assert.Contains(retriedBatch, call => call.SourceEventId == fresh);
            Assert.Equal(FlowExecutionSubscriptionLimits.MaximumBatch - 1,
                retriedBatch.Count(call => call.SourceEventId != fresh));
            await using var verify = new OrchestratorDbContext(options);
            Assert.Equal(FlowExecutionSubscriptionLimits.MaximumBatch + 1,
                await verify.OutboxMessages.CountAsync(row => row.Type == FlowExecutionSubscriptionLimits.OutboxType &&
                    row.Status == OutboxStatuses.Pending, ct));
        }
    }

    private sealed class SubscriptionClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class CapacityIngress : IFlowExecutionEventIngress
    {
        public List<FlowExecutionEventAdmissionRequest> Calls { get; } = [];
        public Task<FlowIngressResult> EnqueueExecutionAsync(FlowExecutionEventAdmissionRequest input,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(input);
            return Task.FromResult(new FlowIngressResult(FlowIngressDisposition.CapacityExceeded, null, "synthetic-capacity"));
        }
    }

    [Fact]
    public async Task An_expired_known_parent_wait_finishes_delivery_without_changing_unknown_source_facts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        var flowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var active = DateTimeOffset.UtcNow.AddMinutes(-1);
        Guid sourceId;
        string originalPayload;
        await using (var setup = new OrchestratorDbContext(options))
        {
            await setup.Database.MigrateAsync(ct);
            var definition = new FlowDefinitionRecord
            {
                Id = flowId, TenantId = 7, Name = "Synthetic parent wait", Revision = 1, Enabled = true,
                EnabledSinceUtc = active, DraftJson = "{}", CreatedAtUtc = active, UpdatedAtUtc = active
            };
            setup.FlowDefinitions.Add(definition);
            setup.FlowVersions.Add(Version(versionId, flowId, Guid.NewGuid(), 1, active,
                FlowExecutionSubscriptionWaitTests.Trigger() with { Outcomes = [FlowExecutionOutcome.Unknown] }));
            setup.Agents.Add(new() { Id = agent, TenantId = 7, Name = "synthetic-host", CreatedAtUtc = active });
            setup.Jobs.Add(new() { Id = 9, TenantId = 7, AgentId = agent, Name = "Synthetic bound job",
                CreatedAtUtc = active, UpdatedAtUtc = active });
            setup.JobRuns.Add(new() { Id = 41, JobId = 9, TenantId = 7, AgentId = agent, CreatedAtUtc = active,
                StartedAtUtc = active, StartedBy = "synthetic-parent", Status = (int)JobRunState.Running });
            setup.Set<JobRunControlRecord>().Add(new() { RunId = 41 });
            await setup.SaveChangesAsync(ct);
            definition.PublishedVersionId = versionId;
            definition.PublishedVersionNumber = 1;
            await setup.SaveChangesAsync(ct);
            var requests = new RequestService(setup, keys);
            var request = await requests.CreateAsync(new("synthetic-parent", string.Empty, "9", null, 7, agent), ct);
            await using (var accepted = await setup.Database.BeginTransactionAsync(ct))
            {
                var run = new JobRunInfo(41, 9, 7, string.Empty, "synthetic-parent", JobRunState.Running, 0,
                    active, active, null, null, null, null, agent);
                await ExecutionLifecycleOutbox.RecordAcceptedRequestJobBindingAsync(setup, request, run, ct);
                await requests.UpdateAsync(new(request.Id, null, null, null, "41", "Unknown",
                    "Synthetic unknown delivery", null, null, null), ct);
                await accepted.CommitAsync(ct);
            }
            var row = await setup.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct);
            sourceId = row.Id;
            originalPayload = row.PayloadJson;
            var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal("job-run:41", fact.CorrelationId);
            Assert.True(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(setup,
                FlowExecutionSubscriptionWaitTests.Trigger(), fact, ct));
        }
        var clock = new SubscriptionClock(DateTimeOffset.UtcNow + FlowLimits.MaximumRetryAge + TimeSpan.FromSeconds(1));
        var ingress = new RecordingIngress();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock)
            .AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(connection))
            .AddSingleton<IFlowExecutionEventIngress>(ingress).BuildServiceProvider();
        await using (services)
        {
            var worker = new ExecutionLifecycleSubscriptionWorker(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ExecutionLifecycleSubscriptionWorker>.Instance, clock);
            await worker.ProcessBatchAsync(ct);
            await worker.ProcessBatchAsync(ct);
        }
        Assert.Empty(ingress.Calls);
        await using var verify = new OrchestratorDbContext(options);
        var delivery = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(row => row.Type == FlowExecutionSubscriptionLimits.OutboxType, ct);
        Assert.Equal(OutboxStatuses.Failed, delivery.Status);
        Assert.Equal("flow-event-expired", delivery.LastError);
        var source = await verify.OutboxMessages.AsNoTracking().SingleAsync(row => row.Id == sourceId, ct);
        Assert.Equal(originalPayload, source.PayloadJson);
        var unchanged = JsonSerializer.Deserialize(source.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.Equal(FlowExecutionOutcome.Unknown, unchanged.Outcome);
        Assert.Equal("Unknown", unchanged.RawStatus);
        Assert.Equal("Unknown", (await verify.Requests.AsNoTracking().SingleAsync(ct)).Status);
        Assert.Equal((int)JobRunState.Running, (await verify.JobRuns.AsNoTracking().SingleAsync(ct)).Status);
        Assert.Null((await verify.Set<JobRunControlRecord>().AsNoTracking().SingleAsync(ct)).TerminalReadyAtUtc);
        Assert.Empty(await verify.FlowRuns.AsNoTracking().ToArrayAsync(ct));
    }


    [Fact]
    public async Task Invalid_published_hash_does_not_select_an_intent_or_block_an_independent_valid_subscription()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync(ct);
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var good = Version(Guid.NewGuid(), goodId, Guid.NewGuid(), 1, at);
        var bad = Version(Guid.NewGuid(), badId, Guid.NewGuid(), 1, at);
        bad.ConfigurationHash = new string('0', 64);
        var goodDefinition = new FlowDefinitionRecord { Id = goodId, TenantId = 7, Name = "Synthetic valid source",
            Revision = 1, Enabled = true, EnabledSinceUtc = at, DraftJson = "{}", CreatedAtUtc = at, UpdatedAtUtc = at };
        var badDefinition = new FlowDefinitionRecord { Id = badId, TenantId = 7, Name = "Synthetic invalid source",
            Revision = 1, Enabled = true, EnabledSinceUtc = at, DraftJson = "{}", CreatedAtUtc = at, UpdatedAtUtc = at };
        db.Tenants.Add(new Tenant { Id = 7, Name = "Synthetic hash tenant" });
        db.FlowDefinitions.AddRange(goodDefinition, badDefinition);
        await db.SaveChangesAsync(ct);
        db.FlowVersions.AddRange(good, bad);
        await db.SaveChangesAsync(ct);
        goodDefinition.PublishedVersionId = good.Id;
        badDefinition.PublishedVersionId = bad.Id;
        await db.SaveChangesAsync(ct);
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct);
        await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed",
            "Synthetic native admission failure.", null, null, null), ct);
        Assert.Equal("Failed", (await db.Requests.AsNoTracking().SingleAsync(x => x.Id == request.Id, ct)).Status);
        Assert.Single(await db.OutboxMessages.AsNoTracking().Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync(ct));
        var intent = ExecutionLifecycleSubscriptions.ReadDelivery((await db.OutboxMessages.AsNoTracking()
            .SingleAsync(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType, ct)).PayloadJson)!;
        Assert.Equal(good.Id, intent.FlowVersionId);
        Assert.NotEqual(bad.Id, intent.FlowVersionId);
        Assert.Equal(0, await db.FlowRuns.CountAsync(ct));
    }

    private sealed class SubscriptionSelectionObserver : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"FlowDefinitions\"", StringComparison.Ordinal))
                _started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private static FlowVersionRecord Version(Guid id, Guid flowId, Guid triggerId, int number, DateTimeOffset published,
        FlowExecutionTriggerSettingsDto? settings = null)
    {
        var finish = Guid.NewGuid();
        var graph = new FlowGraphDto(2,
            [new(triggerId, FlowNodeKind.ExecutionEvent, "Synthetic execution event", new(0, 0))
                { ExecutionTrigger = settings ?? FlowExecutionSubscriptionWaitTests.Trigger() },
             new(finish, FlowNodeKind.Finish, "Finish", new(250, 0))],
            [new(triggerId, FlowNodeCatalogue.Event, finish, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
        Assert.True(FlowGraphV2Validator.ValidatePublished(graph).Valid);
        var json = FlowGraphSerialization.Serialize(graph);
        return new() { Id = id, FlowId = flowId, TenantId = 7, VersionNumber = number, GraphJson = json,
            ConfigurationHash = FlowGraphSerialization.Hash(graph),
            PublishedAtUtc = published, PublishedBy = "synthetic-operator",
            ExecutionAuthorityJson = "{\"principalId\":\"synthetic-configuring-operator\"}" };
    }

    private sealed class RecordingIngress : IFlowExecutionEventIngress
    {
        public List<FlowExecutionEventAdmissionRequest> Calls { get; } = [];
        public Task<FlowIngressResult> EnqueueExecutionAsync(FlowExecutionEventAdmissionRequest input, CancellationToken cancellationToken = default)
        { Calls.Add(input); return Task.FromResult(new FlowIngressResult(FlowIngressDisposition.Enqueued, Guid.NewGuid())); }
    }
}
