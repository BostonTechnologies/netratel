using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Terminal;

internal static class TerminalPtySelfTest
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(bool forceNative, bool forcePython, CancellationToken ct)
    {
        var availability = UnixPtyHelperTerminalHost.GetAvailability();
        Write(new
        {
            stage = "availability",
            availability.AppBaseDirectory,
            availability.ProcessPath,
            availability.PythonPath,
            availability.HelperPath,
            availability.IsAvailable
        });

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
        Write(new
        {
            stage = "candidates",
            values = candidates.Select(candidate => new
            {
                candidate.Backend,
                type = candidate.GetType().Name
            })
        });

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
            Write(new { stage = "failed", forceNative, forcePython, error = "no requested Unix PTY backend was selected" });
            return 2;
        }

        var expectedBackend = forcePython ? "unix-pty-python" : "unix-pty-native";
        if (!string.Equals(host.Backend, expectedBackend, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
            Write(new { stage = "failed", forceNative, forcePython, expectedBackend, actualBackend = host.Backend });
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
            Write(new { stage = "started", host.Backend, forceNative, forcePython, native = string.Equals(host.Backend, "unix-pty-native", StringComparison.OrdinalIgnoreCase) });

            await host.WriteInputAsync("printf 'NetRatel_PTY_SELF_TEST\\n'; stty size\n", ct).ConfigureAwait(false);
            await WaitForOutputAsync("NetRatel_PTY_SELF_TEST", output, outputLock, ct).ConfigureAwait(false);
            await WaitForOutputAsync("24 90", output, outputLock, ct).ConfigureAwait(false);
            Write(new { stage = "input-output", result = "passed" });

            var resize = await host.ResizeAsync(77, 17, ct).ConfigureAwait(false);
            Write(new
            {
                stage = "resize",
                resize.Result,
                resize.RequestedCols,
                resize.RequestedRows,
                resize.AppliedCols,
                resize.AppliedRows,
                resize.Error
            });
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
            Write(new { stage = "passed", reaped = true, output = Snapshot(output, outputLock) });
            return 0;
        }
        catch (Exception ex)
        {
            Write(new
            {
                stage = "failed",
                error = ex.ToString(),
                output = Snapshot(output, outputLock)
            });
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

    private static void Write(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
