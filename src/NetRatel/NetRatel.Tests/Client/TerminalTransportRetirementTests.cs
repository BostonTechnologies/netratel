using FluentAssertions;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class TerminalTransportRetirementTests
{
    [Fact]
    public void New_Terminal_Session_Dtos_Default_To_The_Akka_Gateway()
    {
        var session = new TerminalSessionDto(
            "session-1",
            "client-a",
            "powershell",
            "opened",
            true,
            1,
            1,
            null);

        session.Transport.Should().Be(TerminalTransportKind.AkkaGateway);
    }

    [Fact]
    public void Client_Settings_Do_Not_Expose_A_Retired_Transport_Selector()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var sourcePath = Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientSettingsDialog.razor");

        File.Exists(sourcePath).Should().BeFalse("the old client settings dialog has no callers and only exposes retired transport choices");
    }
}
