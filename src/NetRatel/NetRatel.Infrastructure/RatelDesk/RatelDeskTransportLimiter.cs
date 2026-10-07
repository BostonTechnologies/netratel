namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Fixed memory bounds: eight operations globally, two per tenant, one per connector (stripes may conservatively share a slot).</summary>
public sealed class RatelDeskTransportLimiter
{
    private readonly SemaphoreSlim _global = new(8, 8);
    private readonly SemaphoreSlim[] _tenants = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(2, 2)).ToArray();
    private readonly SemaphoreSlim[] _connectors = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable?> TryAcquireAsync(int tenantId, Guid connectorId, CancellationToken cancellationToken)
    {
        if (!await _global.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        var tenant = _tenants[(uint)tenantId % (uint)_tenants.Length];
        var slot = _connectors[(uint)HashCode.Combine(tenantId, connectorId) % (uint)_connectors.Length];
        var tenantAcquired = false;
        try
        {
            tenantAcquired = await tenant.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (tenantAcquired && await slot.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new Lease(slot, tenant, _global);
            if (tenantAcquired) tenant.Release();
            _global.Release(); return null;
        }
        catch { if (tenantAcquired) tenant.Release(); _global.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim slot, SemaphoreSlim tenant, SemaphoreSlim global) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { slot.Release(); tenant.Release(); global.Release(); } }
    }
}
