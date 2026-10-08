using System;
using System.IO;
using System.Text.Json;

namespace NetRatel.Client.Service.Auth;

internal sealed record OperationalRecoveryState(int Version, DateTimeOffset RecordedAtUtc, bool HasOutage,
    double OutageElapsedSeconds, int FailureCount, long AttemptCount, DateTimeOffset? NextAttemptUtc,
    DateTimeOffset? RetryAfterUntilUtc, Guid? ConsumedUpdateAttemptId);

/// <summary>One small nonsecret advisory record; loss never blocks ordinary operational recovery.</summary>
public sealed class OperationalRecoveryStateStore(string path, Action<string>? log = null)
{
    private const int MaximumBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool _reportedFailure;

    internal static OperationalRecoveryStateStore ForInstallation(string? stateDirectory, Action<string>? log = null)
    {
        var directory = string.IsNullOrWhiteSpace(stateDirectory)
            ? Environment.GetEnvironmentVariable("NetRatel_CLIENT_STATE_DIR") : stateDirectory;
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetDirectoryName(AgentCredentialStore.DefaultPath());
        return new OperationalRecoveryStateStore(Path.Combine(directory!, "operational-recovery.json"), log);
    }

    internal OperationalRecoveryState? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumBytes) return Invalid();
            var state = JsonSerializer.Deserialize<OperationalRecoveryState>(stream, JsonOptions);
            if (state is null || state.Version != 1 || !double.IsFinite(state.OutageElapsedSeconds) ||
                state.OutageElapsedSeconds is < 0 or > 604800 || state.FailureCount is < 0 or > 5 ||
                state.AttemptCount < 0 ||
                state.RecordedAtUtc == default)
                return Invalid();
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ReportFailure("Could not read operational recovery advisory state", exception);
            return null;
        }
    }

    internal bool Write(OperationalRecoveryState state)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            if (bytes.Length > MaximumBytes) return false;
            temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportFailure("Could not write operational recovery advisory state", exception);
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { ReportFailure("Could not remove operational recovery temporary file", exception); }
            }
        }
    }

    private OperationalRecoveryState? Invalid()
    {
        ReportFailure("Ignored invalid operational recovery advisory state", null);
        return null;
    }

    private void ReportFailure(string message, Exception? exception)
    {
        if (_reportedFailure) return;
        _reportedFailure = true;
        log?.Invoke($"[OperationalRecovery] {message}{(exception is null ? "." : $": {exception.GetType().Name}.")}");
    }
}
