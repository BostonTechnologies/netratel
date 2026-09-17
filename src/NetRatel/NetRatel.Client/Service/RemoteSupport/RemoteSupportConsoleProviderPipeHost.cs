using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

[SupportedOSPlatform("windows")]
internal sealed class RemoteSupportConsoleProviderPipeHost : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private const int PipeBufferBytes = 1024 * 1024;
    private const int StreamBufferBytes = 256 * 1024;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private ConnectedConsoleProvider? _connectedProvider;
    private Process? _providerProcess;
    private Task? _listenerTask;
    private bool _disposed;
    private bool _pipeReady;
    private string? _lastLaunchCommand;

    public event Action<RemoteDesktopPipeMessage>? MessageReceived;
    public event Action<ConnectedConsoleProvider>? ProviderDisconnected;

    public void Start()
    {
        if (_listenerTask is not null)
        {
            return;
        }

        LogManager.WriteLog("[RemoteSupportConsoleProvider] Pipe host startup.");
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.ConsoleProviderPipeName = RemoteSupportConsoleProviderConstants.PipeName;
            state.ConsoleProviderPipePath = RemoteSupportConsoleProviderConstants.FullPipePath;
            state.ConsoleProviderPipeSecurity = RemoteSupportConsoleProviderConstants.SecurityDescription;
            state.ConsoleProviderPipeHostStarted = true;
            state.ConsoleProviderPipeHostReady = false;
            state.ConsoleProviderHelperConnected = false;
            state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderPipeNotReady;
            state.ConsoleProviderLastError = null;
        });
        LogManager.WriteLog($"[RemoteSupportConsoleProvider] Pipe listener starting name={RemoteSupportConsoleProviderConstants.PipeName} path={RemoteSupportConsoleProviderConstants.FullPipePath}");
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
    }

    public ConnectedConsoleProvider? GetConnectedProvider()
    {
        lock (_sync)
        {
            return _connectedProvider;
        }
    }

    public bool ResetConnectedProviderIfVersionMismatch(string serviceVersion)
    {
        lock (_sync)
        {
            var provider = _connectedProvider;
            if (provider is null || IsVersionCompatible(provider.Version, serviceVersion))
            {
                return false;
            }

            LogManager.WriteLog($"[RemoteSupportConsoleProvider] Resetting stale console provider pid={provider.ProcessId} providerVersion={provider.Version} serviceVersion={serviceVersion}");
            _connectedProvider = null;
            try
            {
                provider.Writer.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_providerProcess is { HasExited: false } &&
                    _providerProcess.Id == provider.ProcessId)
                {
                    _providerProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Failed to terminate stale console provider pid={provider.ProcessId}: {ex.Message}");
            }

            _providerProcess = null;
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperConnected = false;
                state.ConsoleProviderVersionMatchesService = false;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperStarting;
                state.ConsoleProviderLastError = "Stale console provider was reset for relaunch.";
            });
            return true;
        }
    }

    public async Task<ConnectedConsoleProvider?> EnsureProviderAsync(TimeSpan timeout, CancellationToken ct)
    {
        var existing = GetConnectedProvider();
        if (existing is not null)
        {
            return existing;
        }

        if (_listenerTask is null)
        {
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderPipeHostNotStarted;
                state.ConsoleProviderLastError = "Console provider pipe host has not been started.";
            });
            return null;
        }

        if (!_pipeReady)
        {
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderPipeNotReady;
                state.ConsoleProviderLastError = "Console provider pipe host is not ready yet.";
            });
        }

        StartProviderProcess();
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var provider = GetConnectedProvider();
            if (provider is not null)
            {
                return provider;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        RecordProviderProcessExitCodeIfAvailable();
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.ConsoleProviderLastStage = state.ConsoleProviderHelperLaunchError is null
                ? RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello
                : RemoteSupportStatusCodes.ConsoleProviderLaunchFailed;
            state.ConsoleProviderLastError = state.ConsoleProviderHelperLaunchError
                ?? $"Console provider process did not send hello within {timeout.TotalSeconds:n0} seconds.";
        });
        return null;
    }

    private static bool IsVersionCompatible(string? helperVersion, string serviceVersion)
    {
        var helperBase = NormalizeVersion(helperVersion);
        var serviceBase = NormalizeVersion(serviceVersion);
        return !string.IsNullOrWhiteSpace(helperBase) &&
               string.Equals(helperBase, serviceBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        return version.Trim()
            .Split('+', 2, StringSplitOptions.TrimEntries)[0]
            .Split('-', 2, StringSplitOptions.TrimEntries)[0];
    }

    private void StartProviderProcess()
    {
        lock (_sync)
        {
            if (_providerProcess is { HasExited: false })
            {
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelperLaunchAttempted = true;
                    state.ConsoleProviderHelperLaunchCommand = _lastLaunchCommand;
                    state.ConsoleProviderHelperPid = _providerProcess.Id;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperStarting;
                    state.ConsoleProviderLastError = null;
                });
                return;
            }

            try
            {
                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Unable to resolve NetRatel.Client executable path.");
                _lastLaunchCommand = $"\"{exePath}\" --remote-support-console-helper";
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelperLaunchAttempted = true;
                    state.ConsoleProviderHelperLaunchCommand = _lastLaunchCommand;
                    state.ConsoleProviderHelperLaunchExitCode = null;
                    state.ConsoleProviderHelperLaunchError = null;
                    state.ConsoleProviderLauncherBackend = null;
                    state.ConsoleProviderDuplicatedTokenSucceeded = false;
                    state.ConsoleProviderSetTokenSessionIdSucceeded = false;
                    state.ConsoleProviderTokenSessionId = null;
                    state.ConsoleProviderCreateEnvironmentBlockSucceeded = false;
                    state.ConsoleProviderCreateProcessAsUserSucceeded = false;
                    state.ConsoleProviderLaunchedProcessSessionId = null;
                    state.ConsoleProviderLaunchedDesktop = null;
                    state.ConsoleProviderLaunchWin32Error = null;
                    state.ConsoleProviderLaunchHresult = null;
                    state.ConsoleProviderLaunchException = null;
                    state.ConsoleProviderLaunchAttemptsJson = null;
                    state.ConsoleProviderHelperPid = null;
                    state.ConsoleProviderHelperSessionId = null;
                    state.ConsoleProviderHelperActiveConsoleSessionId = null;
                    state.ConsoleProviderHelperLaunchedInTargetSession = false;
                    state.ConsoleProviderHelperWindowStation = null;
                    state.ConsoleProviderHelperDesktop = null;
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderHelperVersion = null;
                    state.ConsoleProviderVersionMatchesService = false;
                    state.ConsoleProviderHelloReceived = false;
                    state.ConsoleProviderLastStage = "console_provider_launch_attempted";
                    state.ConsoleProviderLastError = null;
                });
                var launchSummary = new RemoteSupportConsoleActiveSessionLauncher(exePath)
                    .Launch("--remote-support-console-helper");
                var bestLaunch = launchSummary.BestResult;
                _providerProcess = bestLaunch?.LaunchedPid is { } launchedPid
                    ? TryGetProcessById(launchedPid)
                    : null;
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Started provider launch activeSession={launchSummary.ActiveConsoleSessionId?.ToString() ?? "<unknown>"} pid={_providerProcess?.Id.ToString() ?? bestLaunch?.LaunchedPid?.ToString() ?? "<unknown>"} path={exePath}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderLauncherBackend = bestLaunch?.LauncherBackend;
                    state.ConsoleProviderDuplicatedTokenSucceeded = bestLaunch?.DuplicatedTokenSucceeded == true;
                    state.ConsoleProviderSetTokenSessionIdSucceeded = bestLaunch?.SetTokenSessionIdSucceeded == true;
                    state.ConsoleProviderTokenSessionId = bestLaunch?.TokenSessionId;
                    state.ConsoleProviderCreateEnvironmentBlockSucceeded = bestLaunch?.CreateEnvironmentBlockSucceeded == true;
                    state.ConsoleProviderCreateProcessAsUserSucceeded = bestLaunch?.CreateProcessAsUserSucceeded == true;
                    state.ConsoleProviderLaunchedProcessSessionId = bestLaunch?.LaunchedProcessSessionId;
                    state.ConsoleProviderLaunchedDesktop = bestLaunch?.LaunchedDesktop;
                    state.ConsoleProviderLaunchWin32Error = bestLaunch?.LaunchWin32Error;
                    state.ConsoleProviderLaunchHresult = bestLaunch?.LaunchHresult;
                    state.ConsoleProviderLaunchException = bestLaunch?.LaunchException;
                    state.ConsoleProviderLaunchAttemptsJson = JsonSerializer.Serialize(launchSummary.Results, _json);
                    state.ConsoleProviderHelperPid = _providerProcess?.Id ?? bestLaunch?.LaunchedPid;
                    state.ConsoleProviderHelperActiveConsoleSessionId = launchSummary.ActiveConsoleSessionId;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopHelperStarting;
                    state.ConsoleProviderLastError = bestLaunch is null || bestLaunch.CreateProcessAsUserSucceeded == false
                        ? bestLaunch?.LaunchException ?? "No active-session console provider launch backend succeeded."
                        : null;
                    if (_providerProcess is null)
                    {
                        state.ConsoleProviderHelperLaunchError = state.ConsoleProviderLastError;
                    }
                });
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Provider process start failed: {ex}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelperLaunchError = ex.Message;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderLaunchFailed;
                    state.ConsoleProviderLastError = ex.Message;
                });
            }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                _pipeReady = true;
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderPipeName = RemoteSupportConsoleProviderConstants.PipeName;
                    state.ConsoleProviderPipePath = RemoteSupportConsoleProviderConstants.FullPipePath;
                    state.ConsoleProviderPipeSecurity = RemoteSupportConsoleProviderConstants.SecurityDescription;
                    state.ConsoleProviderPipeHostStarted = true;
                    state.ConsoleProviderPipeHostReady = true;
                    state.ConsoleProviderLastStage = "console_provider_pipe_ready";
                    state.ConsoleProviderLastError = null;
                });
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Pipe listener ready name={RemoteSupportConsoleProviderConstants.PipeName} path={RemoteSupportConsoleProviderConstants.FullPipePath}");
                LogManager.WriteLog("[RemoteSupportConsoleProvider] Waiting for console helper.");
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _pipeReady = false;
                LogManager.WriteLog("[RemoteSupportConsoleProvider] Pipe client connected; waiting for hello.");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderPipeHostReady = false;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello;
                    state.ConsoleProviderLastError = null;
                });
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
                LogManager.WriteLog("[RemoteSupportConsoleProvider] Pipe connection ended; restarting listener.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _pipeReady = false;
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Pipe listener exception: {ex}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderPipeHostReady = false;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderPipeNotReady;
                    state.ConsoleProviderLastError = ex.Message;
                });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: StreamBufferBytes, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
        ConnectedConsoleProvider? provider = null;

        try
        {
            var helloLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderRawFirstMessageBytes = helloLine is null ? null : Encoding.UTF8.GetByteCount(helloLine);
                state.ConsoleProviderRawFirstMessagePreview = TrimPreview(helloLine, 240);
                state.ConsoleProviderHelloParseError = null;
            });
            LogManager.WriteLog($"[RemoteSupportConsoleProvider] console_provider_raw_first_message_bytes={Encoding.UTF8.GetByteCount(helloLine ?? string.Empty)}");
            LogManager.WriteLog($"[RemoteSupportConsoleProvider] console_provider_raw_first_message_preview={TrimPreview(helloLine, 240) ?? "<null>"}");
            var helloMessage = DeserializePipeMessage(helloLine, isHello: true);
            if (helloMessage is null ||
                !string.Equals(helloMessage.Kind, "hello", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(helloMessage.PayloadJson))
            {
                LogManager.WriteLog("[RemoteSupportConsoleProvider] Provider did not send a valid hello; closing connection.");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelloReceived = false;
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello;
                    state.ConsoleProviderLastError = "Provider did not send a valid hello.";
                    state.ConsoleProviderHelloParseError ??= "Provider did not send a valid hello.";
                });
                return;
            }

            RemoteSupportConsoleProviderHello? hello;
            try
            {
                hello = JsonSerializer.Deserialize<RemoteSupportConsoleProviderHello>(helloMessage.PayloadJson, _json);
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] console_provider_hello_parse_failed: {ex.Message}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelloReceived = false;
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderHelloParseError = ex.Message;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello;
                    state.ConsoleProviderLastError = $"Provider hello payload parse failed: {ex.Message}";
                });
                return;
            }

            if (hello is null ||
                !string.Equals(hello.Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal))
            {
                LogManager.WriteLog("[RemoteSupportConsoleProvider] Provider hello was invalid or wrong provider.");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelloReceived = false;
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello;
                    state.ConsoleProviderLastError = "Provider hello was invalid or wrong provider.";
                    state.ConsoleProviderHelloParseError = "Provider hello was invalid or wrong provider.";
                });
                return;
            }

            var activeConsoleSessionId = hello.ActiveConsoleSessionId;
            var launchedInTargetSession = hello.HelperLaunchedInTargetSession ||
                (activeConsoleSessionId.HasValue && hello.SessionId == unchecked((int)activeConsoleSessionId.Value));
            if (!launchedInTargetSession)
            {
                var error = $"Console provider helper launched in session {hello.SessionId}, but active console session is {activeConsoleSessionId?.ToString() ?? "<unknown>"}.";
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] {error}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelperPid = hello.ProcessId;
                    state.ConsoleProviderHelperSessionId = hello.SessionId;
                    state.ConsoleProviderHelperActiveConsoleSessionId = hello.ActiveConsoleSessionId;
                    state.ConsoleProviderHelperLaunchedInTargetSession = false;
                    state.ConsoleProviderHelperWindowStation = hello.WindowStation;
                    state.ConsoleProviderHelperDesktop = hello.Desktop;
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderHelloReceived = true;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderLaunchFailed;
                    state.ConsoleProviderLastError = error;
                });
                return;
            }

            provider = new ConnectedConsoleProvider(
                Guid.NewGuid(),
                hello.SessionId,
                hello.ProcessId,
                hello.Version,
                hello.Provider,
                hello.DesktopState,
                hello.InputDesktopName,
                hello.ActiveConsoleSessionId,
                hello.HelperLaunchedInTargetSession,
                hello.WindowStation,
                hello.Desktop,
                DateTimeOffset.UtcNow,
                writer);
            lock (_sync)
            {
                _connectedProvider = provider;
            }

            LogManager.WriteLog($"[RemoteSupportConsoleProvider] Provider registered pid={provider.ProcessId} sessionId={provider.SessionId} provider={provider.Provider} desktopState={provider.DesktopState} desktop={provider.InputDesktopName ?? "<unknown>"} version={provider.Version}");
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperPid = provider.ProcessId;
                state.ConsoleProviderHelperSessionId = provider.SessionId;
                state.ConsoleProviderHelperActiveConsoleSessionId = provider.ActiveConsoleSessionId;
                state.ConsoleProviderHelperLaunchedInTargetSession = provider.HelperLaunchedInTargetSession;
                state.ConsoleProviderHelperWindowStation = provider.WindowStation;
                state.ConsoleProviderHelperDesktop = provider.Desktop;
                state.ConsoleProviderHelperConnected = true;
                state.ConsoleProviderHelperVersion = provider.Version;
                state.ConsoleProviderHelloReceived = true;
                state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleSecureDesktopMediaPreviewAvailable;
                state.ConsoleProviderLastError = null;
            });
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                var message = DeserializePipeMessage(line, isHello: false);
                if (message is null)
                {
                    continue;
                }

                provider.LastHeartbeatUtc = DateTimeOffset.UtcNow;
                if (string.Equals(message.Kind, "heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                MessageReceived?.Invoke(message);
            }
        }
        finally
        {
            if (provider is not null)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_connectedProvider, provider))
                    {
                        _connectedProvider = null;
                    }
                }

                LogManager.WriteLog($"[RemoteSupportConsoleProvider] Provider disconnected pid={provider.ProcessId} sessionId={provider.SessionId}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelperConnected = false;
                    state.ConsoleProviderLastStage = "console_provider_disconnected";
                    state.ConsoleProviderLastError = null;
                });
                ProviderDisconnected?.Invoke(provider);
            }
        }
    }

    private void RecordProviderProcessExitCodeIfAvailable()
    {
        try
        {
            if (_providerProcess is not { HasExited: true })
            {
                return;
            }

            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.ConsoleProviderHelperLaunchExitCode = _providerProcess.ExitCode;
                if (_providerProcess.ExitCode != 0 && string.IsNullOrWhiteSpace(state.ConsoleProviderHelperLaunchError))
                {
                    state.ConsoleProviderHelperLaunchError = $"Console provider exited with code {_providerProcess.ExitCode}.";
                }
            });
        }
        catch
        {
        }
    }

    private static Process? TryGetProcessById(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            return null;
        }
    }

    private RemoteDesktopPipeMessage? DeserializePipeMessage(string? line, bool isHello)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteDesktopPipeMessage>(line, _json);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportConsoleProvider] Ignoring malformed pipe message: {ex.Message}");
            if (isHello)
            {
                LogManager.WriteLog($"[RemoteSupportConsoleProvider] console_provider_hello_parse_failed: {ex.Message}");
                RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
                {
                    state.ConsoleProviderHelloParseError = ex.Message;
                    state.ConsoleProviderLastStage = RemoteSupportStatusCodes.ConsoleProviderStartedButNoHello;
                    state.ConsoleProviderLastError = $"Provider hello parse failed: {ex.Message}";
                });
            }

            return null;
        }
    }

    private static string? TrimPreview(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            RemoteSupportConsoleProviderConstants.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            PipeBufferBytes,
            PipeBufferBytes,
            security);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            if (_providerProcess is { HasExited: false })
            {
                _providerProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _cts.Dispose();
    }
}

internal static class RemoteSupportConsoleProviderConstants
{
    public const string PipeName = "netratel-remote-support-console-provider";
    public const string FullPipePath = @"\\.\pipe\netratel-remote-support-console-provider";
    public const string SecurityDescription = "LocalSystem FullControl; Builtin Administrators ReadWrite";
}

internal sealed record RemoteSupportConsoleProviderHello(
    int SessionId,
    int ProcessId,
    string Version,
    string Provider,
    string DesktopState,
    string? InputDesktopName,
    uint? ActiveConsoleSessionId = null,
    bool HelperLaunchedInTargetSession = false,
    string? WindowStation = null,
    string? Desktop = null);

internal sealed class ConnectedConsoleProvider
{
    public ConnectedConsoleProvider(
        Guid routeId,
        int sessionId,
        int processId,
        string version,
        string provider,
        string desktopState,
        string? inputDesktopName,
        uint? activeConsoleSessionId,
        bool helperLaunchedInTargetSession,
        string? windowStation,
        string? desktop,
        DateTimeOffset connectedAtUtc,
        StreamWriter writer)
    {
        RouteId = routeId;
        SessionId = sessionId;
        ProcessId = processId;
        Version = version;
        Provider = provider;
        DesktopState = desktopState;
        InputDesktopName = inputDesktopName;
        ActiveConsoleSessionId = activeConsoleSessionId;
        HelperLaunchedInTargetSession = helperLaunchedInTargetSession;
        WindowStation = windowStation;
        Desktop = desktop;
        ConnectedAtUtc = connectedAtUtc;
        LastHeartbeatUtc = connectedAtUtc;
        Writer = writer;
    }

    public Guid RouteId { get; }
    public int SessionId { get; }
    public int ProcessId { get; }
    public string Version { get; }
    public string Provider { get; }
    public string DesktopState { get; }
    public string? InputDesktopName { get; }
    public uint? ActiveConsoleSessionId { get; }
    public bool HelperLaunchedInTargetSession { get; }
    public string? WindowStation { get; }
    public string? Desktop { get; }
    public DateTimeOffset ConnectedAtUtc { get; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public StreamWriter Writer { get; }
    public object WriterSync { get; } = new();

    public void Send(RemoteDesktopPipeMessage message, JsonSerializerOptions json)
    {
        lock (WriterSync)
        {
            Writer.WriteLine(JsonSerializer.Serialize(message, json));
            Writer.Flush();
        }
    }
}
