using AwesomeAssertions;
using NetRatel.Client;
using NetRatel.Client.Service.Terminal;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class TerminalHostTests
{
    [Fact]
    public async Task Redirected_terminal_reports_the_account_id_observed_inside_the_shell()
    {
        var windows = OperatingSystem.IsWindows();
        var context = new TerminalHostContext(windows ? "cmd" : "sh", windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh",
            windows ? "/d" : "", null, 100, 30, new TerminalHostOptions());
        using var host = new RedirectedProcessTerminalHost(context);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new System.Text.StringBuilder();
        await host.StartAsync(chunk =>
        {
            if (chunk.Direction != "stdout" || output.Task.IsCompleted) return Task.CompletedTask;
            foreach (var line in buffer.Append(chunk.Data).ToString().Split('\n').SkipLast(1))
            {
                var text = line.Trim();
                if (windows ? text.StartsWith('"') && text.Contains("S-1-", StringComparison.Ordinal) : uint.TryParse(text, out _))
                    output.TrySetResult(text);
            }
            return Task.CompletedTask;
        }, timeout.Token);
        try
        {
            var observed = Assert.IsType<ObservedExecutionIdentity>(host.ObservedIdentity);
            await host.WriteInputAsync(windows ? "whoami /user /fo csv /nh\r\n" : "id -u\n", timeout.Token);
            var actual = await output.Task.WaitAsync(timeout.Token);
            if (windows) Assert.Contains($"\"{observed.AccountId}\"", actual, StringComparison.Ordinal);
            else Assert.Equal(observed.AccountId, actual);
        }
        finally { await host.RequestCloseAsync("test complete", timeout.Token); }
    }

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
