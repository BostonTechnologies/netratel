using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class PrimaryCardGatewayRemoteSupportCutoverSourceTests
{
    [Fact]
    public void ClientGridView_Routes_AgentId_Records_To_The_Gateway_RemoteSupportDialog()
    {
        var source = ReadRepositoryFile("NetRatel.Web", "Components", "Pages", "Clients", "ClientGridView.razor");

        source.Should().Contain("GatewayRemoteSupportDialog");
        source.Should().Contain("OpenRemoteSupportAsync");
        source.Should().Contain("client.AgentId");
    }

    [Fact]
    public void GatewayDialog_BindsTheExactRequestedTargetToTheV2Lifecycle()
    {
        var source = ReadRepositoryFile(
            "NetRatel.Web",
            "Components",
            "Dialogs",
            "GatewayRemoteSupportDialog.razor");

        source.Should().Contain("new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser");
        source.Should().Contain("OpenV2Async");
        source.Should().Contain("PrepareV2MediaAsync");
        source.Should().Contain("target.WindowsSessionId");
        source.Should().Contain("target.UserSidHash");
        source.Should().Contain("_inventory?.InventorySequence");
        source.Should().Contain("OpenConsoleAsync");
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine([repositoryRoot, "src", "NetRatel", .. segments]));
    }
}
