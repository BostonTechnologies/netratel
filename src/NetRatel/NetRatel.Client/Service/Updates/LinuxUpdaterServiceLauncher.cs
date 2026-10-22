using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Updates;

internal sealed class NativeUpdaterLaunchException(string code, bool retrySafe) : IOException(code)
{
    internal string Code { get; } = code;
    internal bool RetrySafe { get; } = retrySafe;
}

internal static class LinuxUpdaterServiceLauncher
{
    internal static async Task StartAsync(string serviceName, string stateDirectory, Guid attemptId,
        CancellationToken cancellationToken = default,
        Func<string[], CancellationToken, Task<(int ExitCode, string Output)>>? run = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || !serviceName.EndsWith(".service", StringComparison.Ordinal) ||
            serviceName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '@')) ||
            serviceName[0] == '-')
            throw new NativeUpdaterLaunchException("linux_updater_unit_invalid", retrySafe: false);

        // Waiting for a oneshot updater to finish couples this child to the service
        // that the updater must stop. Wait only for systemd to acknowledge the job.
        run ??= RunAsync;
        var start = await run(["start", "--no-block", "--no-ask-password", serviceName], cancellationToken)
            .ConfigureAwait(false);
        if (start.ExitCode == 0) return;

        var state = await run(["show", "--property=ActiveState,Job", serviceName], cancellationToken)
            .ConfigureAwait(false);
        var fields = state.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var idle = state.ExitCode == 0 && fields.Any(value => value is "ActiveState=inactive" or "ActiveState=failed") &&
            fields.Contains("Job=", StringComparer.Ordinal);
        // A nonzero command alone is not proof that no job was queued. Do not
        // relaunch if systemd or the native state cannot exclude an in-flight run.
        var retrySafe = idle && !HasAttemptState(stateDirectory, attemptId);
        throw new NativeUpdaterLaunchException("linux_updater_start_rejected", retrySafe);
    }

    private static bool HasAttemptState(string directory, Guid attemptId)
    {
        foreach (var name in new[] { "state.json", "result.json" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                if (!ProtectedUpdateState.IsProtectedFile(path) || new FileInfo(path).Length > 64 * 1024) return true;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("attemptId", out var value) ||
                    !value.TryGetGuid(out var recordedAttempt) || recordedAttempt == attemptId) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process? command;
        try { command = Process.Start(info); }
        catch (Win32Exception)
        {
            throw new NativeUpdaterLaunchException("linux_updater_command_unavailable", retrySafe: arguments[0] == "start");
        }
        using (command)
        {
            if (command is null)
                throw new NativeUpdaterLaunchException("linux_updater_command_unavailable", retrySafe: arguments[0] == "start");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var output = command.StandardOutput.ReadToEndAsync(stopping.Token);
            var errors = command.StandardError.ReadToEndAsync(stopping.Token);
            try
            {
                await command.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return (command.ExitCode, await output.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // Stop only the command client. A job already accepted by systemd
                // owns its own lifecycle and must not be cancelled or re-launched.
                if (!command.HasExited) command.Kill();
                await command.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new NativeUpdaterLaunchException("linux_updater_acknowledgement_timeout", retrySafe: false);
            }
        }
    }
}
