using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class LegacyApiSpacetimeServiceRemovalTests
{
    [Fact]
    public void UnregisteredLegacySpacetimeWorkersAndSubscriptions_AreRemoved()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

        foreach (var legacySource in new[]
                 {
                     "src/NetRatel/NetRatel.API/Application/AgentTelemetryWorker.cs",
                     "src/NetRatel/NetRatel.API/Application/ClientEnvironmentWorker.cs",
                     "src/NetRatel/NetRatel.API/Application/LatencyPingWorker.cs",
                     "src/NetRatel/NetRatel.API/Infrastructure/Spacetime/ApiSpacetimeSubscriptions.cs",
                     "src/NetRatel/NetRatel.API/Realtime/LatencyStore.cs",
                     "src/NetRatel/NetRatel.API/Realtime/AgentTelemetryRegistry.cs",
                     "src/NetRatel/NetRatel.API/Services/RemoteDesktop/RemoteDesktopSessionRegistry.cs",
                     "src/NetRatel/NetRatel.API/Services/Terminal/TerminalSessionRegistry.cs",
                     "src/NetRatel/NetRatel.API/Services/Terminal/TerminalTransportSessionRegistry.cs",
                     "src/NetRatel/NetRatel.API/Services/Spacetime/DedicatedSpacetimeConnectionFactory.cs",
                     "src/NetRatel/NetRatel.API/Services/FileSystem/FileBrowseTransportExecutor.cs",
                     "src/NetRatel/NetRatel.API/Services/FileSystem/FileBrowseRequestTracker.cs",
                     "src/NetRatel/NetRatel.API/Services/FileSystem/FileSystemTransportService.cs",
                     "src/NetRatel/NetRatel.API/Services/FileSystem/FileBrowseShadowObservationQueue.cs",
                     "src/NetRatel/NetRatel.API/Services/FileSystem/FileBrowseShadowTransportObserver.cs",
                     "src/NetRatel/NetRatel.API/Gateway/FileBrowseShadowHealthCheck.cs",
                     "src/NetRatel/NetRatel.API/Application/Mcp/Services/ClientTaskBridge.cs",
                     "src/NetRatel/NetRatel.API/Application/Mcp/Contracts/ToolContracts.cs",
                     "src/NetRatel/NetRatel.API/Infrastructure/Spacetime/SpacetimeClient.cs",
                     "src/NetRatel/NetRatel.API/Endpoints/Mcp/McpEndpoints.cs",
                     "src/NetRatel/NetRatel.API/Services/ClientTasks/ClientTaskTargetDisplay.cs",
                     "src/NetRatel/NetRatel.API/Services/SpacetimeConnectionTokenProvider.cs",
                     "src/NetRatel/NetRatel.API/Services/SpacetimeTokenOptions.cs",
                     "src/NetRatel/NetRatel.API/Services/Jobs/CommandTaskProjectionApplier.cs",
                     "src/NetRatel/NetRatel.API/Services/Jobs/JobExecutionProjectionApplier.cs",
                     "src/NetRatel/NetRatel.API/Services/Jobs/TaskLogProjectionApplier.cs",
                     "src/NetRatel/NetRatel.API/Services/Jobs/JobShadowProjectionObserver.cs",
                     "src/NetRatel/NetRatel.API/Services/Requests/RequestExecutionProjectionApplier.cs",
                     "src/NetRatel/NetRatel.API/Services/RemoteSupport/RemoteSupportShadowObservationQueue.cs",
                     "src/NetRatel/NetRatel.API/Services/RemoteSupport/RemoteSupportShadowProjectionObserver.cs",
                     "src/NetRatel/NetRatel.API/Gateway/RemoteSupportShadowHealthCheck.cs",
                     "src/NetRatel/NetRatel.Akka/RemoteSupport/ClientRemoteSupportRouterActor.cs",
                     "src/NetRatel/NetRatel.Application/RemoteSupport/RemoteSupportShadowContracts.cs",
                     "src/NetRatel/NetRatel.Infrastructure/Notifications/SpacetimeClientDisplayNameResolver.cs",
                     "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientRemoteDesktopDialog.razor",
                     "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientRemoteDesktopScreenshotDialog.razor",
                     "src/NetRatel/NetRatel.Web/Services/RemoteDesktop/RemoteDesktopApiService.cs",
                     "src/NetRatel/NetRatel.Web/Services/Spacetime/WebSpacetimeSubscriptions.cs"
                 })
        {
            File.Exists(Path.Combine(repositoryRoot, legacySource)).Should().BeFalse(legacySource);
        }
    }

    [Fact]
    public void RetiredModuleBindingsAndGenerationAssets_AreAbsentFromRetainedProjects()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

        foreach (var retiredPath in new[]
                 {
                     "NetRatel.Server/StdbModule.csproj",
                     "src/NetRatel/NetRatel.Shared/module_bindings",
                     "src/NetRatel/NetRatel.Shared/Service/SpacetimeDb/SpacetimeDbService.cs",
                     "src/NetRatel/NetRatel.Shared/Service/ClientEnvironment/ClientEnvironmentService.cs",
                     "src/NetRatel/NetRatel.Shared/Data/ViewModel/ClientViewModel.cs",
                     "src/NetRatel/NetRatel.Shared/SpacetimeIdentityHelpers.cs",
                     "src/NetRatel/NetRatel.Tests/LiveTestBase.cs",
                     "src/NetRatel/NetRatel.Tests/ExampleLiveTests.cs",
                     "build-wasi-and-gen.ps1",
                     "tools/update-spacetime.sh",
                     "src/NetRatel/NetRatel.Client/NetRatel - Backup.Client.csproj",
                     "tests/NetRatel.Client.IntegrationTests/NetRatel.Client.IntegrationTests.csproj"
                 })
        {
            var absolutePath = Path.Combine(repositoryRoot, retiredPath);
            (File.Exists(absolutePath) || Directory.Exists(absolutePath)).Should().BeFalse(retiredPath);
        }

        File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj"))
            .Should().NotContain("SpacetimeDB");
        File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj"))
            .Should().NotContain("SpacetimeDB");

        File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Application/Jobs/JobShadowContracts.cs"))
            .Should().NotContain("spacetimedb-job-execution-event");
    }

    [Fact]
    public void ActiveRemoteSupportGateway_DoesNotDependOnTheRetiredShadowSubsystem()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

        File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.API/Gateway/AgentRemoteSupportGatewayService.cs"))
            .Should().NotContain("RemoteSupportShadow");
    }

    [Theory]
    [InlineData("src/NetRatel/NetRatel.Web/appsettings.json")]
    [InlineData("src/NetRatel/NetRatel.Web/appsettings.Development.json")]
    public void WebConfiguration_DoesNotRetainTheUnusedSpacetimeConnection(string relativePath)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

        File.ReadAllText(Path.Combine(repositoryRoot, relativePath))
            .Should().NotContain("\"SpaceTime\"");
    }
}
