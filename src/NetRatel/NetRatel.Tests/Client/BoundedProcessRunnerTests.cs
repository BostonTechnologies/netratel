using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NetRatel.Client.Service;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class BoundedProcessRunnerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task CompletedProcess_PreservesExitCodeAndBothDiagnostics(int exitCode)
    {
        var startInfo = Shell($"printf 'ordinary output'; printf 'ordinary error' >&2; exit {exitCode}",
            $"[Console]::Out.Write('ordinary output'); [Console]::Error.Write('ordinary error'); exit {exitCode}");

        var result = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(15), "run transient diagnostic child");

        result.ExitCode.Should().Be(exitCode);
        result.Output.Should().Be("ordinary output");
        result.Error.Should().Be("ordinary error");
    }

    [Fact]
    public async Task SaturatedOutputPipes_AreDrainedConcurrentlyWithBoundedCapture()
    {
        var outputLine = new string('o', 128);
        var errorLine = new string('e', 128);
        var startInfo = Shell(
            $"i=0; while [ \"$i\" -lt 4000 ]; do printf '%s\\n' '{outputLine}'; printf '%s\\n' '{errorLine}' >&2; i=$((i + 1)); done; exit 7",
            "for ($i = 0; $i -lt 4000; $i++) { [Console]::Out.WriteLine(('o' * 128)); [Console]::Error.WriteLine(('e' * 128)) }; exit 7");

        var result = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(20), "saturate both transient child pipes");

        result.ExitCode.Should().Be(7);
        result.Output.Should().StartWith(outputLine).And.EndWith(BoundedProcessRunner.TruncationMarker);
        result.Error.Should().StartWith(errorLine).And.EndWith(BoundedProcessRunner.TruncationMarker);
        result.Output.Length.Should().Be(BoundedProcessRunner.MaxCapturedOutputCharacters + BoundedProcessRunner.TruncationMarker.Length);
        result.Error.Length.Should().Be(BoundedProcessRunner.MaxCapturedOutputCharacters + BoundedProcessRunner.TruncationMarker.Length);
    }

    [Fact]
    public async Task ChildWithoutOutput_TimeoutTerminatesItWithinTheBound()
    {
        var pidFile = NewPidFile();
        try
        {
            var startInfo = SleepingChild(pidFile);
            var elapsed = Stopwatch.StartNew();
            Func<Task> run = () => BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(3), "wait for silent transient child");

            await run.Should().ThrowAsync<TimeoutException>().WithMessage("*wait for silent transient child*");

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7));
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            await AssertStoppedAsync([pid]);
        }
        finally
        {
            CleanupRecordedProcesses(pidFile);
        }
    }

    [Fact]
    public async Task CallerCancellation_TerminatesOwnedChildWithinTheBound()
    {
        var pidFile = NewPidFile();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var running = BoundedProcessRunner.RunAsync(SleepingChild(pidFile), TimeSpan.FromSeconds(20),
                "cancel transient child", cancellation.Token);
            var pid = (await WaitForPidsAsync(pidFile, running))[0];
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            Func<Task> run = () => running;

            var thrown = await run.Should().ThrowAsync<OperationCanceledException>();

            thrown.Which.CancellationToken.Should().Be(cancellation.Token);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            await AssertStoppedAsync([pid]);
        }
        finally
        {
            cancellation.Cancel();
            CleanupRecordedProcesses(pidFile);
        }
    }

    [Fact]
    public async Task Timeout_TerminatesLiveParentAndItsDescendant()
    {
        var pidFile = NewPidFile();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var startInfo = ProcessTreeProbe(pidFile);
            var elapsed = Stopwatch.StartNew();
            var running = BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(3), "terminate transient child tree", cancellation.Token);
            var pids = await WaitForPidsAsync(pidFile, running);
            pids.Should().HaveCount(2);
            pids.Should().OnlyHaveUniqueItems();
            pids.Should().OnlyContain(pid => IsAlive(pid), "the fixture must publish a live parent and its live descendant before timeout");
            Func<Task> run = () => running;

            await run.Should().ThrowAsync<TimeoutException>();

            await AssertStoppedAsync(pids);
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7));
        }
        finally
        {
            cancellation.Cancel();
            CleanupRecordedProcesses(pidFile);
        }
    }

    [Fact]
    public async Task ExitedParentWithInheritedPipe_StillHasABoundedDrainDeadline()
    {
        // Unix shells allow a transient descendant to inherit the redirected pipe.
        // It is already orphaned when timeout occurs, so the test owns its cleanup.
        if (OperatingSystem.IsWindows())
            Assert.Skip("Inherited redirected-pipe setup requires a Unix shell; other runner tests cover native Windows child processes.");
        var pidFile = NewPidFile();
        try
        {
            var startInfo = Shell($"sleep 30 & printf '%s\\n' \"$!\" > '{QuotePath(pidFile + ".tmp")}' ; mv '{QuotePath(pidFile + ".tmp")}' '{QuotePath(pidFile)}'; exit 0", "");
            var elapsed = Stopwatch.StartNew();
            Func<Task> run = () => BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(1), "drain inherited transient pipe");

            await run.Should().ThrowAsync<TimeoutException>().WithMessage("*drain inherited transient pipe*");

            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            CleanupRecordedProcesses(pidFile);
        }
    }

    private static ProcessStartInfo SleepingChild(string pidFile) => Shell(
        $"printf '%s\\n' \"$$\" > '{QuotePath(pidFile + ".tmp")}'; mv '{QuotePath(pidFile + ".tmp")}' '{QuotePath(pidFile)}'; exec sleep 30",
        $"[IO.File]::WriteAllText('{QuotePath(pidFile + ".tmp")}', [string]$PID); [IO.File]::Move('{QuotePath(pidFile + ".tmp")}', '{QuotePath(pidFile)}'); Start-Sleep -Seconds 30");

    private static ProcessStartInfo ProcessTreeProbe(string pidFile)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository.Parent is not null &&
               !File.Exists(Path.Combine(repository.FullName, "tools", "NetRatel.ClientArtifactCrashProbe",
                   "NetRatel.ClientArtifactCrashProbe.csproj")))
            repository = repository.Parent;
        var configuration = typeof(BoundedProcessRunnerTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The test assembly has no build configuration.");
        var apphost = Path.Combine(repository.FullName, "tools", "NetRatel.ClientArtifactCrashProbe",
            "bin", configuration, "net10.0",
            "NetRatel.ClientArtifactCrashProbe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(apphost), $"Process-tree probe is missing: {apphost}");
        var startInfo = new ProcessStartInfo(apphost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--bounded-process-tree-parent");
        startInfo.ArgumentList.Add(pidFile);
        return startInfo;
    }

    private static ProcessStartInfo Shell(string unixCommand, string windowsCommand)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? WindowsPowerShellPath() : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windowsCommand })
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(unixCommand);
        }
        return startInfo;
    }

    private static string WindowsPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string NewPidFile() => Path.Combine(Path.GetTempPath(), $"netratel-bounded-process-{Guid.NewGuid():N}.pid");

    private static string QuotePath(string path) => OperatingSystem.IsWindows()
        ? path.Replace("'", "''", StringComparison.Ordinal)
        : path.Replace("'", "'\\''", StringComparison.Ordinal);

    private static async Task<int[]> WaitForPidsAsync(string pidFile, Task<BoundedProcessResult> running)
    {
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(pidFile)!, Path.GetFileName(pidFile))
        {
            NotifyFilter = NotifyFilters.FileName
        };
        // Children rename a fully written temporary file, so publication is readiness.
        watcher.Created += (_, _) => published.TrySetResult();
        watcher.Renamed += (_, _) => published.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (!File.Exists(pidFile))
        {
            var completed = await Task.WhenAny(published.Task, running).WaitAsync(TimeSpan.FromSeconds(10));
            if (completed == running && !File.Exists(pidFile))
            {
                BoundedProcessResult result;
                try
                {
                    result = await running;
                }
                catch (Exception failure)
                {
                    throw new InvalidOperationException("Transient child failed before publishing its PID readiness file.", failure);
                }
                throw new InvalidOperationException($"Transient child exited before publishing its PID readiness file. ExitCode={result.ExitCode}; stderr={result.Error}; stdout={result.Output}");
            }
        }
        var value = await File.ReadAllTextAsync(pidFile);
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
    }

    private static bool IsAlive(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var commandEnd = stat.LastIndexOf(')');
                if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                    throw new InvalidDataException($"No native process state was available for test-owned PID {pid}.");
                // Orphaned descendants may remain zombies under a container PID 1
                // that does not reap them. They have exited and cannot execute or
                // retain pipes, although cross-process .NET waits still see a PID.
                return stat[commandEnd + 2] is not ('Z' or 'X' or 'x');
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task AssertStoppedAsync(int[] pids)
    {
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        foreach (var pid in pids)
        {
            if (!IsAlive(pid))
                continue;
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                await process.WaitForExitAsync(reap.Token);
            }
            catch (ArgumentException)
            {
                // A missing PID already satisfies the termination assertion.
                continue;
            }
            catch (OperationCanceledException)
            {
                // Preserve failure for a live child; a native Linux zombie has
                // already terminated even if the cross-process wait times out.
                if (IsAlive(pid))
                    throw;
            }
            finally
            {
                process?.Dispose();
            }
            IsAlive(pid).Should().BeFalse("the runner must terminate its owned child tree");
        }
    }

    private static void CleanupRecordedProcesses(string pidFile)
    {
        File.Delete(pidFile + ".tmp");
        if (!File.Exists(pidFile))
            return;
        foreach (var value in File.ReadAllText(pidFile).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(value, out var pid) || !IsAlive(pid))
                continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(1000) && IsAlive(pid))
                    throw new TimeoutException($"Test-owned child {pid} did not exit within the cleanup bound.");
                IsAlive(pid).Should().BeFalse("cleanup must leave no live test-owned process");
            }
            catch (ArgumentException)
            {
                // The test-owned child exited between lookup and cleanup.
                continue;
            }
            catch (InvalidOperationException)
            {
                // Preserve genuine cleanup failures; only an exited child is benign.
                if (IsAlive(pid))
                    throw;
            }
        }
        File.Delete(pidFile);
    }
}
