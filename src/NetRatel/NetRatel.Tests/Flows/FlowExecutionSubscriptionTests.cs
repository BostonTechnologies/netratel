using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
        control.TerminalReadyAtUtc = now;
        control.TerminalResultHash = new string('a', 64);
        await db.SaveChangesAsync();
        Assert.False(await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, trigger, request, default));
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
        var json = JsonSerializer.Serialize(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new() { Id = id, FlowId = flowId, TenantId = 7, VersionNumber = number, GraphJson = json,
            ConfigurationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
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
