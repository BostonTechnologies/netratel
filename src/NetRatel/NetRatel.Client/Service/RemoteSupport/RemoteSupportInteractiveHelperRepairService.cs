using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed record RemoteSupportHelperRepairResult(
    string StatusCode,
    string Message,
    bool Attempted,
    bool TerminateAttempted,
    bool RelaunchAttempted,
    bool VersionRestored,
    bool ReconnectRequired,
    string? Error = null,
    int? StalePid = null,
    int? StaleSessionId = null,
    string? ConnectedVersion = null,
    string? ServiceVersion = null,
    string? LauncherTarget = null);

[SupportedOSPlatform("windows")]
internal sealed class RemoteSupportInteractiveHelperRepairService
{
    public const int MaxAttemptsPerSession = 1;
    public const int TimeoutSeconds = 15;
    public const int PrepareTimeoutSeconds = 20;
    public const bool OnlyIfPidAndSessionMatch = true;
    public const bool NeverTerminatesServiceProcess = true;
    public const bool NeverTerminatesConsoleProvider = true;

    private readonly RemoteDesktopUserHelperPipeHost? _helperPipeHost;
    private readonly RemoteSupportConsoleProviderPipeHost? _consoleProviderPipeHost;
    private readonly string _serviceVersion;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public RemoteSupportInteractiveHelperRepairService(
        RemoteDesktopUserHelperPipeHost? helperPipeHost,
        RemoteSupportConsoleProviderPipeHost? consoleProviderPipeHost,
        string serviceVersion)
    {
        _helperPipeHost = helperPipeHost;
        _consoleProviderPipeHost = consoleProviderPipeHost;
        _serviceVersion = serviceVersion;
    }

    public Task<RemoteSupportHelperRepairResult> TryLaunchAsync(string sessionId, CancellationToken ct)
    {
        var activeSessionId = new RemoteDesktopUserHelperTask().GetActiveConsoleSessionId();
        return activeSessionId == uint.MaxValue
            ? Task.FromResult(Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperLaunchTimeout,
                "No active Windows session is available for helper launch.",
                false,
                false,
                false,
                false,
                true,
                ServiceVersion: _serviceVersion)))
            : TryLaunchAsync(sessionId, unchecked((int)activeSessionId), ct);
    }

    public async Task<RemoteSupportHelperRepairResult> TryLaunchAsync(
        string sessionId,
        int targetWindowsSessionId,
        CancellationToken ct)
    {
        if (_attempts.AddOrUpdate($"launch:{sessionId}:{targetWindowsSessionId}", 1, (_, current) => current + 1) > MaxAttemptsPerSession)
        {
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperLaunchTimeout,
                "Interactive helper launch was already attempted for this Remote Support session.",
                false,
                false,
                false,
                false,
                true,
                ServiceVersion: _serviceVersion));
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.InteractiveHelperRepairSupported = true;
            state.InteractiveHelperRepairInProgress = true;
            state.InteractiveHelperRepairLastAttemptAt = attemptedAt;
            state.InteractiveHelperRepairLastResult = RemoteSupportStatusCodes.InteractiveHelperLaunchStarted;
            state.InteractiveHelperRepairLastError = null;
            state.InteractiveHelperServiceVersion = _serviceVersion;
            state.InteractiveHelperTerminateAttempted = false;
            state.InteractiveHelperRelaunchAttempted = false;
        });

        try
        {
            var task = new RemoteDesktopUserHelperTask();
            task.EnsureLauncherAndRunKey();
            task.TryRegisterScheduledTask();
            var launcherTarget = RemoteDesktopUserHelperTask.GetLauncherPath();
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.InteractiveHelperLauncherTarget = launcherTarget;
                state.InteractiveHelperLauncherTargetVersion = _serviceVersion;
                state.InteractiveHelperRelaunchAttempted = true;
                state.InteractiveHelperRelaunchResult = RemoteSupportStatusCodes.InteractiveHelperLaunchRequested;
            });

            task.StartForSession(checked((uint)targetWindowsSessionId));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(PrepareTimeoutSeconds));
            var helper = _helperPipeHost is null
                ? null
                : await _helperPipeHost.WaitForHelperAsync(targetWindowsSessionId, TimeSpan.FromSeconds(PrepareTimeoutSeconds), timeoutCts.Token).ConfigureAwait(false);
            if (helper is not null && IsVersionCompatible(helper.Version, _serviceVersion))
            {
                return Complete(new RemoteSupportHelperRepairResult(
                    RemoteSupportStatusCodes.InteractiveHelperReady,
                    "Interactive helper connected and matches the service version.",
                    true,
                    false,
                    true,
                    true,
                    false,
                    StalePid: helper.ProcessId,
                    StaleSessionId: helper.SessionId,
                    ConnectedVersion: helper.Version,
                    ServiceVersion: _serviceVersion,
                    LauncherTarget: launcherTarget));
            }

            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperLaunchTimeout,
                "Interactive helper did not connect before timeout.",
                true,
                false,
                true,
                false,
                true,
                StalePid: helper?.ProcessId,
                StaleSessionId: helper?.SessionId,
                ConnectedVersion: helper?.Version,
                ServiceVersion: _serviceVersion,
                LauncherTarget: launcherTarget));
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportHelperRepair] Helper launch failed session={sessionId}: {ex}");
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperLaunchTimeout,
                $"Interactive helper launch failed: {ex.Message}",
                true,
                false,
                true,
                false,
                true,
                Error: ex.Message,
                ServiceVersion: _serviceVersion));
        }
    }

    public Task<RemoteSupportHelperRepairResult> TryRepairAsync(string sessionId, ConnectedUserHelper staleHelper, CancellationToken ct) =>
        TryRepairAsync(sessionId, staleHelper.SessionId, staleHelper, ct);

    public async Task<RemoteSupportHelperRepairResult> TryRepairAsync(
        string sessionId,
        int targetWindowsSessionId,
        ConnectedUserHelper staleHelper,
        CancellationToken ct)
    {
        if (_attempts.AddOrUpdate(sessionId, 1, (_, current) => current + 1) > MaxAttemptsPerSession)
        {
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.HelperRepairCompletedReconnectRequired,
                "Interactive helper repair was already attempted for this Remote Support session.",
                false,
                false,
                false,
                false,
                true,
                StalePid: staleHelper.ProcessId,
                StaleSessionId: staleHelper.SessionId,
                ConnectedVersion: staleHelper.Version,
                ServiceVersion: _serviceVersion));
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.InteractiveHelperRepairSupported = true;
            state.InteractiveHelperRepairInProgress = true;
            state.InteractiveHelperRepairLastAttemptAt = attemptedAt;
            state.InteractiveHelperRepairLastResult = RemoteSupportStatusCodes.HelperRepairStarted;
            state.InteractiveHelperRepairLastError = null;
            state.InteractiveHelperConnectedVersion = staleHelper.Version;
            state.InteractiveHelperServiceVersion = _serviceVersion;
            state.InteractiveHelperStalePid = staleHelper.ProcessId;
            state.InteractiveHelperStaleSessionId = staleHelper.SessionId;
            state.InteractiveHelperTerminateAttempted = false;
            state.InteractiveHelperRelaunchAttempted = false;
        });

        try
        {
            if (staleHelper.SessionId != targetWindowsSessionId)
            {
                return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperRepairRequiresLogoff,
                    "Stale helper does not match the selected Windows session; refusing cross-session repair.",
                    true,
                    false,
                    false,
                    false,
                    true,
                    StalePid: staleHelper.ProcessId,
                    StaleSessionId: staleHelper.SessionId,
                    ConnectedVersion: staleHelper.Version,
                    ServiceVersion: _serviceVersion));
            }

            if (IsProtectedProcess(staleHelper.ProcessId))
            {
                return Complete(new RemoteSupportHelperRepairResult(
                    RemoteSupportStatusCodes.InteractiveHelperRepairRequiresLogoff,
                    "Stale helper process is protected by repair guardrails.",
                    true,
                    false,
                    false,
                    false,
                    true,
                    StalePid: staleHelper.ProcessId,
                    StaleSessionId: staleHelper.SessionId,
                    ConnectedVersion: staleHelper.Version,
                    ServiceVersion: _serviceVersion));
            }

            var task = new RemoteDesktopUserHelperTask();
            task.EnsureLauncherAndRunKey();
            task.TryRegisterScheduledTask();

            var launcherTarget = RemoteDesktopUserHelperTask.GetLauncherPath();
            if (!IsSafeStaleHelperProcess(staleHelper, out var safetyError))
            {
                return Complete(new RemoteSupportHelperRepairResult(
                    RemoteSupportStatusCodes.InteractiveHelperRepairBlockedUnsafeProcess,
                    $"Stale helper was not terminated because it failed repair guardrails: {safetyError}",
                    true,
                    false,
                    false,
                    false,
                    true,
                    Error: safetyError,
                    StalePid: staleHelper.ProcessId,
                    StaleSessionId: staleHelper.SessionId,
                    ConnectedVersion: staleHelper.Version,
                    ServiceVersion: _serviceVersion,
                    LauncherTarget: launcherTarget));
            }

            var terminateAttempted = TryTerminateStaleHelper(staleHelper, out var terminateError);
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.InteractiveHelperLauncherTarget = launcherTarget;
                state.InteractiveHelperLauncherTargetVersion = _serviceVersion;
                state.InteractiveHelperTerminateAttempted = terminateAttempted;
                state.InteractiveHelperRepairLastError = terminateError;
            });

            task.StartForSession(checked((uint)targetWindowsSessionId));
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.InteractiveHelperRelaunchAttempted = true;
                state.InteractiveHelperRelaunchResult = "launch_requested";
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var helper = _helperPipeHost is null
                ? null
                : await _helperPipeHost.WaitForHelperAsync(targetWindowsSessionId, TimeSpan.FromSeconds(TimeoutSeconds), timeoutCts.Token).ConfigureAwait(false);
            if (helper is not null && IsVersionCompatible(helper.Version, _serviceVersion))
            {
                return Complete(new RemoteSupportHelperRepairResult(
                    RemoteSupportStatusCodes.InteractiveHelperReady,
                    "Updated interactive helper connected.",
                    true,
                    terminateAttempted,
                    true,
                    true,
                    false,
                    StalePid: staleHelper.ProcessId,
                    StaleSessionId: staleHelper.SessionId,
                    ConnectedVersion: helper.Version,
                    ServiceVersion: _serviceVersion,
                    LauncherTarget: launcherTarget));
            }

            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperRepairFailed,
                "Interactive helper repair completed but a fresh matching helper did not connect before the browser offer expired. Use Reconnect session.",
                true,
                terminateAttempted,
                true,
                false,
                true,
                Error: terminateError,
                StalePid: staleHelper.ProcessId,
                StaleSessionId: staleHelper.SessionId,
                ConnectedVersion: helper?.Version ?? staleHelper.Version,
                ServiceVersion: _serviceVersion,
                LauncherTarget: launcherTarget));
        }
        catch (OperationCanceledException)
        {
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperLaunchTimeout,
                "Interactive helper repair timed out.",
                true,
                false,
                true,
                false,
                true,
                Error: "timeout",
                StalePid: staleHelper.ProcessId,
                StaleSessionId: staleHelper.SessionId,
                ConnectedVersion: staleHelper.Version,
                ServiceVersion: _serviceVersion));
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportHelperRepair] Repair failed session={sessionId}: {ex}");
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.InteractiveHelperRepairFailed,
                $"Interactive helper repair failed: {ex.Message}",
                true,
                false,
                false,
                false,
                true,
                Error: ex.Message,
                StalePid: staleHelper.ProcessId,
                StaleSessionId: staleHelper.SessionId,
                ConnectedVersion: staleHelper.Version,
                ServiceVersion: _serviceVersion));
        }
    }

    public static RemoteSupportHelperRepairResult RunManualRepair()
    {
        var state = RemoteSupportDiagnosticState.Read();
        var serviceVersion = GetCurrentVersion();
        try
        {
            var task = new RemoteDesktopUserHelperTask();
            task.EnsureLauncherAndRunKey();
            task.TryRegisterScheduledTask();
            var activeSessionId = task.GetActiveConsoleSessionId();
            var desktopState = RemoteDesktopDiagnosticState.Read();
            var stalePid = state.HelperVersionMatchesService ? null : state.InteractiveHelperStalePid ?? desktopState.HelperPid;
            var staleSession = state.HelperSessionId ?? state.InteractiveHelperStaleSessionId ?? desktopState.HelperSessionId;
            var terminateAttempted = false;
            string? error = null;
            if (stalePid is { } pid &&
                staleSession.HasValue &&
                activeSessionId != uint.MaxValue &&
                staleSession.Value == unchecked((int)activeSessionId) &&
                pid != Environment.ProcessId &&
                IsSafeStaleHelperProcess(pid, staleSession.Value, out error))
            {
                terminateAttempted = TryTerminateProcess(pid, out error);
            }

            task.StartForActiveConsoleSession();
            var result = new RemoteSupportHelperRepairResult(
                "manual_helper_repair_requested",
                "Helper launcher was refreshed and relaunch requested.",
                true,
                terminateAttempted,
                true,
                false,
                true,
                Error: error,
                StalePid: stalePid,
                StaleSessionId: staleSession,
                ConnectedVersion: state.HelperVersion,
                ServiceVersion: serviceVersion,
                LauncherTarget: RemoteDesktopUserHelperTask.GetLauncherPath());
            return Complete(result);
        }
        catch (Exception ex)
        {
            return Complete(new RemoteSupportHelperRepairResult(
                RemoteSupportStatusCodes.HelperRelaunchFailed,
                ex.Message,
                true,
                false,
                false,
                false,
                true,
                Error: ex.Message,
                ServiceVersion: serviceVersion));
        }
    }

    private bool IsProtectedProcess(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        var consoleProvider = _consoleProviderPipeHost?.GetConnectedProvider();
        return consoleProvider is not null && consoleProvider.ProcessId == pid;
    }

    private static bool TryTerminateStaleHelper(ConnectedUserHelper helper, out string? error)
    {
        error = null;
        if (helper.ProcessId == Environment.ProcessId)
        {
            error = "Refusing to terminate service process.";
            return false;
        }

        return TryTerminateProcess(helper.ProcessId, out error);
    }

    private static bool IsSafeStaleHelperProcess(ConnectedUserHelper helper, out string? error) =>
        IsSafeStaleHelperProcess(helper.ProcessId, helper.SessionId, out error);

    private static bool IsSafeStaleHelperProcess(int pid, int sessionId, out string? error)
    {
        error = null;
        if (pid == Environment.ProcessId)
        {
            error = "Refusing to terminate the service process.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.SessionId != sessionId)
            {
                error = $"Process session {process.SessionId} does not match helper metadata session {sessionId}.";
                return false;
            }

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                error = $"Could not read process path: {ex.Message}";
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (!string.Equals(fileName, "NetRatel.Client.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fileName, "NetRatel.Client", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Process path is not NetRatel.Client: {path}";
                return false;
            }

            var normalizedPath = path ?? string.Empty;
            if (normalizedPath.IndexOf("NetRatel", StringComparison.OrdinalIgnoreCase) < 0)
            {
                error = $"Process path does not look like an NetRatel helper path: {path}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryTerminateProcess(int pid, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static RemoteSupportHelperRepairResult Complete(RemoteSupportHelperRepairResult result)
    {
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.InteractiveHelperRepairSupported = true;
            state.InteractiveHelperRepairInProgress = false;
            state.InteractiveHelperRepairLastResult = result.StatusCode;
            state.InteractiveHelperRepairLastError = result.Error;
            state.InteractiveHelperLauncherTarget = result.LauncherTarget ?? state.InteractiveHelperLauncherTarget;
            state.InteractiveHelperLauncherTargetVersion = result.ServiceVersion ?? state.InteractiveHelperLauncherTargetVersion;
            state.InteractiveHelperConnectedVersion = result.ConnectedVersion;
            state.InteractiveHelperServiceVersion = result.ServiceVersion;
            state.InteractiveHelperStalePid = result.StalePid;
            state.InteractiveHelperStaleSessionId = result.StaleSessionId;
            state.InteractiveHelperTerminateAttempted = result.TerminateAttempted;
            state.InteractiveHelperRelaunchAttempted = result.RelaunchAttempted;
            state.InteractiveHelperRelaunchResult = result.StatusCode;
        });
        return result;
    }

    private static bool IsVersionCompatible(string? helperVersion, string serviceVersion)
    {
        var helperBase = NormalizeVersion(helperVersion);
        var serviceBase = NormalizeVersion(serviceVersion);
        return !string.IsNullOrWhiteSpace(helperBase) && string.Equals(helperBase, serviceBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        var trimmed = version.Trim();
        var plusIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? trimmed[..plusIndex] : trimmed;
    }

    private static string GetCurrentVersion()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(RemoteSupportInteractiveHelperRepairService).Assembly;
        return assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }
}
