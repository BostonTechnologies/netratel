using AwesomeAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class CommandRuntimeScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void ClientCommandSubscription_IsRetiredInFavourOfTheGateway()
    {
        File.Exists(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Client/Service/Spacetime/ClientSpacetimeSubscriptions.cs"))
            .Should().BeFalse();

        var gateway = Read("src/NetRatel/NetRatel.Client/Service/Gateway/AgentCommandGatewayClient.cs");
        gateway.Should().Contain("AgentCommandGateway");
        gateway.Should().NotContain("using Spacetime");
    }

    [Fact]
    public void CommandRuntime_HasNoForbiddenSurfaceDependency()
    {
        var sources = string.Join('\n', new[]
        {
            Read("src/NetRatel/NetRatel.Application/Commands/ClientCommandContracts.cs"),
            Read("src/NetRatel/NetRatel.Akka/Commands/CommandActor.cs"),
            Read("src/NetRatel/NetRatel.Akka/Commands/ClientCommandRouterActor.cs"),
            Read("src/NetRatel/NetRatel.API/Gateway/AgentCommandGatewayService.cs")
        });

        sources.Should().NotContain("DispatchCommand");
        sources.Should().NotContain("PublishCommandAck");
        sources.Should().NotContain("PublishCommandResult");
        sources.Should().NotContain("CancelCommand");
        sources.Should().NotContain("Akka.Persistence");
        sources.Should().NotContain("ClusterSharding");
        sources.Should().NotContain("TerminalSession");
        sources.Should().NotContain("FileBrowse");
        sources.Should().NotContain("RemoteSupportSession");
        sources.Should().NotContain("RemoteDesktopSession");

        var gatewayService = Read("src/NetRatel/NetRatel.API/Gateway/AgentCommandGatewayService.cs");
        gatewayService.Should().Contain("IJobRunService jobRuns");
        gatewayService.Should().Contain("result.Disposition == CommandMessageDisposition.Accepted");
        gatewayService.Should().Contain("await jobRuns.UpdateTaskActivityStatusAsync");
    }

    [Fact]
    public void CommandPersistence_PreservesLifecycleHistoryWithoutDispatchingCommands()
    {
        var sources = string.Join('\n', new[]
        {
            Read("src/NetRatel/NetRatel.Application/Commands/CommandPersistenceContracts.cs"),
            Read("src/NetRatel/NetRatel.Infrastructure/Persistence/CommandInbox.cs"),
            Read("src/NetRatel/NetRatel.Infrastructure/Persistence/CommandOutbox.cs"),
            Read("src/NetRatel/NetRatel.Infrastructure/Persistence/CommandPersistenceStore.cs"),
            Read("src/NetRatel/NetRatel.Akka/Commands/CommandActor.cs")
        });

        sources.Should().NotContain("DispatchCommandCore");
        sources.Should().NotContain("PublishCommandAck");
        sources.Should().NotContain("PublishCommandResult");
        sources.Should().NotContain("CancelCommand");
        sources.Should().NotContain("Akka.Persistence");
        sources.Should().NotContain("ClusterSharding");
        sources.Should().NotContain("TerminalSession");
        sources.Should().NotContain("JobRunService");
        sources.Should().NotContain("FileBrowse");
        sources.Should().NotContain("RemoteSupportSession");
        sources.Should().NotContain("RemoteDesktopSession");

        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj").Should().NotContain("Akka.Persistence");
        File.Exists(Path.Combine(RepoRoot, "NetRatel.Server"))
            .Should().BeFalse();
    }

    [Fact]
    public void CurrentCommandGateway_UsesItsOwnRpcAndLegacyCommandEventContractRemainsStable()
    {
        var proto = Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto");
        var commandService = ServiceBlock(proto, "AgentCommandShadowGateway");
        var runtimeRegistration = Read("src/NetRatel/NetRatel.API/Gateway/NetRatelAkkaRuntimeRegistration.cs");

        commandService.Should().Contain("PublishCommandEvents");
        commandService.Should().Contain("CommandShadowFrame");
        commandService.Should().NotContain("Telemetry");
        commandService.Should().NotContain("Terminal");
        commandService.Should().NotContain("FileBrowser");
        commandService.Should().NotContain("RemoteSupport");
        commandService.Should().NotContain("RemoteDesktop");
        runtimeRegistration.Should().Contain("MapGrpcService<AgentCommandGatewayService>()");
        runtimeRegistration.Should().NotContain("AgentCommandShadowGatewayService");
        File.Exists(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Gateway/AgentCommandShadowGatewayService.cs"))
            .Should().BeFalse();
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string ServiceBlock(string proto, string serviceName)
    {
        var start = proto.IndexOf($"service {serviceName}", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{serviceName} must exist in the gateway contract");

        var end = proto.IndexOf("\n}", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"{serviceName} must have a closing service block");
        return proto[start..(end + 2)];
    }
}
