using FluentAssertions;
using NetRatel.API.Services.Requests;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RequestJobRunLinkResolverTests
{
    [Fact]
    public void Resolve_Uses_RundeckExecutionId_When_It_Matches_A_Known_JobRun()
    {
        var request = CreateRequest(rundeckExecutionId: "4124");
        var run = CreateRun(4124, 4);

        var resolved = RequestJobRunLinkResolver.Resolve(request, new Dictionary<ulong, JobRunInfo>
        {
            [run.Id] = run
        });

        resolved.Should().Be(run);
    }

    [Fact]
    public void Resolve_Uses_Latest_Log_JobRun_When_ExecutionId_Is_Not_A_Local_Run()
    {
        var request = CreateRequest(
            rundeckExecutionId: "legacy-abc",
            logs:
            [
                "[2026-05-12T10:00:00+00:00] Accepted. JobRun 4000 started.",
                "[2026-05-12T10:01:00+00:00] Callback status 'succeeded' for JobRun 4124."
            ]);

        var olderRun = CreateRun(4000, 3);
        var latestRun = CreateRun(4124, 4);

        var resolved = RequestJobRunLinkResolver.Resolve(request, new Dictionary<ulong, JobRunInfo>
        {
            [olderRun.Id] = olderRun,
            [latestRun.Id] = latestRun
        });

        resolved.Should().Be(latestRun);
    }

    [Fact]
    public void Resolve_Returns_Null_When_Candidates_Do_Not_Match_Local_JobRuns()
    {
        var request = CreateRequest(
            rundeckExecutionId: "9999",
            logs: ["[2026-05-12T10:00:00+00:00] Callback status 'failed' for JobRun 8888."]);

        RequestJobRunLinkResolver.Resolve(request, new Dictionary<ulong, JobRunInfo>())
            .Should()
            .BeNull();
    }

    [Fact]
    public void EnumerateCandidateRunIds_Deduplicates_And_Prefers_ExecutionId_Before_Log_Fallbacks()
    {
        var request = CreateRequest(
            rundeckExecutionId: "4124",
            logs:
            [
                "[2026-05-12T10:00:00+00:00] Accepted. JobRun 4000 started.",
                "[2026-05-12T10:01:00+00:00] Callback status 'succeeded' for JobRun 4124."
            ],
            resultMessage: "Final callback for JobRun 5000");

        RequestJobRunLinkResolver.EnumerateCandidateRunIds(request)
            .Should()
            .Equal(4124, 4000, 5000);
    }

    private static RequestInfo CreateRequest(
        string? rundeckExecutionId = null,
        IReadOnlyList<string>? logs = null,
        string? resultMessage = null)
        => new(
            10,
            "external-service.api",
            "client-1",
            "5",
            rundeckExecutionId,
            "Success",
            resultMessage,
            null,
            "{}",
            logs ?? Array.Empty<string>(),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow);

    private static JobRunInfo CreateRun(ulong runId, ulong jobId)
        => new(
            runId,
            jobId,
            1,
            "client-1",
            "tests",
            JobRunState.Succeeded,
            1,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-4),
            DateTimeOffset.UtcNow.AddMinutes(-3),
            null,
            "{}",
            "{}");
}
