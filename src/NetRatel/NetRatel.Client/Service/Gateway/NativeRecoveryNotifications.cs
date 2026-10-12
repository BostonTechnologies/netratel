using Microsoft.Win32;
using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Owns host event subscriptions for one operational recovery owner.</summary>
internal sealed class NativeRecoveryNotifications : IDisposable
{
    private readonly NetworkAvailabilityChangedEventHandler _networkRestored;
    private readonly PowerModeChangedEventHandler _resumed;
    private PosixSignalRegistration? _resumeSignal;
    private bool _powerNotifications;
    private int _disposed;

    private NativeRecoveryNotifications(Action notifyRecoveryHint)
    {
        _networkRestored = (_, args) =>
        {
            if (args.IsAvailable) notifyRecoveryHint();
        };
        _resumed = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) notifyRecoveryHint();
        };
    }

    internal static IDisposable Subscribe(Action notifyRecoveryHint, Action<string> log)
    {
        var subscription = new NativeRecoveryNotifications(notifyRecoveryHint);
        NetworkChange.NetworkAvailabilityChanged += subscription._networkRestored;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    SystemEvents.PowerModeChanged += subscription._resumed;
                    subscription._powerNotifications = true;
                }
                catch (InvalidOperationException)
                {
                    // Session-zero hosts may not have a desktop event source. The
                    // monotonic timer and network hint retain the same deadline.
                    log("Power notification source is unavailable; operational recovery retains its timer and network notifications.");
                }
            }
            else
            {
                subscription._resumeSignal = PosixSignalRegistration.Create(PosixSignal.SIGCONT,
                    _ => notifyRecoveryHint());
            }
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= _networkRestored;
            if (OperatingSystem.IsWindows() && _powerNotifications)
                SystemEvents.PowerModeChanged -= _resumed;
        }
        finally
        {
            _resumeSignal?.Dispose();
        }
    }
}
