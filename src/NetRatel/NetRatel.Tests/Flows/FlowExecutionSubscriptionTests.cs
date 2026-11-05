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

    private static FlowVersionRecord Version(Guid id, Guid flowId, Guid triggerId, int number, DateTimeOffset published)
    {
        var finish = Guid.NewGuid();
        var graph = new FlowGraphDto(2,
            [new(triggerId, FlowNodeKind.ExecutionEvent, "Synthetic execution event", new(0, 0))
                { ExecutionTrigger = FlowExecutionSubscriptionWaitTests.Trigger() },
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
