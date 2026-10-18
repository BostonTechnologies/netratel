using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobRunServiceTests
{
    [Fact]
    public async Task Reviewed_execution_snapshot_survives_service_reload_without_entering_ordinary_run_output()
    {
        await using var db = CreateDb();
        var keys = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var service = new JobRunService(db, keys);
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();
        const string scriptContent = "printf 'private-reviewed-script-snapshot'";
        var snapshot = new McpOperatorJobExecutionSnapshot(
            new JobDefinitionDetails(new JobDefinitionInfo(11, "reviewed-job", "/", null, 7, string.Empty, now, now, null, agentId), [], []),
            2, "operator", "client", "https://mcp.example.test/mcp", "prod", new string('A', 64), [],
            [new McpOperatorScriptExecutionSnapshot(new ScriptInfo(12, "reviewed-script", "/", "fixture", scriptContent, null, "bash", now, now), [], 1, new string('B', 64))]);
        service.BindMcpExecutionSnapshot(21, snapshot);
        await service.UpsertRunAsync(new UpsertJobRunCommand(21, 11, 7, string.Empty, "mcp-reviewed:operator", JobRunState.Pending,
            0, now, null, null, null, "{}", null, agentId));
        db.ChangeTracker.Clear();
        var reloaded = new JobRunService(db, keys);

        var savedSnapshot = await reloaded.GetMcpExecutionSnapshotAsync(21);
        var ordinary = await reloaded.GetAsync(21);

        savedSnapshot.Should().NotBeNull();
        savedSnapshot!.ComputeDigest().Should().Be(snapshot.ComputeDigest());
        ordinary!.SubmittedDefinitionRevision.Should().Be(snapshot.ComputeDigest());
        System.Text.Json.JsonSerializer.Serialize(ordinary).Should().NotContain(scriptContent);
        (await db.JobRuns.SingleAsync()).ProtectedExecutionJson.Should().NotContain(scriptContent);
    }

    [Fact]
    public async Task CreateTaskActivityAsync_BackToBackRequests_GetDistinctActivityIds()
    {
        await using var db = CreateDb();
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
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
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
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
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
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
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var created = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            "terminal-result-request", null, null, "client", 3, "exec-library-script", "Processing", null,
            DateTimeOffset.UtcNow, null, OutputPolicy: TaskOutputPolicy.Create([])));
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
        var service = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var created = await service.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            "idempotent-result-request", null, null, "client", 3, "exec-library-script", "Processing", null,
            DateTimeOffset.UtcNow, null, OutputPolicy: TaskOutputPolicy.Create([])));
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
