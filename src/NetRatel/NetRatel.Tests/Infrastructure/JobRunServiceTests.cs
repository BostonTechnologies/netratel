using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobRunServiceTests
{
    [Fact]
    public async Task CreateTaskActivityAsync_BackToBackRequests_GetDistinctActivityIds()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db);
        var now = DateTimeOffset.UtcNow;

        var first = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            RequestId: "task-request-1",
            JobRunId: 102,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now,
            CompletedAtUtc: null));

        var second = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            RequestId: "task-request-2",
            JobRunId: 103,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now.AddMilliseconds(1),
            CompletedAtUtc: null));

        first.RequestId.Should().Be("task-request-1");
        second.RequestId.Should().Be("task-request-2");
        first.Id.Should().NotBe(second.Id);
        second.Id.Should().BeGreaterThan(first.Id);
    }

    [Fact]
    public async Task CreateTaskActivityAsync_DuplicateRequestId_ReturnsExistingActivity()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db);
        var now = DateTimeOffset.UtcNow;

        var first = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            RequestId: "same-task-request",
            JobRunId: 102,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now,
            CompletedAtUtc: null));

        var second = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            RequestId: "same-task-request",
            JobRunId: 102,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now,
            CompletedAtUtc: null));

        second.Id.Should().Be(first.Id);
        (await db.JobTaskActivities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertTaskActivityAsync_BackToBackRequests_GetDistinctActivityIds()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db);
        var now = DateTimeOffset.UtcNow;

        var first = await service.UpsertTaskActivityAsync(new UpsertJobTaskActivityCommand(
            ActivityId: 0,
            RequestId: "projected-task-request-1",
            JobRunId: 104,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now,
            CompletedAtUtc: null));

        var second = await service.UpsertTaskActivityAsync(new UpsertJobTaskActivityCommand(
            ActivityId: 0,
            RequestId: "projected-task-request-2",
            JobRunId: 105,
            JobStepId: 32,
            ClientIdentity: "client",
            TenantId: 3,
            TaskType: "exec-library-script",
            Status: "Processing",
            Error: null,
            CreatedAtUtc: now.AddMilliseconds(1),
            CompletedAtUtc: null));

        first.RequestId.Should().Be("projected-task-request-1");
        second.RequestId.Should().Be("projected-task-request-2");
        first.Id.Should().NotBe(second.Id);
        second.Id.Should().BeGreaterThan(first.Id);
    }

    [Fact]
    public async Task UpdateTaskActivityStatusAsync_PersistsTerminalResultSeparatelyFromFailureSummary()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db);
        var created = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            "terminal-result-request", null, null, "client", 3, "exec-library-script", "Processing", null,
            DateTimeOffset.UtcNow, null));
        const string result = "{\"stdout\":[\"completed\"],\"stderr\":[\"failed detail\"],\"exitCode\":1}";

        var updated = await service.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            created.RequestId,
            "Failed",
            "failed detail",
            DateTimeOffset.UtcNow,
            result));

        updated.Should().NotBeNull();
        updated!.Status.Should().Be("Failed");
        updated.Error.Should().Be("failed detail");
        updated.ResultJson.Should().Be(result);
        (await db.JobTaskActivities.SingleAsync()).ResultJson.Should().Be(result);
    }

    [Fact]
    public async Task UpdateTaskActivityStatusAsync_DuplicateOrLateLifecycleDoesNotOverwriteTerminalResult()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db);
        var created = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            "idempotent-result-request", null, null, "client", 3, "exec-library-script", "Processing", null,
            DateTimeOffset.UtcNow, null));
        const string completedResult = "{\"stdout\":[\"completed\"],\"exitCode\":0}";

        await service.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            created.RequestId,
            "Completed",
            null,
            DateTimeOffset.UtcNow,
            completedResult));
        var late = await service.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            created.RequestId,
            "Processing",
            null,
            null,
            "{\"stdout\":[\"late\"]}"));

        late.Should().NotBeNull();
        late!.Status.Should().Be("Completed");
        late.ResultJson.Should().Be(completedResult);
        late.Error.Should().BeNull();
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(options);
    }
}
