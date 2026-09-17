using System.Text.Json;
using FluentAssertions;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class CommandExitStatusTests
{
    [Theory]
    [InlineData(0, "completed")]
    [InlineData(7, "failed")]
    public async Task Gateway_shell_status_uses_exit_code_and_preserves_both_output_streams(int exitCode, string expected)
    {
        var completion = new TaskCompletionSource<(string Status, string? Result, int? ExitCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager(false, (_, status, result, code) =>
        {
            if (status is "completed" or "failed" or "cancelled") completion.TrySetResult((status, result, code));
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        var payload = new ExecShellCommandPayload
        {
            Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            Command = OperatingSystem.IsWindows()
                ? $"[Console]::Out.WriteLine('stdout-marker'); [Console]::Error.WriteLine('stderr-marker'); exit {exitCode}"
                : $"printf 'stdout-marker\n'; printf 'stderr-marker\n' >&2; exit {exitCode}",
            TimeoutSeconds = 5
        };
        manager.EnqueueGatewayCommand("exit-status", TaskKinds.ExecShellCommand, JsonSerializer.Serialize(payload), 1, 0).Should().BeTrue();
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        result.Status.Should().Be(expected);
        result.ExitCode.Should().Be(exitCode);
        using var document = JsonDocument.Parse(result.Result!);
        document.RootElement.GetProperty("stdout").EnumerateArray().Select(line => line.GetString()).Should().Contain("stdout-marker");
        document.RootElement.GetProperty("stderr").EnumerateArray().Select(line => line.GetString()).Should().Contain("stderr-marker");
    }
}
