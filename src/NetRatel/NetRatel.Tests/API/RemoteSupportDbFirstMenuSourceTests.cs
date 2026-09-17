using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportDbFirstMenuSourceTests
{
    [Fact]
    public void GatewayClientGrid_Does_Not_Load_Legacy_Inventory_On_Render()
    {
        var cardGrid = ReadRepoFile("NetRatel.Web", "Components", "Pages", "Clients", "ClientGridView.razor");

        cardGrid.Should().Contain("GatewayRemoteSupportDialog");
        cardGrid.Should().NotContain("LoadCachedWindowsSessionsAsync");
        cardGrid.Should().NotContain("RemoteSupportApi.GetWindowsSessionsAsync");
        cardGrid.Should().NotContain("Login to console");
    }

    [Fact]
    public void Explicit_Login_Target_Forces_Console_Provider_And_Resets_Stale_Helper()
    {
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportSessionManager.cs");
        var consolePipeHost = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportConsoleProviderPipeHost.cs");

        manager.Should().Contain("var loginTargetRequested = IsLoginTargetMode(targetMode);");
        manager.Should().Contain("!providerDecision.CanUseConsoleSecureDesktopHelper && !loginTargetRequested");
        manager.Should().Contain("ResetConnectedProviderIfVersionMismatch(_serviceVersion)");

        consolePipeHost.Should().Contain("ResetConnectedProviderIfVersionMismatch");
        consolePipeHost.Should().Contain("Resetting stale console provider");
        consolePipeHost.Should().Contain("Kill(entireProcessTree: true)");
        consolePipeHost.Should().Contain("Stale console provider was reset for relaunch.");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "NetRatel.sln");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(Path.Combine(new[] { directory.FullName, "src", "NetRatel" }.Concat(parts).ToArray()));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
