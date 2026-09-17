using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;

namespace NetRatel.Client.Service.RemoteSupport;

internal interface IRemoteSupportCaptureProvider : IDisposable
{
    string Name { get; }
    string BackendName { get; }
    RemoteSupportCaptureFrame CaptureFrame(int maxWidth, int maxHeight);
    void Refresh();
}

internal interface IRemoteSupportThreadBoundCaptureProvider
{
    void ReleaseThreadContext();
}

internal sealed record RemoteSupportCaptureFrame(
    byte[] Bgr,
    int SourceWidth,
    int SourceHeight,
    int FrameWidth,
    int FrameHeight,
    string BackendName);

internal sealed class RemoteSupportCaptureException : Exception
{
    public RemoteSupportCaptureException(
        string message,
        string providerName,
        string backendName,
        int? win32Error,
        string? desktopName,
        string? threadDesktopBefore,
        string? threadDesktopAfter,
        bool deterministicFailure = false,
        Exception? inner = null,
        string? captureState = null,
        int attemptCount = 0,
        long elapsedMilliseconds = 0,
        int? hResult = null,
        bool firstFrameDelivered = false,
        int retryAfterMilliseconds = 0,
        IReadOnlyList<RemoteSupportCaptureBackendResult>? backendResults = null)
        : base(message, inner)
    {
        ProviderName = providerName;
        BackendName = backendName;
        Win32Error = win32Error;
        DesktopName = desktopName;
        ThreadDesktopBefore = threadDesktopBefore;
        ThreadDesktopAfter = threadDesktopAfter;
        DeterministicFailure = deterministicFailure;
        CaptureState = captureState;
        AttemptCount = attemptCount;
        ElapsedMilliseconds = elapsedMilliseconds;
        HResultCode = hResult;
        FirstFrameDelivered = firstFrameDelivered;
        RetryAfterMilliseconds = retryAfterMilliseconds;
        BackendResults = backendResults;
    }

    public string ProviderName { get; }
    public string BackendName { get; }
    public int? Win32Error { get; }
    public string? DesktopName { get; }
    public string? ThreadDesktopBefore { get; }
    public string? ThreadDesktopAfter { get; }
    public bool DeterministicFailure { get; }
    public string? CaptureState { get; }
    public int AttemptCount { get; }
    public long ElapsedMilliseconds { get; }
    public int? HResultCode { get; }
    public bool FirstFrameDelivered { get; }
    public int RetryAfterMilliseconds { get; }
    public IReadOnlyList<RemoteSupportCaptureBackendResult>? BackendResults { get; }
}

internal static class RemoteSupportCaptureProviderFactory
{
    public static IRemoteSupportCaptureProvider Create(
        string sessionId,
        RemoteSupportProviderMetadata providerMetadata,
        RemoteSupportDesktopContextCoordinator? desktopContextCoordinator = null) =>
        providerMetadata.IsConsoleProvider
            ? new ConsoleSecureDesktopCaptureProvider(sessionId)
            : new InteractiveDesktopCaptureProvider(
                sessionId,
                desktopContextCoordinator ?? new RemoteSupportDesktopContextCoordinator());
}

internal sealed class InteractiveDesktopCaptureProvider : IRemoteSupportCaptureProvider, IRemoteSupportThreadBoundCaptureProvider
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan InitialFrameDeadline = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DesktopTransitionDeadline = TimeSpan.FromSeconds(15);

    private readonly RemoteSupportDesktopContextCoordinator _coordinator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _initialFrameDeadline;
    private readonly TimeSpan _desktopTransitionDeadline;
    private RemoteSupportInteractiveDesktopContext? _desktopContext;
    private DateTimeOffset? _initializationStartedAt;
    private DateTimeOffset? _recoveryStartedAt;
    private bool _firstFrameDelivered;
    private int _attemptCount;
    private int _consecutiveFailures;

    public InteractiveDesktopCaptureProvider(string sessionId)
        : this(
            sessionId,
            new RemoteSupportDesktopContextCoordinator(),
            () => DateTimeOffset.UtcNow,
            InitialFrameDeadline,
            DesktopTransitionDeadline)
    {
    }

    internal InteractiveDesktopCaptureProvider(
        string sessionId,
        RemoteSupportDesktopContextCoordinator coordinator,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? initialFrameDeadline = null,
        TimeSpan? desktopTransitionDeadline = null)
    {
        SessionId = sessionId;
        _coordinator = coordinator;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _initialFrameDeadline = initialFrameDeadline ?? InitialFrameDeadline;
        _desktopTransitionDeadline = desktopTransitionDeadline ?? DesktopTransitionDeadline;
    }

    public string SessionId { get; }
    public string Name => nameof(InteractiveDesktopCaptureProvider);
    public string BackendName => "CopyFromScreen";

    [SupportedOSPlatform("windows")]
    public RemoteSupportCaptureFrame CaptureFrame(int maxWidth, int maxHeight)
    {
        var now = _utcNow();
        _initializationStartedAt ??= now;
        _attemptCount++;
        _desktopContext ??= new RemoteSupportInteractiveDesktopContext(
            new WindowsRemoteSupportDesktopContextNative(),
            _coordinator,
            requireInputProbe: false,
            _utcNow);

        var context = _desktopContext.EnsureReady();
        if (!context.Ready)
        {
            _consecutiveFailures++;
            throw CreateRecoveryException(now, context, "Interactive capture could not attach to the current input desktop.");
        }

        var sourceWidth = Math.Max(1, Native.GetSystemMetrics(0));
        var sourceHeight = Math.Max(1, Native.GetSystemMetrics(1));
        var (frameWidth, frameHeight) = RemoteSupportCaptureScaling.ScaleToFit(sourceWidth, sourceHeight, maxWidth, maxHeight);
        try
        {
            var bgr = RemoteSupportBitmapCapture.CaptureWithCopyFromScreen(sourceWidth, sourceHeight, frameWidth, frameHeight);
            _firstFrameDelivered = true;
            _recoveryStartedAt = null;
            _consecutiveFailures = 0;
            return new RemoteSupportCaptureFrame(bgr, sourceWidth, sourceHeight, frameWidth, frameHeight, BackendName);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _desktopContext.Invalidate($"capture_failed:{ex.GetType().Name}");
            throw CreateRecoveryException(now, _desktopContext.Snapshot, $"Interactive desktop capture failed: {ex.Message}", ex);
        }
    }

    private RemoteSupportCaptureException CreateRecoveryException(
        DateTimeOffset now,
        RemoteSupportDesktopContextSnapshot context,
        string reason,
        Exception? inner = null)
    {
        var recoveryStart = _firstFrameDelivered
            ? _recoveryStartedAt ??= now
            : _initializationStartedAt ??= now;
        var deadline = _firstFrameDelivered ? _desktopTransitionDeadline : _initialFrameDeadline;
        var elapsed = now - recoveryStart;
        var terminal = elapsed >= deadline;
        var state = terminal ? "interactive_desktop_recovery_exhausted" : "interactive_desktop_recovering";
        var error = $"{reason} captureState={state} attempt={_attemptCount} elapsedMs={(long)elapsed.TotalMilliseconds} desktop={context.DesktopName ?? "<unknown>"} generation={context.Generation} win32={context.Win32Error}";
        return new RemoteSupportCaptureException(
            error,
            Name,
            BackendName,
            context.Win32Error,
            context.DesktopName,
            null,
            context.DesktopName,
            deterministicFailure: terminal,
            inner: inner,
            captureState: state,
            attemptCount: _attemptCount,
            elapsedMilliseconds: (long)elapsed.TotalMilliseconds,
            hResult: inner?.HResult,
            firstFrameDelivered: _firstFrameDelivered,
            retryAfterMilliseconds: (int)ProbeInterval.TotalMilliseconds);
    }

    public void Dispose()
    {
        if (_desktopContext?.Snapshot.WorkerThreadId == Environment.CurrentManagedThreadId)
        {
            ReleaseThreadContext();
        }
    }

    public void Refresh()
    {
        _coordinator.Invalidate("capture_refresh_requested");
        _recoveryStartedAt = _firstFrameDelivered ? _utcNow() : null;
    }

    public void ReleaseThreadContext()
    {
        _desktopContext?.Dispose();
        _desktopContext = null;
    }
}

internal interface IConsoleCaptureBackendRunner
{
    RemoteSupportCaptureBackendMatrixResult Run(string sessionId, int maxWidth, int maxHeight, bool writeSuccessfulJpeg);
    (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) CaptureWithBackend(string backendName, int maxWidth, int maxHeight);
}

[SupportedOSPlatform("windows")]
internal sealed class ConsoleCaptureBackendRunner : IConsoleCaptureBackendRunner
{
    public RemoteSupportCaptureBackendMatrixResult Run(string sessionId, int maxWidth, int maxHeight, bool writeSuccessfulJpeg) =>
        RemoteSupportCaptureBackendMatrix.Run(sessionId, maxWidth, maxHeight, writeSuccessfulJpeg);

    public (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) CaptureWithBackend(
        string backendName,
        int maxWidth,
        int maxHeight) => RemoteSupportCaptureBackendMatrix.CaptureWithBackend(backendName, maxWidth, maxHeight);
}

internal sealed class ConsoleSecureDesktopCaptureProvider : IRemoteSupportCaptureProvider
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan InitialFrameDeadline = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DesktopTransitionDeadline = TimeSpan.FromSeconds(15);

    private readonly string _sessionId;
    private readonly IConsoleCaptureBackendRunner _backendRunner;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _initialFrameDeadline;
    private readonly TimeSpan _desktopTransitionDeadline;
    private RemoteSupportCaptureBackendMatrixResult? _matrix;
    private RemoteSupportCaptureBackendMatrixResult? _lastProbe;
    private DateTimeOffset? _initializationStartedAt;
    private DateTimeOffset? _recoveryStartedAt;
    private bool _firstFrameDelivered;
    private int _attemptCount;
    private int _consecutiveFailures;

    public ConsoleSecureDesktopCaptureProvider(string sessionId)
        : this(
            sessionId,
            CreateBackendRunner(),
            () => DateTimeOffset.UtcNow,
            InitialFrameDeadline,
            DesktopTransitionDeadline)
    {
    }

    internal ConsoleSecureDesktopCaptureProvider(
        string sessionId,
        IConsoleCaptureBackendRunner backendRunner,
        Func<DateTimeOffset> utcNow,
        TimeSpan initialFrameDeadline,
        TimeSpan desktopTransitionDeadline)
    {
        _sessionId = sessionId;
        _backendRunner = backendRunner;
        _utcNow = utcNow;
        _initialFrameDeadline = initialFrameDeadline;
        _desktopTransitionDeadline = desktopTransitionDeadline;
    }

    public string Name => nameof(ConsoleSecureDesktopCaptureProvider);
    public string BackendName => _matrix?.BestBackend?.BackendName ?? "CaptureBackendMatrix";

    private static IConsoleCaptureBackendRunner CreateBackendRunner()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Console desktop capture is supported only on Windows.");
        }

        return new ConsoleCaptureBackendRunner();
    }

    [SupportedOSPlatform("windows")]
    public RemoteSupportCaptureFrame CaptureFrame(int maxWidth, int maxHeight)
    {
        var now = _utcNow();
        _initializationStartedAt ??= now;
        if (_matrix is null)
        {
            _attemptCount++;
            _lastProbe = _backendRunner.Run(_sessionId, maxWidth, maxHeight, writeSuccessfulJpeg: false);
            if (_lastProbe.BestBackend is not null && _lastProbe.BestFrame is not null)
            {
                _matrix = _lastProbe;
                return RecordSuccess(_lastProbe.BestFrame, _lastProbe.BestBackend, now);
            }

            _consecutiveFailures++;
            throw CreateRecoveryException(_lastProbe, now, "No console capture backend produced a frame.");
        }

        var selectedBackend = _matrix.BestBackend?.BackendName ??
            throw new InvalidOperationException("A cached console capture matrix must contain a successful backend.");
        var (captureResult, freshFrame) = _backendRunner.CaptureWithBackend(
            selectedBackend,
            maxWidth,
            maxHeight);
        if (freshFrame is null)
        {
            _consecutiveFailures++;
            _attemptCount++;
            _recoveryStartedAt ??= now;
            _lastProbe = new RemoteSupportCaptureBackendMatrixResult(
                new[] { captureResult },
                null,
                null,
                null);
            _matrix = null;
            var error = $"Console capture backend {selectedBackend} did not produce a fresh frame. win32={captureResult.Win32Error?.ToString() ?? "<none>"} hresult={captureResult.HResult?.ToString() ?? "<none>"} error={captureResult.Exception ?? captureResult.BackendInitError ?? "failed"}";
            throw CreateRecoveryException(_lastProbe, now, error);
        }

        return RecordSuccess(freshFrame, captureResult, now);
    }

    private RemoteSupportCaptureFrame RecordSuccess(
        RemoteSupportCaptureFrame frame,
        RemoteSupportCaptureBackendResult backend,
        DateTimeOffset now)
    {
        _consecutiveFailures = 0;
        _firstFrameDelivered = true;
        _recoveryStartedAt = null;
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.CaptureProviderLastSuccessfulFrameUtc = now;
            state.CaptureProviderConsecutiveFailures = 0;
            state.CaptureProviderLastFrameError = null;
            state.CaptureProviderBackendName = backend.BackendName;
            state.CaptureProviderBestBackend = backend.BackendName;
            state.CaptureProviderLastWin32Error = backend.Win32Error;
            state.RemoteSupportCaptureSupported = true;
            state.RemoteSupportFirstFrameDelivered = true;
        });
        return frame;
    }

    private RemoteSupportCaptureException CreateRecoveryException(
        RemoteSupportCaptureBackendMatrixResult probe,
        DateTimeOffset now,
        string reason)
    {
        var recoveryStart = _firstFrameDelivered
            ? _recoveryStartedAt ??= now
            : _initializationStartedAt ??= now;
        var deadline = _firstFrameDelivered ? _desktopTransitionDeadline : _initialFrameDeadline;
        var elapsed = now - recoveryStart;
        var terminal = elapsed >= deadline;
        var stateName = terminal
            ? "desktop_capture_failed"
            : _firstFrameDelivered ? "desktop_transition_recovering" : "capture_initializing";
        var last = probe.Results.LastOrDefault(x => !x.FirstFrameSucceeded) ?? probe.Results.LastOrDefault();
        var failedBackends = string.Join(",", probe.Results.Where(x => !x.FirstFrameSucceeded).Select(x => x.BackendName));
        var error = $"{reason} captureState={stateName} attempt={_attemptCount} elapsedMs={(long)elapsed.TotalMilliseconds} failedBackends={failedBackends} lastWin32Error={last?.Win32Error?.ToString() ?? "<none>"} lastHresult={last?.HResult?.ToString() ?? "<none>"} desktop={last?.TargetDesktop ?? "<unknown>"}";
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.CaptureProviderLastFrameAttemptUtc = now;
            state.CaptureProviderName = Name;
            state.CaptureProviderSelected = _matrix is not null;
            state.CaptureProviderBackendName = _matrix?.BestBackend?.BackendName ?? "none";
            state.CaptureProviderBestBackend = _matrix?.BestBackend?.BackendName ?? "none";
            state.CaptureProviderFailedBackends = failedBackends;
            state.CaptureProviderLastFrameError = error;
            state.CaptureProviderConsecutiveFailures = _consecutiveFailures;
            state.CaptureProviderLastWin32Error = last?.Win32Error;
            state.CaptureProviderInitFailed = terminal;
            state.RemoteSupportCaptureSupported = !terminal;
            state.RemoteSupportFirstFrameDelivered = _firstFrameDelivered;
        });
        return new RemoteSupportCaptureException(
            error,
            Name,
            _matrix?.BestBackend?.BackendName ?? "none",
            last?.Win32Error,
            last?.TargetDesktop,
            last?.ThreadDesktopBefore,
            last?.ThreadDesktopAfter,
            deterministicFailure: terminal,
            captureState: stateName,
            attemptCount: _attemptCount,
            elapsedMilliseconds: (long)elapsed.TotalMilliseconds,
            hResult: last?.HResult,
            firstFrameDelivered: _firstFrameDelivered,
            retryAfterMilliseconds: (int)ProbeInterval.TotalMilliseconds,
            backendResults: probe.Results);
    }

    [SupportedOSPlatform("windows")]
    public RemoteSupportConsoleCaptureSelfTestResult RunOneShotSelfTest(int maxWidth = 1024, int maxHeight = 576)
    {
        var matrix = RemoteSupportCaptureBackendMatrix.Run(_sessionId, maxWidth, maxHeight, writeSuccessfulJpeg: true);
        var best = matrix.BestBackend;
        var last = best ?? matrix.Results.LastOrDefault();
        return new RemoteSupportConsoleCaptureSelfTestResult(
            best is not null,
            matrix.BestFrame?.FrameWidth,
            matrix.BestFrame?.FrameHeight,
            matrix.BestFrame?.Bgr.Length,
            matrix.OutputJpegPath,
            last?.Win32Error,
            best is null ? string.Join("; ", matrix.Results.Select(x => $"{x.BackendName}: {x.Exception ?? x.BackendInitError ?? "failed"}")) : null,
            last?.TargetDesktop,
            last?.ThreadDesktopBefore,
            last?.ThreadDesktopAfter,
            best?.BackendName ?? "none",
            matrix);
    }

    public void Dispose()
    {
    }

    public void Refresh()
    {
        _matrix = null;
        _lastProbe = null;
        _consecutiveFailures = 0;
        _attemptCount = 0;
        if (_firstFrameDelivered)
        {
            _recoveryStartedAt = _utcNow();
        }
        else
        {
            _initializationStartedAt = _utcNow();
        }
        RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
        {
            state.CaptureProviderInitStarted = null;
            state.CaptureProviderInitSucceeded = false;
            state.CaptureProviderInitFailed = false;
            state.CaptureProviderInitError = null;
            state.CaptureProviderLastFrameError = null;
            state.CaptureProviderConsecutiveFailures = 0;
        });
    }
}

internal sealed record RemoteSupportConsoleCaptureSelfTestResult(
    bool Succeeded,
    int? FrameWidth,
    int? FrameHeight,
    int? FrameBytes,
    string? OutputPath,
    int? Win32Error,
    string? Error,
    string? DesktopName,
    string? ThreadDesktopBefore,
    string? ThreadDesktopAfter,
    string BackendName,
    RemoteSupportCaptureBackendMatrixResult? Matrix = null);

internal sealed record RemoteSupportCaptureBackendMatrixResult(
    IReadOnlyList<RemoteSupportCaptureBackendResult> Results,
    RemoteSupportCaptureBackendResult? BestBackend,
    RemoteSupportCaptureFrame? BestFrame,
    string? OutputJpegPath);

internal sealed record RemoteSupportCaptureBackendResult
{
    public string BackendName { get; init; } = string.Empty;
    public bool BackendAvailable { get; init; }
    public bool BackendInitSucceeded { get; init; }
    public string? BackendInitError { get; init; }
    public int ProcessSessionId { get; init; }
    public uint ActiveConsoleSessionId { get; init; }
    public uint TargetSessionId { get; init; }
    public string? ProcessWindowStation { get; init; }
    public string? TargetWindowStation { get; init; }
    public string? TargetDesktop { get; init; }
    public string? ThreadDesktopBefore { get; init; }
    public string? ThreadDesktopAfter { get; init; }
    public bool SetProcessWindowStationSucceeded { get; init; }
    public bool SetThreadDesktopSucceeded { get; init; }
    public string? SourceBounds { get; init; }
    public bool FirstFrameAttempted { get; init; }
    public bool FirstFrameSucceeded { get; init; }
    public int? FirstFrameBytes { get; init; }
    public string? FirstFrameSize { get; init; }
    public int? Win32Error { get; init; }
    public int? HResult { get; init; }
    public string? Exception { get; init; }
}

internal static class RemoteSupportCaptureBackendMatrix
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    [SupportedOSPlatform("windows")]
    public static RemoteSupportCaptureBackendMatrixResult Run(
        string sessionId,
        int maxWidth,
        int maxHeight,
        bool writeSuccessfulJpeg)
    {
        var attempts = new Func<int, int, (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame)>[]
        {
            TryGdiCopyFromScreen,
            TryWin32DesktopDcBitBlt,
            TryWin32WindowStationDesktopBitBlt,
            TryDxgiOutputDuplication
        };
        var results = new List<RemoteSupportCaptureBackendResult>();
        RemoteSupportCaptureFrame? bestFrame = null;
        RemoteSupportCaptureBackendResult? best = null;
        foreach (var attempt in attempts)
        {
            var (result, frame) = attempt(maxWidth, maxHeight);
            results.Add(result);
            LogManager.WriteLog($"[RemoteSupportCaptureMatrix] session={sessionId} backend={result.BackendName} available={result.BackendAvailable} init={result.BackendInitSucceeded} firstFrame={result.FirstFrameSucceeded} win32={result.Win32Error?.ToString() ?? "<none>"} hresult={result.HResult?.ToString() ?? "<none>"} error={result.Exception ?? result.BackendInitError ?? "<none>"}");
            if (best is null && result.FirstFrameSucceeded && frame is not null)
            {
                best = result;
                bestFrame = frame;
            }
        }

        string? outputPath = null;
        if (writeSuccessfulJpeg && bestFrame is not null)
        {
            outputPath = RemoteSupportConsoleHelper.GetCaptureSelfTestPath();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            RemoteSupportBitmapCapture.WriteJpeg(bestFrame.Bgr, bestFrame.FrameWidth, bestFrame.FrameHeight, outputPath);
        }

        var matrix = new RemoteSupportCaptureBackendMatrixResult(results, best, bestFrame, outputPath);
        var isSelfTest = sessionId.Contains("self-test", StringComparison.OrdinalIgnoreCase);
        if (!isSelfTest)
        {
            var last = best ?? results.LastOrDefault();
            RemoteSupportDiagnosticState.UpdateCaptureProvider(state =>
            {
                state.CaptureProviderName = nameof(ConsoleSecureDesktopCaptureProvider);
                state.CaptureProviderSelected = true;
                state.CaptureProviderBackendName = best?.BackendName ?? "none";
                state.CaptureProviderBestBackend = best?.BackendName ?? "none";
                state.CaptureProviderFailedBackends = string.Join(",", results.Where(x => !x.FirstFrameSucceeded).Select(x => x.BackendName));
                state.CaptureProviderInitStarted = DateTimeOffset.UtcNow;
                state.CaptureProviderInitSucceeded = best is not null;
                state.CaptureProviderInitFailed = best is null;
                state.CaptureProviderInitError = best is null ? "No console capture backend produced a frame." : null;
                state.CaptureProviderDesktopName = last?.TargetDesktop;
                state.CaptureProviderSessionId = Process.GetCurrentProcess().SessionId.ToString();
                state.CaptureProviderActiveConsoleSessionId = last?.ActiveConsoleSessionId.ToString();
                state.CaptureProviderThreadDesktopBefore = last?.ThreadDesktopBefore;
                state.CaptureProviderThreadDesktopAfter = last?.ThreadDesktopAfter;
                state.CaptureProviderSetThreadDesktopSucceeded = last?.SetThreadDesktopSucceeded ?? false;
                state.CaptureProviderLastFrameAttemptUtc = DateTimeOffset.UtcNow;
                state.CaptureProviderLastFrameError = best is null ? string.Join("; ", results.Select(x => $"{x.BackendName}: {x.Exception ?? x.BackendInitError ?? "failed"}")) : null;
                state.CaptureProviderConsecutiveFailures = best is null ? 1 : 0;
                state.CaptureProviderLastSuccessfulFrameUtc = best is null ? null : DateTimeOffset.UtcNow;
                state.CaptureProviderLastWin32Error = last?.Win32Error;
                state.CaptureProviderMatrixJson = JsonSerializer.Serialize(results, Json);
                state.RemoteSupportCaptureSupported = best is not null;
                state.RemoteSupportFirstFrameDelivered = best is not null;
            });
        }

        return matrix;
    }

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) TryGdiCopyFromScreen(int maxWidth, int maxHeight) =>
        RunOnFreshThread(nameof(GdiCopyFromScreenCaptureProvider), maxWidth, maxHeight, attachWindowStation: false, attachDesktop: false, useCopyFromScreen: true);

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) TryWin32DesktopDcBitBlt(int maxWidth, int maxHeight) =>
        RunOnFreshThread(nameof(Win32DesktopDcBitBltCaptureProvider), maxWidth, maxHeight, attachWindowStation: false, attachDesktop: true, useCopyFromScreen: false);

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) TryWin32WindowStationDesktopBitBlt(int maxWidth, int maxHeight) =>
        RunOnFreshThread(nameof(Win32WindowStationDesktopBitBltCaptureProvider), maxWidth, maxHeight, attachWindowStation: true, attachDesktop: true, useCopyFromScreen: false);

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) TryDxgiOutputDuplication(int maxWidth, int maxHeight)
    {
        var context = CreateBaseResult(nameof(DxgiOutputDuplicationCaptureProvider));
        return (context with
        {
            BackendAvailable = false,
            BackendInitSucceeded = false,
            BackendInitError = "DXGI output duplication backend is not linked in this client artifact.",
            HResult = unchecked((int)0x80004001)
        }, null);
    }

    [SupportedOSPlatform("windows")]
    public static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) CaptureWithBackend(
        string backendName,
        int maxWidth,
        int maxHeight) =>
        backendName switch
        {
            nameof(GdiCopyFromScreenCaptureProvider) => TryGdiCopyFromScreen(maxWidth, maxHeight),
            nameof(Win32DesktopDcBitBltCaptureProvider) => TryWin32DesktopDcBitBlt(maxWidth, maxHeight),
            nameof(Win32WindowStationDesktopBitBltCaptureProvider) => TryWin32WindowStationDesktopBitBlt(maxWidth, maxHeight),
            nameof(DxgiOutputDuplicationCaptureProvider) => TryDxgiOutputDuplication(maxWidth, maxHeight),
            _ => (CreateBaseResult(backendName) with
            {
                BackendAvailable = false,
                BackendInitSucceeded = false,
                BackendInitError = $"Unknown console capture backend '{backendName}'.",
                HResult = unchecked((int)0x80004005)
            }, null)
        };

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) RunOnFreshThread(
        string backendName,
        int maxWidth,
        int maxHeight,
        bool attachWindowStation,
        bool attachDesktop,
        bool useCopyFromScreen)
    {
        RemoteSupportCaptureBackendResult? result = null;
        RemoteSupportCaptureFrame? frame = null;
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                (result, frame) = RunBackendOnCurrentThread(backendName, maxWidth, maxHeight, attachWindowStation, attachDesktop, useCopyFromScreen);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        })
        {
            IsBackground = true,
            Name = backendName
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        if (thread.IsAlive)
        {
            return (CreateBaseResult(backendName) with
            {
                BackendAvailable = true,
                BackendInitSucceeded = false,
                BackendInitError = "Backend timed out.",
                HResult = unchecked((int)0x80131505)
            }, null);
        }

        if (threadException is not null)
        {
            return (CreateBaseResult(backendName) with
            {
                BackendAvailable = true,
                BackendInitSucceeded = false,
                BackendInitError = threadException.Message,
                Exception = threadException.ToString(),
                HResult = threadException.HResult,
                Win32Error = Marshal.GetLastWin32Error()
            }, null);
        }

        return (result ?? CreateBaseResult(backendName), frame);
    }

    [SupportedOSPlatform("windows")]
    private static (RemoteSupportCaptureBackendResult Result, RemoteSupportCaptureFrame? Frame) RunBackendOnCurrentThread(
        string backendName,
        int maxWidth,
        int maxHeight,
        bool attachWindowStation,
        bool attachDesktop,
        bool useCopyFromScreen)
    {
        var processWindowStation = Native.GetProcessWindowStationName();
        var originalWindowStation = Native.GetProcessWindowStation();
        var targetWindowStation = processWindowStation;
        var setProcessWindowStationSucceeded = false;
        var setThreadDesktopSucceeded = false;
        var threadDesktopBefore = Native.GetCurrentThreadDesktopName();
        var threadDesktopAfter = threadDesktopBefore;
        var targetDesktop = threadDesktopBefore;
        var win32Error = 0;
        IntPtr openedWindowStation = IntPtr.Zero;
        IntPtr openedDesktop = IntPtr.Zero;
        try
        {
            if (attachWindowStation)
            {
                openedWindowStation = Native.OpenWindowStation("WinSta0", false, Native.WinstaAllAccess);
                win32Error = Marshal.GetLastWin32Error();
                targetWindowStation = openedWindowStation == IntPtr.Zero ? "WinSta0" : Native.GetWindowStationName(openedWindowStation);
                if (openedWindowStation != IntPtr.Zero)
                {
                    setProcessWindowStationSucceeded = Native.SetProcessWindowStation(openedWindowStation);
                    win32Error = Marshal.GetLastWin32Error();
                }
            }

            if (attachDesktop)
            {
                openedDesktop = Native.TryOpenInputDesktop(out var inputName, out var inputError);
                win32Error = inputError;
                targetDesktop = inputName;
                if (openedDesktop == IntPtr.Zero)
                {
                    openedDesktop = Native.TryOpenDesktop("Winlogon", out var winlogonName, out var winlogonError);
                    win32Error = winlogonError;
                    targetDesktop = winlogonName ?? "Winlogon";
                }

                if (openedDesktop != IntPtr.Zero)
                {
                    setThreadDesktopSucceeded = Native.SetThreadDesktop(openedDesktop);
                    win32Error = Marshal.GetLastWin32Error();
                }
            }

            threadDesktopAfter = Native.GetCurrentThreadDesktopName();
            var sourceWidth = Math.Max(1, Native.GetSystemMetrics(0));
            var sourceHeight = Math.Max(1, Native.GetSystemMetrics(1));
            var (frameWidth, frameHeight) = RemoteSupportCaptureScaling.ScaleToFit(sourceWidth, sourceHeight, maxWidth, maxHeight);
            try
            {
                var bgr = useCopyFromScreen
                    ? RemoteSupportBitmapCapture.CaptureWithCopyFromScreen(sourceWidth, sourceHeight, frameWidth, frameHeight)
                    : RemoteSupportBitmapCapture.CaptureWithDesktopDc(sourceWidth, sourceHeight, frameWidth, frameHeight, out win32Error);
                var frame = new RemoteSupportCaptureFrame(bgr, sourceWidth, sourceHeight, frameWidth, frameHeight, backendName);
                return (CreateBaseResult(backendName) with
                {
                    BackendAvailable = true,
                    BackendInitSucceeded = true,
                    ProcessWindowStation = processWindowStation,
                    TargetWindowStation = targetWindowStation,
                    TargetDesktop = targetDesktop,
                    ThreadDesktopBefore = threadDesktopBefore,
                    ThreadDesktopAfter = threadDesktopAfter,
                    SetProcessWindowStationSucceeded = setProcessWindowStationSucceeded,
                    SetThreadDesktopSucceeded = setThreadDesktopSucceeded,
                    SourceBounds = $"{sourceWidth}x{sourceHeight}",
                    FirstFrameAttempted = true,
                    FirstFrameSucceeded = true,
                    FirstFrameBytes = bgr.Length,
                    FirstFrameSize = $"{frameWidth}x{frameHeight}",
                    Win32Error = win32Error,
                    HResult = 0
                }, frame);
            }
            catch (Exception ex)
            {
                win32Error = ex is Win32Exception win32 ? win32.NativeErrorCode : Marshal.GetLastWin32Error();
                return (CreateBaseResult(backendName) with
                {
                    BackendAvailable = true,
                    BackendInitSucceeded = true,
                    ProcessWindowStation = processWindowStation,
                    TargetWindowStation = targetWindowStation,
                    TargetDesktop = targetDesktop,
                    ThreadDesktopBefore = threadDesktopBefore,
                    ThreadDesktopAfter = threadDesktopAfter,
                    SetProcessWindowStationSucceeded = setProcessWindowStationSucceeded,
                    SetThreadDesktopSucceeded = setThreadDesktopSucceeded,
                    SourceBounds = $"{sourceWidth}x{sourceHeight}",
                    FirstFrameAttempted = true,
                    FirstFrameSucceeded = false,
                    Win32Error = win32Error,
                    HResult = ex.HResult,
                    Exception = ex.Message
                }, null);
            }
        }
        finally
        {
            if (originalWindowStation != IntPtr.Zero && attachWindowStation)
            {
                Native.SetProcessWindowStation(originalWindowStation);
            }

            if (openedWindowStation != IntPtr.Zero)
            {
                Native.CloseWindowStation(openedWindowStation);
            }

            if (openedDesktop != IntPtr.Zero && !setThreadDesktopSucceeded)
            {
                Native.CloseDesktop(openedDesktop);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static RemoteSupportCaptureBackendResult CreateBaseResult(string backendName) => new()
    {
        BackendName = backendName,
        ProcessSessionId = Process.GetCurrentProcess().SessionId,
        ActiveConsoleSessionId = Native.WTSGetActiveConsoleSessionId(),
        TargetSessionId = Native.WTSGetActiveConsoleSessionId(),
        ProcessWindowStation = Native.GetProcessWindowStationName(),
        ThreadDesktopBefore = Native.GetCurrentThreadDesktopName()
    };
}

internal sealed class GdiCopyFromScreenCaptureProvider
{
}

internal sealed class Win32DesktopDcBitBltCaptureProvider
{
}

internal sealed class Win32WindowStationDesktopBitBltCaptureProvider
{
}

internal sealed class DxgiOutputDuplicationCaptureProvider
{
}

internal static class RemoteSupportCaptureScaling
{
    public static (int Width, int Height) ScaleToFit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        scale = Math.Min(1d, Math.Max(0.1d, scale));
        var width = Math.Max(2, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(2, (int)Math.Round(sourceHeight * scale));
        if (width % 2 != 0)
        {
            width--;
        }

        if (height % 2 != 0)
        {
            height--;
        }

        return (Math.Max(2, width), Math.Max(2, height));
    }
}

internal static class RemoteSupportBitmapCapture
{
    [SupportedOSPlatform("windows")]
    public static byte[] CaptureWithCopyFromScreen(int sourceWidth, int sourceHeight, int frameWidth, int frameHeight)
    {
        using var source = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(sourceWidth, sourceHeight), CopyPixelOperation.SourceCopy);
        }

        using var scaled = Scale(source, frameWidth, frameHeight);
        return ToBgr(scaled);
    }

    [SupportedOSPlatform("windows")]
    public static byte[] CaptureWithDesktopDc(int sourceWidth, int sourceHeight, int frameWidth, int frameHeight, out int win32Error)
    {
        win32Error = 0;
        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            win32Error = Marshal.GetLastWin32Error();
            throw new Win32Exception(win32Error, "GetDC(NULL) failed.");
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldObject = IntPtr.Zero;
        try
        {
            memoryDc = Native.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                win32Error = Marshal.GetLastWin32Error();
                throw new Win32Exception(win32Error, "CreateCompatibleDC failed.");
            }

            bitmap = Native.CreateCompatibleBitmap(screenDc, sourceWidth, sourceHeight);
            if (bitmap == IntPtr.Zero)
            {
                win32Error = Marshal.GetLastWin32Error();
                throw new Win32Exception(win32Error, "CreateCompatibleBitmap failed.");
            }

            oldObject = Native.SelectObject(memoryDc, bitmap);
            if (oldObject == IntPtr.Zero)
            {
                win32Error = Marshal.GetLastWin32Error();
                throw new Win32Exception(win32Error, "SelectObject failed.");
            }

            if (!Native.BitBlt(memoryDc, 0, 0, sourceWidth, sourceHeight, screenDc, 0, 0, Native.SRCCOPY))
            {
                win32Error = Marshal.GetLastWin32Error();
                throw new Win32Exception(win32Error, "BitBlt failed.");
            }

            using var source = Image.FromHbitmap(bitmap);
            using var scaled = Scale(source, frameWidth, frameHeight);
            return ToBgr(scaled);
        }
        finally
        {
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                Native.SelectObject(memoryDc, oldObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                Native.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                Native.DeleteDC(memoryDc);
            }

            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void WriteJpeg(byte[] bgr, int width, int height, string path)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            var rowBytes = width * 3;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bgr, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Jpeg);
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap Scale(Image source, int frameWidth, int frameHeight)
    {
        var scaled = new Bitmap(frameWidth, frameHeight, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.DrawImage(source, 0, 0, frameWidth, frameHeight);
        return scaled;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ToBgr(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            var rowBytes = bitmap.Width * 3;
            var buffer = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourcePtr = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(sourcePtr, buffer, y * rowBytes, rowBytes);
            }

            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

internal static partial class Native
{
    public const int SRCCOPY = 0x00CC0020;
    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_HOOKCONTROL = 0x0008;
    private const uint DESKTOP_JOURNALRECORD = 0x0010;
    private const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    public const uint WinstaAllAccess = 0x0000037F;
    private const uint DesktopAccess =
        DESKTOP_READOBJECTS |
        DESKTOP_CREATEWINDOW |
        DESKTOP_WRITEOBJECTS |
        DESKTOP_SWITCHDESKTOP |
        DESKTOP_ENUMERATE |
        DESKTOP_HOOKCONTROL |
        DESKTOP_JOURNALRECORD |
        DESKTOP_JOURNALPLAYBACK;

    [SupportedOSPlatform("windows")]
    public static string? GetCurrentThreadDesktopName()
    {
        var desktop = GetCurrentThreadDesktopHandle();
        return desktop == IntPtr.Zero ? null : GetDesktopName(desktop);
    }

    [SupportedOSPlatform("windows")]
    public static IntPtr GetCurrentThreadDesktopHandle() => GetThreadDesktop(GetCurrentThreadId());

    [SupportedOSPlatform("windows")]
    public static bool TryProbeCursorAccess(out int error)
    {
        Marshal.SetLastPInvokeError(0);
        if (!GetCursorPos(out var point))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        var result = SetCursorPos(point.X, point.Y);
        error = result ? 0 : Marshal.GetLastPInvokeError();
        return result;
    }

    [SupportedOSPlatform("windows")]
    public static string? GetProcessWindowStationName()
    {
        var station = GetProcessWindowStation();
        return station == IntPtr.Zero ? null : GetWindowStationName(station);
    }

    [SupportedOSPlatform("windows")]
    public static string? GetWindowStationName(IntPtr station) =>
        GetUserObjectName(station);

    [SupportedOSPlatform("windows")]
    public static IntPtr TryOpenInputDesktop(out string? desktopName, out int error)
    {
        var desktop = OpenInputDesktop(0, false, DesktopAccess);
        error = Marshal.GetLastWin32Error();
        desktopName = desktop == IntPtr.Zero ? null : GetDesktopName(desktop);
        return desktop;
    }

    [SupportedOSPlatform("windows")]
    public static IntPtr TryOpenDesktop(string name, out string? desktopName, out int error)
    {
        var desktop = OpenDesktop(name, 0, false, DesktopAccess);
        error = Marshal.GetLastWin32Error();
        desktopName = desktop == IntPtr.Zero ? null : GetDesktopName(desktop);
        return desktop;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetDesktopName(IntPtr desktop) =>
        GetUserObjectName(desktop);

    [SupportedOSPlatform("windows")]
    private static string? GetUserObjectName(IntPtr handle)
    {
        _ = GetUserObjectInformation(handle, UOI_NAME, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            return GetUserObjectInformation(handle, UOI_NAME, buffer, needed, out var actual)
                ? Marshal.PtrToStringUni(buffer, Math.Max(0, ((int)actual / 2) - 1))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, IntPtr pvInfo, uint nLength, out uint lpnLengthNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();
}
