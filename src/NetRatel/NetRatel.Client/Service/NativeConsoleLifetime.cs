using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetRatel.Client.Service;

/// <summary>Deliberate SIGTERM/SIGINT shutdown for systemd, launchd and direct container entrypoints.</summary>
internal sealed class NativeConsoleLifetime : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly PosixSignalRegistration? _terminate;
    private readonly PosixSignalRegistration? _interrupt;
    public CancellationToken Stopping => _stopping.Token;

    public NativeConsoleLifetime()
    {
        Console.CancelKeyPress += OnCancel;
        if (!OperatingSystem.IsWindows())
        {
            _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            _interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        }
    }
    private void OnSignal(PosixSignalContext context) { context.Cancel = true; _stopping.Cancel(); }
    private void OnCancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; _stopping.Cancel(); }
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        _terminate?.Dispose();
        _interrupt?.Dispose();
        _stopping.Dispose();
    }
}
