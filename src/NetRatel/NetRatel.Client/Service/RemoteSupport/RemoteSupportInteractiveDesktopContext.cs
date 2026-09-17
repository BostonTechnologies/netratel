using System;
using System.Runtime.Versioning;
using System.Threading;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed class RemoteSupportDesktopContextCoordinator
{
    private long _generation = 1;
    private string? _lastReason;

    public long Generation => Interlocked.Read(ref _generation);
    public string? LastReason => Volatile.Read(ref _lastReason);

    public long Invalidate(string reason)
    {
        Volatile.Write(ref _lastReason, reason);
        return Interlocked.Increment(ref _generation);
    }
}

internal sealed record RemoteSupportDesktopContextSnapshot(
    bool Ready,
    long Generation,
    string? DesktopName,
    int Win32Error,
    string Status,
    DateTimeOffset? LastBoundAt,
    DateTimeOffset? LastProbeAt,
    string? LastInvalidationReason,
    int WorkerThreadId);

internal interface IRemoteSupportDesktopContextNative
{
    IntPtr GetCurrentThreadDesktop();
    string? GetCurrentThreadDesktopName();
    IntPtr OpenInputDesktop(out string? desktopName, out int error);
    IntPtr OpenDefaultDesktop(out string? desktopName, out int error);
    bool SetThreadDesktop(IntPtr desktop, out int error);
    bool CloseDesktop(IntPtr desktop, out int error);
    bool ProbeInputDesktop(out int error);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRemoteSupportDesktopContextNative : IRemoteSupportDesktopContextNative
{
    public IntPtr GetCurrentThreadDesktop() => Native.GetCurrentThreadDesktopHandle();

    public string? GetCurrentThreadDesktopName() => Native.GetCurrentThreadDesktopName();

    public IntPtr OpenInputDesktop(out string? desktopName, out int error) =>
        Native.TryOpenInputDesktop(out desktopName, out error);

    public IntPtr OpenDefaultDesktop(out string? desktopName, out int error) =>
        Native.TryOpenDesktop("Default", out desktopName, out error);

    public bool SetThreadDesktop(IntPtr desktop, out int error)
    {
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        var result = Native.SetThreadDesktop(desktop);
        error = result ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return result;
    }

    public bool CloseDesktop(IntPtr desktop, out int error)
    {
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
        var result = Native.CloseDesktop(desktop);
        error = result ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return result;
    }

    public bool ProbeInputDesktop(out int error) => Native.TryProbeCursorAccess(out error);
}

internal sealed class RemoteSupportInteractiveDesktopContext : IDisposable
{
    internal static readonly TimeSpan ProbeFreshness = TimeSpan.FromSeconds(2);

    private readonly IRemoteSupportDesktopContextNative _native;
    private readonly RemoteSupportDesktopContextCoordinator _coordinator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _requireInputProbe;
    private readonly int _workerThreadId;
    private IntPtr _originalDesktop;
    private IntPtr _activeDesktop;
    private long _boundGeneration;
    private bool _ready;
    private bool _disposed;
    private int _lastError;
    private string? _desktopName;
    private string _status = "desktop_context_not_bound";
    private DateTimeOffset? _lastBoundAt;
    private DateTimeOffset? _lastProbeAt;
    private string? _lastInvalidationReason;

    public RemoteSupportInteractiveDesktopContext(
        IRemoteSupportDesktopContextNative native,
        RemoteSupportDesktopContextCoordinator coordinator,
        bool requireInputProbe,
        Func<DateTimeOffset>? utcNow = null)
    {
        _native = native;
        _coordinator = coordinator;
        _requireInputProbe = requireInputProbe;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _workerThreadId = Environment.CurrentManagedThreadId;
        _originalDesktop = _native.GetCurrentThreadDesktop();
    }

    public bool NeedsRefresh =>
        !_ready ||
        _boundGeneration != _coordinator.Generation ||
        !_lastProbeAt.HasValue ||
        _utcNow() - _lastProbeAt.Value >= ProbeFreshness;

    public RemoteSupportDesktopContextSnapshot Snapshot => new(
        _ready,
        _boundGeneration,
        _desktopName,
        _lastError,
        _status,
        _lastBoundAt,
        _lastProbeAt,
        _lastInvalidationReason,
        _workerThreadId);

    public RemoteSupportDesktopContextSnapshot EnsureReady(bool forceRebind = false)
    {
        ThrowIfDisposed();
        if (!forceRebind && _ready && _boundGeneration == _coordinator.Generation)
        {
            if (_lastProbeAt.HasValue && _utcNow() - _lastProbeAt.Value < ProbeFreshness)
            {
                return Snapshot;
            }

            if (Probe())
            {
                return Snapshot;
            }
        }

        return Rebind();
    }

    public void Invalidate(string reason, bool publishGeneration = true)
    {
        _ready = false;
        _status = "desktop_context_invalidated";
        _lastInvalidationReason = reason;
        if (publishGeneration)
        {
            _coordinator.Invalidate(reason);
        }
    }

    private RemoteSupportDesktopContextSnapshot Rebind()
    {
        var targetGeneration = _coordinator.Generation;
        var replacement = _native.OpenInputDesktop(out var desktopName, out var openError);
        if (replacement == IntPtr.Zero)
        {
            replacement = _native.OpenDefaultDesktop(out desktopName, out openError);
        }

        if (replacement == IntPtr.Zero)
        {
            SetFailure("input_desktop_open_failed", openError);
            return Snapshot;
        }

        if (!_native.SetThreadDesktop(replacement, out var attachError))
        {
            _native.CloseDesktop(replacement, out _);
            SetFailure("input_desktop_attach_failed", attachError);
            return Snapshot;
        }

        var previous = _activeDesktop;
        _activeDesktop = replacement;
        _desktopName = desktopName ?? _native.GetCurrentThreadDesktopName();
        _boundGeneration = targetGeneration;
        _lastBoundAt = _utcNow();
        _lastError = 0;
        _status = "desktop_context_bound";
        _ready = true;

        if (previous != IntPtr.Zero && previous != replacement)
        {
            _native.CloseDesktop(previous, out _);
        }

        if (!Probe())
        {
            return Snapshot;
        }

        _status = "desktop_context_ready";
        return Snapshot;
    }

    private bool Probe()
    {
        var ready = true;
        _lastError = 0;
        if (_requireInputProbe)
        {
            ready = _native.ProbeInputDesktop(out _lastError);
        }
        _lastProbeAt = _utcNow();
        _desktopName = _native.GetCurrentThreadDesktopName() ?? _desktopName;
        _ready = ready;
        _status = ready ? "desktop_context_ready" : "desktop_context_probe_failed";
        return ready;
    }

    private void SetFailure(string status, int error)
    {
        _ready = false;
        _status = status;
        _lastError = error;
        _lastProbeAt = _utcNow();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RemoteSupportInteractiveDesktopContext));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_activeDesktop == IntPtr.Zero)
        {
            return;
        }

        var detached = _originalDesktop != IntPtr.Zero && _native.SetThreadDesktop(_originalDesktop, out _);
        if (detached)
        {
            _native.CloseDesktop(_activeDesktop, out _);
        }

        _activeDesktop = IntPtr.Zero;
        _ready = false;
    }
}
