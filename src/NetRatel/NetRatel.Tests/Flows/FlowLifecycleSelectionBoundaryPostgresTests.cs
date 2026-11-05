using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Flows;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowLifecycleSelectionBoundaryPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("enable", false)]
    [InlineData("publish", false)]
    [InlineData("create", false)]
    [InlineData("enable", true)]
    [InlineData("publish", true)]
    [InlineData("create", true)]
    public async Task Activation_and_source_selection_have_one_order_including_initially_invisible_definitions(
        string mode, bool activationFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var sourceGate = new BoundaryGate(pauseBeforeClock: !activationFirst);
        var configurationGate = new BoundaryGate(pauseAfterFence: activationFirst);
        var protection = new EphemeralDataProtectionProvider();
        await using var source = Provider(connection, protection, sourceGate);
        await using var configuration = Provider(connection, protection, configurationGate);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var flowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var old = DateTimeOffset.UtcNow.AddMinutes(-2);
        int requestId;
        await using (var setup = new OrchestratorDbContext(options))
        {
            await setup.Database.MigrateAsync(ct);
            setup.Tenants.Add(new Tenant { Id = 7, Name = "Synthetic activation tenant" });
            if (mode != "create")
            {
                var definition = Definition(flowId, old, enabled: mode != "enable");
                setup.FlowDefinitions.Add(definition);
                await setup.SaveChangesAsync(ct);
                if (mode == "enable")
                {
                    setup.FlowVersions.Add(Version(versionId, flowId, old));
                    await setup.SaveChangesAsync(ct);
                    definition.PublishedVersionId = versionId;
                    definition.PublishedVersionNumber = 1;
                }
            }
            await setup.SaveChangesAsync(ct);
            requestId = (await new RequestService(setup, protection).CreateAsync(
                new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct)).Id;
        }

        Task SourceWriteAsync() => FailAsync(source, requestId, ct);
        async Task ActivateAsync()
        {
            if (mode == "enable")
            {
                var enabled = await configuration.GetRequiredService<IFlowDefinitionService>()
                    .SetEnabledAsync(7, flowId, new(1, true), "synthetic-operator", ct);
                Assert.Equal(FlowWriteDisposition.Stored, enabled.Disposition);
                return;
            }
            // The publisher's immutable snapshot is seeded here; this test owns selection/fencing,
            // while v2 publication and definition pins have their own integration coverage.
            await using var scope = configuration.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await FlowTenantTransactionLock.AcquireAsync(db, 7, ct);
            var at = await db.Database.SqlQuery<DateTimeOffset>(
                $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            var definition = mode == "create" ? Definition(flowId, at, true) :
                await db.FlowDefinitions.FromSqlInterpolated(
                    $"SELECT * FROM \"FlowDefinitions\" WHERE \"TenantId\" = {7} AND \"Id\" = {flowId} FOR UPDATE")
                    .SingleAsync(ct);
            if (mode == "create") db.FlowDefinitions.Add(definition);
            db.FlowVersions.Add(Version(versionId, flowId, at));
            definition.PublishedVersionId = versionId;
            definition.PublishedVersionNumber = 1;
            definition.UpdatedAtUtc = at;
            definition.Revision++;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        Task sourceWrite;
        Task activation;
        if (activationFirst)
        {
            activation = ActivateAsync();
            await configurationGate.Paused.WaitAsync(ct);
            sourceWrite = SourceWriteAsync();
            await AssertWaitingForFenceAsync(options, await sourceGate.FenceAttempted.WaitAsync(ct), ct);
            configurationGate.Release();
        }
        else
        {
            sourceWrite = SourceWriteAsync();
            await sourceGate.Paused.WaitAsync(ct);
            activation = ActivateAsync();
            await AssertWaitingForFenceAsync(options, await configurationGate.FenceAttempted.WaitAsync(ct), ct);
            sourceGate.Release();
        }
        await Task.WhenAll(sourceWrite, activation);

        string originalPayload;
        FlowExecutionEventDto original;
        DateTimeOffset activeSince;
        await using (var verify = new OrchestratorDbContext(options))
        {
            var definition = await verify.FlowDefinitions.AsNoTracking().SingleAsync(x => x.Id == flowId, ct);
            var version = await verify.FlowVersions.AsNoTracking().SingleAsync(x => x.Id == versionId, ct);
            activeSince = definition.EnabledSinceUtc > version.PublishedAtUtc
                ? definition.EnabledSinceUtc.Value : version.PublishedAtUtc;
            var request = await verify.Requests.AsNoTracking().SingleAsync(x => x.Id == requestId, ct);
            Assert.Equal("Failed", request.Status);
            var sourceRow = await verify.OutboxMessages.AsNoTracking().SingleAsync(x =>
                x.Type == FlowExecutionEventLimits.OutboxType && x.EntityId == requestId.ToString(CultureInfo.InvariantCulture), ct);
            originalPayload = sourceRow.PayloadJson;
            original = JsonSerializer.Deserialize(originalPayload,
                FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal(FlowExecutionOutcome.Failed, original.Outcome);
            Assert.Equal("Failed", original.RawStatus);
            Assert.NotNull(original.CompletedAtUtc);
            var deliveries = await verify.OutboxMessages.AsNoTracking().Where(x =>
                x.Type == FlowExecutionSubscriptionLimits.OutboxType).ToArrayAsync(ct);
            if (activationFirst)
            {
                Assert.True(original.CompletedAtUtc.Value <= activeSince);
                Assert.True(original.OccurredAtUtc >= activeSince);
                var delivery = ExecutionLifecycleSubscriptions.ReadDelivery(Assert.Single(deliveries).PayloadJson)!;
                Assert.Equal(original.EventId, delivery.SourceEventId);
                Assert.Equal(versionId, delivery.FlowVersionId);
                Assert.Equal(activeSince, delivery.ActiveSinceUtc);
                Assert.Equal(ExecutionLifecycleSubscriptions.PayloadHash(originalPayload), delivery.SourcePayloadHash);
            }
            else
            {
                Assert.True(original.OccurredAtUtc < activeSince);
                Assert.Empty(deliveries);
            }
        }

        // Future native transitions see the activated subscription. An earlier fact is never
        // retimestamped or given a retrospective delivery when activation follows its selection.
        int freshId;
        await using (var scope = source.CreateAsyncScope())
            freshId = (await scope.ServiceProvider.GetRequiredService<IRequestService>().CreateAsync(
                new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct)).Id;
        await FailAsync(source, freshId, ct);
        await using var final = new OrchestratorDbContext(options);
        Assert.Equal(originalPayload, (await final.OutboxMessages.AsNoTracking()
            .SingleAsync(x => x.Id == original.EventId, ct)).PayloadJson);
        var facts = await final.OutboxMessages.AsNoTracking().Where(x =>
            x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync(ct);
        var fresh = Assert.Single(facts, x => x.EntityId == freshId.ToString(CultureInfo.InvariantCulture));
        var intents = (await final.OutboxMessages.AsNoTracking().Where(x =>
            x.Type == FlowExecutionSubscriptionLimits.OutboxType).Select(x => x.PayloadJson).ToArrayAsync(ct))
            .Select(x => ExecutionLifecycleSubscriptions.ReadDelivery(x)!).ToArray();
        Assert.Single(intents, x => x.SourceEventId == fresh.Id && x.FlowVersionId == versionId &&
            x.ActiveSinceUtc == activeSince && x.SourcePayloadHash == ExecutionLifecycleSubscriptions.PayloadHash(fresh.PayloadJson));
        Assert.Equal(activationFirst ? 2 : 1, intents.Length);
        Assert.Equal(0, await final.JobRuns.CountAsync(ct));
        Assert.Equal(0, await final.FlowRuns.CountAsync(ct));
    }

    private static ServiceProvider Provider(string connection, IDataProtectionProvider protection, BoundaryGate gate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(protection);
        services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection).AddInterceptors(gate));
        services.AddNetRatelFlows();
        services.AddScoped<IRequestService, RequestService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task FailAsync(ServiceProvider provider, int requestId, CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<IRequestService>().UpdateAsync(
            new(requestId, null, null, null, null, "Failed", "Synthetic committed admission failure.", null, null, null), ct);
        Assert.NotNull(updated);
    }

    private static FlowDefinitionRecord Definition(Guid id, DateTimeOffset at, bool enabled) => new()
    {
        Id = id, TenantId = 7, Name = "Synthetic activation source", Revision = 1, Enabled = enabled,
        EnabledSinceUtc = enabled ? at : null, CreatedAtUtc = at, UpdatedAtUtc = at, DraftJson = "{}"
    };

    private static FlowVersionRecord Version(Guid id, Guid flowId, DateTimeOffset at)
    {
        var trigger = Guid.NewGuid();
        var finish = Guid.NewGuid();
        var graph = new FlowGraphDto(2,
            [new(trigger, FlowNodeKind.ExecutionEvent, "Synthetic request failure", new(0, 0))
                { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) },
             new(finish, FlowNodeKind.Finish, "Finish", new(250, 0))],
            [new(trigger, FlowNodeCatalogue.Event, finish, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
        Assert.True(FlowGraphV2Validator.ValidatePublished(graph).Valid);
        var json = JsonSerializer.Serialize(graph, Json);
        return new() { Id = id, FlowId = flowId, TenantId = 7, VersionNumber = 1, GraphJson = json,
            ConfigurationHash = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(json)),
            PublishedAtUtc = at, PublishedBy = "synthetic-operator",
            ExecutionAuthorityJson = "{\"principalId\":\"synthetic-operator\"}" };
    }

    private static async Task AssertWaitingForFenceAsync(DbContextOptions<OrchestratorDbContext> options,
        int processId, CancellationToken ct)
    {
        await using var db = new OrchestratorDbContext(options);
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var waiting = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value" FROM pg_locks
                WHERE pid = {processId} AND locktype = 'advisory' AND NOT granted
                """).SingleAsync(ct);
            if (waiting > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }
        Assert.Fail("The independent provider did not wait on the tenant transaction fence.");
    }

    private sealed class BoundaryGate(bool pauseAfterFence = false, bool pauseBeforeClock = false) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<int> _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _fencePaused;
        private int _clockPaused;
        public Task<int> FenceAttempted => _attempted.Task;
        public Task Paused => _paused.Task;
        public void Release() => _release.TrySetResult();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsFence(command)) _attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (pauseAfterFence && IsFence(command) && Interlocked.Exchange(ref _fencePaused, 1) == 0)
            {
                _paused.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (pauseBeforeClock && command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _clockPaused, 1) == 0)
            {
                _paused.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        private static bool IsFence(DbCommand command) =>
            command.CommandText.Contains("pg_advisory_xact_lock(", StringComparison.Ordinal);
    }
}
