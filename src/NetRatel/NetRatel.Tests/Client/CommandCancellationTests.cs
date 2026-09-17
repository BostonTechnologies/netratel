using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Client.Service.Shells;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class CommandCancellationTests
{
    [Fact]
    public async Task Queued_cancellation_is_registered_before_execution_and_repeated_requests_are_idempotent()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<(string Id, string Status)>();
        using var manager = new ClientTaskManager(false, async (id, status, _, _) =>
        {
            statuses.Enqueue((id, status));
            if (id == "first" && status == "started")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            if (id == "queued" && status == "cancelled") queuedCancelled.TrySetResult();
        }, _ => { });
        manager.Start(1, 0);
        try
        {
            manager.EnqueueGatewayCommand("first", TaskKinds.OsInfo, null, 1, 0).Should().BeTrue();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.EnqueueGatewayCommand("queued", TaskKinds.OsInfo, null, 1, 0).Should().BeTrue();
            manager.CancelGatewayCommand("queued").Should().BeTrue();
            manager.CancelGatewayCommand("queued").Should().BeTrue();
            manager.CancelGatewayCommand("unknown").Should().BeFalse();
            releaseFirst.TrySetResult();
            await queuedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            statuses.Where(item => item.Id == "queued").Select(item => item.Status).Should().Equal("cancelled");
        }
        finally { releaseFirst.TrySetResult(); }
    }

    [Fact]
    public async Task Running_shell_cancellation_publishes_one_terminal_acknowledgement_with_a_cancelled_execution_token()
    {
        await using var process = new ShellProcessFixture();
        var terminal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        using var manager = new ClientTaskManager(false, (_, status, _, _) =>
        {
            statuses.Enqueue(status);
            if (status is "cancelled" or "completed" or "failed") terminal.TrySetResult(status);
            return Task.CompletedTask;
        }, _ => { });
        manager.Start(1, 0);
        manager.EnqueueGatewayCommand("running", TaskKinds.ExecShellCommand, JsonSerializer.Serialize(process.Payload()), 1, 0).Should().BeTrue();
        await process.WaitUntilRunningAsync();
        manager.CancelGatewayCommand("running").Should().BeTrue();
        (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("cancelled");
        statuses.Should().Equal("started", "cancelled");
        await process.AssertChildExitedAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shell_cancellation_and_timeout_terminate_the_process_tree_and_finish_redirected_streams(bool cancel)
    {
        await using var process = new ShellProcessFixture();
        using var cancellation = new CancellationTokenSource();
        var runner = new ExternalShellRunner();
        var running = runner.RunShellCommandAsync(process.Payload(timeoutSeconds: cancel ? 60 : 3), cancellation.Token);
        try
        {
            await process.WaitUntilRunningAsync();
            if (cancel) cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
            result.ExitCode.Should().Be(-1);
            result.Output.Should().Contain("before-output");
            result.Error.Should().Contain("before-error");
            result.Error.Should().Contain(cancel ? "Cancelled" : "Timed out after 3s");
            await process.AssertChildExitedAsync();
        }
        finally
        {
            cancellation.Cancel();
            await process.StopChildAsync();
            // Even an assertion failure must not leave the test's shell running.
            await running.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task Already_cancelled_shell_request_does_not_start_a_process()
    {
        await using var process = new ShellProcessFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ExternalShellRunner();
        await FluentActions.Awaiting(() => runner.RunShellCommandAsync(process.Payload(), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        process.ReadyExists.Should().BeFalse();
    }

    private sealed class ShellProcessFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netratel-command-cancel-{Guid.NewGuid():N}");
        private readonly FileSystemWatcher _watcher;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process? _child;
        private string ReadyPath => Path.Combine(_directory, "ready");
        public bool ReadyExists => File.Exists(ReadyPath);

        public ShellProcessFixture()
        {
            Directory.CreateDirectory(_directory);
            _watcher = new FileSystemWatcher(_directory);
            _watcher.Created += (_, _) => SignalReady();
            _watcher.Renamed += (_, _) => SignalReady();
            _watcher.EnableRaisingEvents = true;
        }

        private void SignalReady()
        {
            if (ReadyExists) _ready.TrySetResult();
        }

        public ExecShellCommandPayload Payload(int timeoutSeconds = 60) => new()
        {
            Preferred = OperatingSystem.IsWindows() ? ShellExecutor.WindowsPowerShell : ShellExecutor.Bash,
            WorkingDirectory = _directory,
            TimeoutSeconds = timeoutSeconds,
            Command = OperatingSystem.IsWindows()
                ? "[Console]::Out.WriteLine('before-output'); [Console]::Error.WriteLine('before-error'); $child = Start-Process ping.exe -ArgumentList '-n 60 127.0.0.1' -NoNewWindow -PassThru; [IO.File]::WriteAllText((Join-Path (Get-Location) 'ready.tmp'), [string]$child.Id); Move-Item ready.tmp ready; Wait-Process -Id $child.Id"
                : "printf 'before-output\\n'; printf 'before-error\\n' >&2; sleep 60 & child=$!; printf '%s' \"$child\" > ready.tmp; mv ready.tmp ready; wait \"$child\""
        };

        public async Task WaitUntilRunningAsync()
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ReadyPath)));
        }

        public async Task AssertChildExitedAsync()
        {
            _child.Should().NotBeNull();
            await _child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            _child.HasExited.Should().BeTrue();
        }

        public async Task StopChildAsync()
        {
            if (_child is null && ReadyExists)
            {
                var pid = int.Parse(await File.ReadAllTextAsync(ReadyPath));
                try { _child = Process.GetProcessById(pid); }
                catch (ArgumentException) { return; }
            }
            if (_child is not null && !_child.HasExited)
            {
                try { _child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_child.HasExited) { return; }
                await _child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopChildAsync();
            _child?.Dispose();
            _watcher.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
