using FluentAssertions;
using NetRatel.Application.Terminals;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TerminalShadowScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Phase6ContractsAndActors_CannotCarryOrPersistRawTerminalContent()
    {
        var contractProperties = typeof(TerminalShadowEvent)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();
        contractProperties.Should().NotContain(new[]
        {
            "Data",
            "Payload",
            "PayloadJson",
            "Transcript",
            "Command",
            "WorkingDirectory",
            "CloseReason",
            "TimingId"
        });

        var sources = string.Join('\n', new[]
        {
            Read("src/NetRatel/NetRatel.Application/Terminals/TerminalShadowContracts.cs"),
            Read("src/NetRatel/NetRatel.Akka/Terminals/TerminalSessionActor.cs"),
            Read("src/NetRatel/NetRatel.Akka/Terminals/ClientTerminalRouterActor.cs"),
            Read("src/NetRatel/NetRatel.API/Services/Terminal/TerminalShadowObservationQueue.cs")
        });

        sources.Should().NotContain("Akka.Persistence");
        sources.Should().NotContain("ClusterSharding");
        sources.Should().NotContain("SignalR");
        sources.Should().NotContain("MapGrpc");
        sources.Should().NotContain("DbContext");
        sources.Should().NotContain("TerminalInputRequest");
        sources.Should().NotContain("TerminalDirectFrame");
        sources.Should().NotContain("string Data");
        sources.Should().NotContain("string Payload");
        sources.Should().Contain("CreateBounded<TerminalShadowEvent>");
        sources.Should().Contain("TryWrite(observation)");
    }

    [Fact]
    public void ProductionTerminalOwnersAndForbiddenSurfaces_RemainInPlace()
    {
        File.Exists(Path.Combine(RepoRoot, "NetRatel.Server/StdbModule.csproj")).Should().BeFalse();
        Read("src/NetRatel/NetRatel.Client/Service/Gateway/AgentTerminalGatewayClient.cs").Should().NotContain("TerminalShadow");
        Read("src/NetRatel/NetRatel.Web/Services/Terminal/TerminalService.cs").Should().NotContain("TerminalShadow");
        Read("src/NetRatel/NetRatel.Web/Components/Pages/Terminal/TerminalConsole.razor").Should().NotContain("TerminalShadow");
        Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto").Should().NotContain("TerminalShadow");
        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj").Should().NotContain("Akka.Persistence");
        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj").Should().Contain("Akka.Cluster.Hosting");
    }

    [Fact]
    public void ObservationHooks_RunAfterExistingTerminalOperations()
    {
        var direct = Read("src/NetRatel/NetRatel.API/Services/Terminal/TerminalDirectTunnelRegistry.cs");

        AssertOrdered(
            direct,
            "await tunnel.SendAsync(new TerminalDirectFrame(\n            Type: TerminalDirectFrameTypes.Stdin",
            "ObserveStream(\n            session,\n            TerminalShadowEventKind.InputObserved");
        AssertOrdered(
            direct,
            "var (firstOutput, channelWritten) = session.WriteData",
            "TerminalShadowEventKind.OutputObserved");
    }

    private static void AssertOrdered(string source, string existingOperation, string observation)
    {
        var operationIndex = source.IndexOf(existingOperation, StringComparison.Ordinal);
        var observationIndex = source.IndexOf(observation, operationIndex, StringComparison.Ordinal);
        operationIndex.Should().BeGreaterThanOrEqualTo(0);
        observationIndex.Should().BeGreaterThan(operationIndex);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
