using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobRuntimeScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void JobRuntime_HasNoExecutionOrForbiddenSurfaceDependency()
    {
        var sources = string.Join('\n', new[]
        {
            Read("src/NetRatel/NetRatel.Application/Jobs/JobRuntimeContracts.cs"),
            Read("src/NetRatel/NetRatel.Akka/Jobs/JobCoordinatorActor.cs"),
            Read("src/NetRatel/NetRatel.Akka/Jobs/JobRunActor.cs"),
            Read("src/NetRatel/NetRatel.Infrastructure/Persistence/JobObservationStore.cs"),
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
    public void CurrentGatewayAuthorityAndContracts_RemainInPlace()
    {
        Read("src/NetRatel/NetRatel.API/Services/Jobs/AkkaJobAuthorityService.cs")
            .Should().Contain("IJobRuntimeRouter");
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

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
