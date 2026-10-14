using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobRunStreamSnapshotTests
{
    [Fact]
    public async Task Recovered_snapshot_bounds_output_keeps_sequence_order_and_separates_errors_from_logs()
    {
        await using var db = CreateDb();
        var runs = new JobRunService(db);
        var jobs = new JobDefinitionService(db);
        var job = await jobs.CreateAsync(new("Disposable", "/", null, 7, ""));
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        await runs.UpsertRunAsync(new(10, job.Id, 7, "", "tester", JobRunState.Unknown,
            2, now, null, now.AddMinutes(1), "Outcome receipt missing", null, null,
            Environment: ClientEnvironment.Dev, TargetDisplayName: "Recorded target"));
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            await runs.UpsertStepRunAsync(new((ulong)(10 + ordinal), 10, (ulong)ordinal,
                JobStepRunState.Unknown, ordinal, $"task-{ordinal}", "Outcome receipt missing", null, now.AddMinutes(1)));
            var activity = await runs.CreateTaskActivityAsync(new($"task-{ordinal}", 10,
                (ulong)ordinal, "", 7, "exec-shell-cmd", "Unknown", "Technical diagnostic",
                now, now.AddMinutes(1)));
            await runs.AppendTaskLogAsync(new(activity.RequestId, activity.Id, "", 7,
                "stdout", new string('a', 40_000), 1, now));
            await runs.AppendTaskLogAsync(new(activity.RequestId, activity.Id, "", 7,
                "stderr", "latest output", 2, now));
        }
        var snapshot = await JobRunEndpoints.BuildStreamSnapshotAsync(10, runs, jobs, new TenantService(db), db, default);
        snapshot.Should().NotBeNull();
        snapshot!.Run.ClientDisplayName.Should().Be("Recorded target");
        snapshot.Run.Environment.Should().Be(ClientEnvironment.Dev);
        snapshot.Run.StartedAtMicros.Should().BeNull();
        snapshot.Steps.Should().OnlyContain(x => x.LogText == null && x.StartedAtMicros == null);
        snapshot.Logs.SelectMany(x => x.Logs).Sum(x => Encoding.UTF8.GetByteCount(x.Message))
            .Should().BeLessThanOrEqualTo(JobRunEndpoints.StreamOutputBytes);
        snapshot.Logs.Should().Contain(x => x.Truncated);
        foreach (var step in snapshot.Logs)
            step.Logs.Select(x => x.Seq).Should().BeInAscendingOrder();
        snapshot.Logs.Last().Logs.Last().Message.Should().Be("latest output");
    }

    [Fact]
    public async Task Historical_snapshot_does_not_resolve_a_reassigned_agent_from_another_tenant()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = agentId, TenantId = 8, Name = "Private other tenant target" });
        await db.SaveChangesAsync();
        var jobs = new JobDefinitionService(db);
        var job = await jobs.CreateAsync(new("Historical", "/", null, 7, ""));
        var runs = new JobRunService(db);
        var now = DateTimeOffset.Parse("2026-10-14T10:00:00Z");
        await runs.UpsertRunAsync(new(10, job.Id, 7, "", "tester", JobRunState.Failed,
            1, now, null, now, "Dispatch rejected", null, null, agentId));
        var snapshot = await JobRunEndpoints.BuildStreamSnapshotAsync(10, runs, jobs, new TenantService(db), db, default);
        snapshot.Should().NotBeNull();
        snapshot!.Run.ClientDisplayName.Should().BeNull();
        snapshot.Run.Environment.Should().BeNull();
        snapshot.Run.StartedAtMicros.Should().BeNull();
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
