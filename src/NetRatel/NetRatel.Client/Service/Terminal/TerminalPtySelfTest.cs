using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Terminal;

internal static class TerminalPtySelfTest
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(bool forceNative, bool forcePython, CancellationToken ct)
    {
        var availability = UnixPtyHelperTerminalHost.GetAvailability();
        Write(new TerminalAvailabilityDiagnostic(
            "availability", availability.AppBaseDirectory, availability.ProcessPath,
            availability.PythonPath, availability.HelperPath, availability.IsAvailable),
            TerminalJsonContext.Default.TerminalAvailabilityDiagnostic);

        var factory = new TerminalHostFactory(new TerminalHostOptions
        {
            BackendPreference = TerminalBackendPreference.UnixPty,
            EnableNativeUnixPty = true
        });
        var candidates = factory.CreateCandidates(new Terminal.TerminalOpenPayload
        {
            ShellType = "bash",
            Cols = 90,
            Rows = 24
        });
        Write(new TerminalCandidatesDiagnostic("candidates", candidates.Select(candidate =>
            new TerminalCandidateDiagnostic(candidate.Backend, candidate.GetType().Name)).ToArray()),
            TerminalJsonContext.Default.TerminalCandidatesDiagnostic);

        var host = forceNative
            ? NativeUnixPtyTerminalHost.TryCreate(new TerminalHostContext(
                "bash",
                "/bin/bash",
                "-l",
                null,
                90,
                24,
                new TerminalHostOptions()))
            : forcePython
                ? candidates.FirstOrDefault(candidate => string.Equals(candidate.Backend, "unix-pty-python", StringComparison.OrdinalIgnoreCase))
                : candidates.FirstOrDefault();
        if (host is null)
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
            Write(new TerminalSelectionFailureDiagnostic("failed", forceNative, forcePython, "no requested Unix PTY backend was selected"),
                TerminalJsonContext.Default.TerminalSelectionFailureDiagnostic);
            return 2;
        }

        var expectedBackend = forcePython ? "unix-pty-python" : "unix-pty-native";
        if (!string.Equals(host.Backend, expectedBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
            Write(new TerminalBackendFailureDiagnostic("failed", forceNative, forcePython, expectedBackend, host.Backend),
                TerminalJsonContext.Default.TerminalBackendFailureDiagnostic);
            return 2;
        }

        var output = new StringBuilder();
        var outputLock = new object();
        var exited = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            host.Exited += reason => exited.TrySetResult(reason);
            await host.StartAsync(chunk =>
            {
                lock (outputLock)
                {
                    output.Append(chunk.Data);
                }
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
            Write(new TerminalStartedDiagnostic("started", host.Backend, forceNative, forcePython, string.Equals(host.Backend, "unix-pty-native", StringComparison.OrdinalIgnoreCase)),
                TerminalJsonContext.Default.TerminalStartedDiagnostic);

            await host.WriteInputAsync("printf 'NetRatel_PTY_SELF_TEST\\n'; stty size\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("NetRatel_PTY_SELF_TEST", output, outputLock, ct).ConfigureAwait(false);
            await WaitForOutputAsync("24 90", output, outputLock, ct).ConfigureAwait(false);
            Write(new TerminalStageDiagnostic("input-output", "passed"), TerminalJsonContext.Default.TerminalStageDiagnostic);

            var resize = await host.ResizeAsync(77, 17, ct).ConfigureAwait(false);
            Write(new TerminalResizeDiagnostic("resize", resize.Result, resize.RequestedCols, resize.RequestedRows,
                resize.AppliedCols, resize.AppliedRows, resize.Error), TerminalJsonContext.Default.TerminalResizeDiagnostic);
            if (!string.Equals(resize.Result, "applied", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            await host.WriteInputAsync("stty size\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("17 77", output, outputLock, ct).ConfigureAwait(false);
            await host.WriteInputAsync("printf 'NetRatel_PTY_LONG_RUNNING_READY\\n'; sleep 30\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("NetRatel_PTY_LONG_RUNNING_READY", output, outputLock, ct).ConfigureAwait(false);
            await host.WriteInputAsync("\u0003printf 'NetRatel_PTY_CTRL_C_OK\\n'\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("NetRatel_PTY_CTRL_C_OK", output, outputLock, ct).ConfigureAwait(false);
            await host.WriteInputAsync("for i in $(seq 1 512); do printf 'NetRatel_PTY_BURST_%04d\\n' \"$i\"; done\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("NetRatel_PTY_BURST_0512", output, outputLock, ct).ConfigureAwait(false);
            await host.WriteInputAsync("exit\n", ct).ConfigureAwait(false);
            await exited.Task.WaitAsync(StepTimeout, ct).ConfigureAwait(false);
            if (!host.HasExited)
            {
                throw new InvalidOperationException("The PTY child was not reaped after shell exit.");
            }
            Write(new TerminalPassedDiagnostic("passed", true, Snapshot(output, outputLock)), TerminalJsonContext.Default.TerminalPassedDiagnostic);
            return 0;
        }
        catch (Exception ex)
        {
            Write(new TerminalFailureDiagnostic("failed", ex.ToString(), Snapshot(output, outputLock)),
                TerminalJsonContext.Default.TerminalFailureDiagnostic);
            return 1;
        }
        finally
        {
            try { await host.RequestCloseAsync("PTY self-test complete.", CancellationToken.None).ConfigureAwait(false); } catch { }
            host.Dispose();
            foreach (var candidate in candidates.Skip(1))
            {
                candidate.Dispose();
            }
        }
    }

    private static async Task WaitForOutputAsync(
        string expected,
        StringBuilder output,
        object outputLock,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + StepTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            lock (outputLock)
            {
                if (output.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for terminal output '{expected}'.");
    }

    private static string Snapshot(StringBuilder output, object outputLock)
    {
        lock (outputLock)
        {
            var value = output.ToString();
            return value.Length <= 4000 ? value : value[^4000..];
        }
    }

    private static void Write<T>(T value, JsonTypeInfo<T> typeInfo) =>
        Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));
}
