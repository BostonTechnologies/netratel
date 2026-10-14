using System.Text.Json;
using System.Collections.Concurrent;
using NetRatel.Client.Service.Shells;
using AwesomeAssertions;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class CommandExitStatusTests
{
    [Fact]
    public async Task Gateway_shell_command_accepts_camel_case_wire_payload()
    {
        var completion = new TaskCompletionSource<(string Status, string? Result, int? ExitCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager((_, status, result, code) =>
        {
            if (status is "completed" or "failed" or "cancelled") completion.TrySetResult((status, result, code));
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);

        const string payload = """{"preferred":3,"command":"printf camel-case-marker","timeoutSeconds":5}""";
        manager.EnqueueGatewayCommand("camel-case-payload", TaskKinds.ExecShellCommand, payload, 1, 0).Should().BeTrue();

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        result.Status.Should().Be("completed");
        result.ExitCode.Should().Be(0);
        using var document = JsonDocument.Parse(result.Result!);
        document.RootElement.GetProperty("stdout").EnumerateArray().Select(line => line.GetString()).Should().Contain("camel-case-marker");
    }

    [Theory]
    [InlineData(0, "completed")]
    [InlineData(7, "failed")]
    public async Task Gateway_shell_status_uses_exit_code_and_preserves_both_output_streams(int exitCode, string expected)
    {
        var completion = new TaskCompletionSource<(string Status, string? Result, int? ExitCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager((_, status, result, code) =>
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_library_or_failed_process_start_has_no_started_or_exit_receipt(bool missingInterpreter)
    {
        var statuses = new ConcurrentQueue<string>();
        var completion = new TaskCompletionSource<(string? Result, int? ExitCode)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ExternalShellRunner(null, _ => Path.Combine(Path.GetTempPath(), "netratel-missing-shell", "missing"));
        using var manager = new ClientTaskManager(runner, (_, status, result, code) =>
        {
            statuses.Enqueue(status);
            if (status == "failed") completion.TrySetResult((result, code));
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        manager.EnqueueGatewayCommand("no-start", TaskKinds.ExecLibraryScript, JsonSerializer.Serialize(new ExecLibraryScriptPayload
        {
            ScriptId = 4,
            ScriptType = ScriptType.Bash,
            Preferred = ShellExecutor.Bash,
            ScriptContent = missingInterpreter ? "printf must-not-run" : null
        }), 1, 0).Should().BeTrue();
        var terminal = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        statuses.Should().Equal("failed");
        terminal.ExitCode.Should().BeNull();
        using var document = JsonDocument.Parse(terminal.Result!);
        document.RootElement.GetProperty("exitCode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Live_output_is_redacted_and_ordered_before_cancelled_terminal_snapshot()
    {
        var updates = new ConcurrentQueue<(string Status, string? Result, int? ExitCode)>();
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new ClientTaskManager((_, status, result, code) =>
        {
            updates.Enqueue((status, result, code));
            if (status == "progress") live.TrySetResult();
            if (status is "completed" or "failed" or "cancelled") terminal.TrySetResult();
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        manager.EnqueueGatewayCommand("live-output", TaskKinds.ExecShellCommand, JsonSerializer.Serialize(new ExecShellCommandPayload
        {
            Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            Command = OperatingSystem.IsWindows()
                ? "[Console]::Out.WriteLine('Bearer private-test-token'); [Console]::Error.WriteLine('stderr-marker'); Start-Sleep -Seconds 30"
                : "printf 'Bearer private-test-token\\n'; printf 'stderr-marker\\n' >&2; sleep 30",
            TimeoutSeconds = 10
        }), 1, 0).Should().BeTrue();
        await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
        updates.Select(update => update.Status).Should().NotContain("completed").And.NotContain("failed").And.NotContain("cancelled");
        manager.CancelGatewayCommand("live-output").Should().BeTrue();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        updates.First().Status.Should().Be("started");
        var output = updates.Where(update => update.Status == "progress").ToArray();
        output.Should().NotBeEmpty();
        var sequences = output.Select(update =>
        {
            update.Result.Should().NotContain("private-test-token");
            using var document = JsonDocument.Parse(update.Result!);
            return document.RootElement.GetProperty("outputSequence").GetInt64();
        });
        sequences.Should().Equal(Enumerable.Range(1, output.Length).Select(number => (long)number));
        var final = updates.Last();
        final.Status.Should().Be("cancelled");
        final.ExitCode.Should().BeNull();
        using var snapshot = JsonDocument.Parse(final.Result!);
        snapshot.RootElement.GetProperty("outputStreamed").GetBoolean().Should().BeTrue();
        snapshot.RootElement.GetProperty("cancelled").GetBoolean().Should().BeTrue();
        snapshot.RootElement.GetProperty("stdout").EnumerateArray().Select(line => line.GetString()).Should().Contain("Bearer [REDACTED]");
    }

    [Fact]
    public async Task Noisy_process_retains_bounded_output_and_reports_truncation()
    {
        var runner = new ExternalShellRunner();
        var result = await runner.RunShellCommandAsync(new ExecShellCommandPayload
        {
            Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            Command = OperatingSystem.IsWindows()
                ? "1..2000 | ForEach-Object { [Console]::Out.WriteLine(('x' * 100)) }"
                : "for i in {1..2000}; do printf '%0100d\\n' 0; done",
            TimeoutSeconds = 5
        }, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputTruncated.Should().BeTrue();
        result.Output.Count.Should().BeLessThanOrEqualTo(1024);
        result.Output.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line)).Should().BeLessThanOrEqualTo(48 * 1024);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_selected_working_directory_or_timeout_fails_before_process_start(bool invalidDirectory)
    {
        var starts = 0;
        var runner = new ExternalShellRunner();
        var observer = new ExternalShellRunner.ExecutionObserver(
            () => { starts++; return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        var payload = new ExecShellCommandPayload
        {
            Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            Command = "must-not-run",
            WorkingDirectory = invalidDirectory ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) : null,
            TimeoutSeconds = invalidDirectory ? 5 : 0
        };
        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunShellCommandAsync(payload, CancellationToken.None, observer));
        starts.Should().Be(0);
    }

}
