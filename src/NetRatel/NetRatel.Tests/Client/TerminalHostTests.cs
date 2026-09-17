using FluentAssertions;
using NetRatel.Client;
using NetRatel.Client.Service.Terminal;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class TerminalHostTests
{
    [Fact]
    public void NativeUnixPty_IsEnabledByDefault()
    {
        new ClientOptions().EnableNativeUnixPty.Should().BeTrue();
        new TerminalHostOptions().EnableNativeUnixPty.Should().BeTrue();
    }

    [Fact]
    public void NativeUnixPty_UsesValidShellArgumentVector()
    {
        NativeUnixPtyTerminalHost.BuildShellArgv("exec bash -l")
            .Should()
            .Equal("/bin/sh", "-lc", "exec bash -l");
    }

    [Theory]
    [InlineData(1, 0, 2, 1)]
    [InlineData(192, 45, 192, 45)]
    [InlineData(500, 200, 300, 120)]
    public void NativeUnixPty_ClampsTerminalDimensions(
        int cols,
        int rows,
        int expectedCols,
        int expectedRows)
    {
        NativeUnixPtyTerminalHost.ClampSize(cols, rows)
            .Should()
            .Be((expectedCols, expectedRows));
    }

    [Fact]
    public void GatewayTerminalClient_AdvertisesResizeWithoutASpacetimeFallback()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentTerminalGatewayClient.cs"));
        var source = File.ReadAllText(path);

        source.Should().Contain("\"resize\"");
        source.Should().Contain("\"idempotent-close\"");
        source.Should().NotContain("using Spacetime");
    }

    [Fact]
    public void NativeUnixPty_IsPreferredAndPythonHelperIsRetainedAsPublishedFallback()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var hostSource = File.ReadAllText(Path.Combine(
            root,
            "src/NetRatel/NetRatel.Client/Service/Terminal/TerminalHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"));

        hostSource.IndexOf("NativeUnixPtyTerminalHost.TryCreate", StringComparison.Ordinal)
            .Should()
            .BeLessThan(hostSource.IndexOf("UnixPtyHelperTerminalHost.TryCreate", StringComparison.Ordinal));
        hostSource.Should().Contain("libnetratel_terminal_pty.so");
        hostSource.IndexOf("Path.GetDirectoryName(Environment.ProcessPath)", StringComparison.Ordinal)
            .Should()
            .BeLessThan(hostSource.IndexOf(
                "Path.Combine(AppContext.BaseDirectory, \"terminal_pty_helper.py\")",
                StringComparison.Ordinal));
        project.Should().Contain("Service\\Terminal\\terminal_pty_helper.py");
        project.Should().Contain("<TargetPath>terminal_pty_helper.py</TargetPath>");
        project.Should().Contain("<CopyToPublishDirectory>Always</CopyToPublishDirectory>");
    }
}
