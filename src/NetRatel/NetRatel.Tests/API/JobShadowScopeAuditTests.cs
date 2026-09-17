using FluentAssertions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobShadowScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Phase5Implementation_HasNoExecutionOrForbiddenSurfaceDependency()
    {
        var sources = string.Join('\n', new[]
        {
            Read("src/NetRatel/NetRatel.Application/Jobs/JobShadowContracts.cs"),
            Read("src/NetRatel/NetRatel.Akka/Jobs/JobCoordinatorActor.cs"),
            Read("src/NetRatel/NetRatel.Akka/Jobs/JobRunActor.cs"),
            Read("src/NetRatel/NetRatel.Infrastructure/Persistence/JobShadowPersistenceStore.cs"),
            Read("src/NetRatel/NetRatel.API/Services/Jobs/JobShadowObservationQueue.cs")
        });

        sources.Should().NotContain("DispatchCommand(");
        sources.Should().NotContain("DispatchCommandCore");
        sources.Should().NotContain("CancelCommand(");
        sources.Should().NotContain("PublishJobRunSnapshot(");
        sources.Should().NotContain("PublishJobStepSnapshot(");
        sources.Should().NotContain("Akka.Persistence");
        sources.Should().NotContain("ClusterSharding");
        sources.Should().NotContain("TerminalSession");
        sources.Should().NotContain("FileBrowse");
        sources.Should().NotContain("RemoteSupportSession");
        sources.Should().NotContain("RemoteDesktopSession");
        sources.Should().NotContain("IBackgroundJobClient");
        sources.Should().NotContain("IJobRunService");
    }

    [Fact]
    public void GatewayAuthorityAndContracts_RemainInPlace()
    {
        Read("src/NetRatel/NetRatel.API/Services/Jobs/AkkaJobAuthorityService.cs")
            .Should().Contain("IJobShadowRouter");
        Read("src/NetRatel/NetRatel.API/Services/Jobs/HangfireJobDispatcher.cs")
            .Should().Contain("IAkkaJobAuthorityService");
        Read("src/NetRatel/NetRatel.API/Services/Jobs/JobTaskBridge.cs")
            .Should().NotContain("Spacetime");
        File.Exists(Path.Combine(RepoRoot, "NetRatel.Server"))
            .Should().BeFalse();
        Read("src/NetRatel/NetRatel.Client/Service/Gateway/AgentJobGatewayClient.cs")
            .Should().Contain("AgentJobGateway");
        Read("src/NetRatel/NetRatel.Client/Service/Tasks/ClientTaskManager.cs")
            .Should().NotContain("Spacetime");
        Read("src/NetRatel/NetRatel.Infrastructure/Persistence/CommandOutbox.cs")
            .Should().NotContain("JobShadow");
        Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto")
            .Should().NotContain("JobShadow");
        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj")
            .Should().NotContain("Akka.Persistence");
    }

    [Fact]
    public void ShadowIngressQueue_IsBoundedAndDropsWithoutBlocking()
    {
        var queue = new JobShadowObservationQueue(new NeverCalledRouter());
        for (var index = 1; index <= 1_024; index++)
        {
            queue.TryEnqueue(CreateObservation(index)).Should().BeTrue();
        }

        queue.TryEnqueue(CreateObservation(1_025)).Should().BeFalse();
        var status = queue.GetStatus();
        status.Enqueued.Should().Be(1_024);
        status.Dropped.Should().Be(1);
    }

    private static JobRunShadowObservation CreateObservation(long sourceEventId)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new(
            sourceEventId,
            checked((ulong)sourceEventId),
            JobId: 1,
            TenantId: 1,
            ClientIdentity: "client-a",
            StartedBy: "test",
            Status: JobRunState.Pending,
            CurrentStepOrdinal: 0,
            CreatedAtUtc: timestamp,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            Timestamp: timestamp);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private sealed class NeverCalledRouter : IJobShadowRouter
    {
        public Task<JobShadowMessageResult> RecordAsync(
            RecordJobShadowObservation message,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The queue is not started in this bounded-capacity test.");

        public Task<JobRunShadowState> GetStateAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
