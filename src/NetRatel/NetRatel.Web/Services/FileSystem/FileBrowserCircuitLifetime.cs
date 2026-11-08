using Microsoft.AspNetCore.Components.Server.Circuits;

namespace NetRatel.Web.Services.FileSystem;

/// <summary>Native work and private content leases end when their circuit disconnects.</summary>
public sealed class FileBrowserCircuitLifetime : CircuitHandler, IDisposable
{
    private CancellationTokenSource _connection = new();
    private int _disposed;
    public CancellationToken Token => _connection.Token;
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    { _connection.Cancel(); return Task.CompletedTask; }
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_connection.IsCancellationRequested) { _connection.Dispose(); _connection = new(); }
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connection.Cancel(); _connection.Dispose();
    }
}
