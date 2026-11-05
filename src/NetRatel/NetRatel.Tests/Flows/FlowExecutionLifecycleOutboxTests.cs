using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionLifecycleOutboxTests
{
    [Theory]
    [InlineData("Success", FlowExecutionOutcome.Succeeded)]
    [InlineData("Completed", FlowExecutionOutcome.Succeeded)]
    [InlineData("Failed", FlowExecutionOutcome.Failed)]
    [InlineData("TimedOut", FlowExecutionOutcome.TimedOut)]
    [InlineData("Unknown", FlowExecutionOutcome.Unknown)]
    public async Task Request_only_terminal_transition_is_durable_without_a_job(string status, FlowExecutionOutcome expected)
    {
        await using var db = Memory();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var row = await requests.CreateAsync(new("synthetic-source", string.Empty, null, null, 7, null));
        await requests.UpdateAsync(new(row.Id, null, null, null, null, status, "synthetic result", null, null, null));
        var saved = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        var value = Read(saved);
        Assert.Equal(FlowExecutionSourceKind.Request, value.SourceKind);
        Assert.Equal(expected, value.Outcome);
        Assert.Equal(status, value.RawStatus);
        Assert.Equal("synthetic-source", value.RequestKind);
        Assert.Equal("request:" + row.Id, value.CorrelationId);
        Assert.Equal(row.Id.ToString(), value.ExecutionId);
        Assert.Null(value.ParentExecutionId);
        Assert.False(value.Replay!.Supported);

        await requests.UpdateAsync(new(row.Id, null, null, null, null, status, "diagnostic edit", null, null, null));
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Script_terminal_fact_keeps_submission_identity_after_later_edits_and_has_no_inline_output()
    {
        await using var db = Memory();
        var now = DateTimeOffset.UtcNow;
        var activity = new JobTaskActivityRecord
        {
            Id = 20, RequestId = "synthetic-script-attempt", TenantId = 7, AgentId = Guid.NewGuid(),
            TaskType = TaskKinds.ExecLibraryScript, Status = "Pending", ScriptName = "Synthetic diagnosis",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64))),
            CreatedAtUtc = now, StartedAtUtc = now
        };
        db.JobTaskActivities.Add(activity);
        await db.SaveChangesAsync();
        activity.Status = "Completed";
        activity.CompletedAtUtc = now.AddSeconds(1);
        activity.ResultJson = "{\"stdout\":\"synthetic bounded output\"}";
        await db.SaveChangesAsync();
        var saved = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        var value = Read(saved);
        Assert.Equal("91", value.Definition.Id);
        Assert.Equal("4", value.Definition.Revision);
        Assert.Equal("Synthetic diagnosis", value.Definition.Name);
        Assert.Equal(new string('A', 64), value.Definition.ContentHash);
        Assert.DoesNotContain("synthetic bounded output", saved.PayloadJson);
        Assert.True(Assert.Single(value.Results!).Available);

        activity.ScriptName = "Renamed after execution";
        await db.SaveChangesAsync();
        Assert.Equal("Synthetic diagnosis", Read(Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync())).Definition.Name);

        var fact = await db.OutboxMessages.SingleAsync();
        fact.PayloadJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static FlowExecutionEventDto Read(OutboxMessage row) =>
        JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
    private static OrchestratorDbContext Memory() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionLifecycleOutboxPostgreSqlTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Request_state_and_fact_roll_back_together_and_direct_projection_keeps_one_terminal_event()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        int id;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            var requests = new RequestService(db, keys);
            id = (await requests.CreateAsync(new("synthetic-source", string.Empty, null, null, 7, null), ct)).Id;
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await requests.UpdateAsync(new(id, null, null, null, null, "Failed", null, null, null, null), ct);
                Assert.Single(await db.OutboxMessages.ToArrayAsync(ct));
                await transaction.RollbackAsync(ct);
            }
        }

        await using (var db = new OrchestratorDbContext(options))
        {
            var requests = new RequestService(db, keys);
            Assert.Equal("New", (await db.Requests.SingleAsync(ct)).Status);
            Assert.Empty(await db.OutboxMessages.ToArrayAsync(ct));
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Failed, "Synthetic failure", null, DateTimeOffset.UtcNow, ct));
        }

        await using (var db = new OrchestratorDbContext(options))
        {
            var requests = new RequestService(db, keys);
            Assert.Equal("Failed", (await db.Requests.AsNoTracking().SingleAsync(ct)).Status);
            var fact = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync(ct));
            var value = JsonSerializer.Deserialize(fact.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal(FlowExecutionOutcome.Failed, value.Outcome);
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Failed, "Duplicate callback", null, DateTimeOffset.UtcNow, ct));
            Assert.False(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Running, null, null, DateTimeOffset.UtcNow, ct));
            Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync(ct));
            Assert.Equal("Failed", (await db.Requests.AsNoTracking().SingleAsync(ct)).Status);
        }
    }
}
