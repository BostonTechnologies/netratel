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
    public void Client_Settings_No_Longer_Offer_The_Retired_Transport()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientSettingsDialog.razor"));

        source.Should().Contain("TerminalTransportKind.AkkaGateway");
        source.Should().NotContain("Value=\"@TerminalTransportKind.Spacetime.ToString()\"");
    }
}
