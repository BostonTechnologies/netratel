using Microsoft.Extensions.Caching.Memory;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class ProviderAccessToken(string header, string headerValue, TimeSpan lifetime)
{
    public string Header { get; } = header;
    public string HeaderValue { get; } = headerValue;
    public TimeSpan Lifetime { get; } = lifetime;
}

internal sealed class SecretProviderTokenCache(TimeProvider clock) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly object _gate = new();

    public async Task<ProviderAccessToken> GetAsync(SecretProviderContext context, Func<CancellationToken, Task<ProviderAccessToken>> login, CancellationToken ct)
    {
        Slot slot;
        lock (_gate)
        {
            slot = _cache.GetOrCreate(Key(context), entry =>
            {
                entry.Size = 1;
                entry.SlidingExpiration = TimeSpan.FromMinutes(15);
                return new Slot();
            })!;
        }

        await slot.Gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (slot.Token is { } token && slot.ExpiresAtUtc > now)
                return token;
            var fresh = await login(ct);
            slot.Token = fresh;
            slot.ExpiresAtUtc = now + fresh.Lifetime - TimeSpan.FromSeconds(10);
            return fresh;
        }
        finally { slot.Gate.Release(); }
    }

    public void Invalidate(SecretProviderContext context) => _cache.Remove(Key(context));
    public void Dispose() => _cache.Dispose();

    private static (int, Guid, long) Key(SecretProviderContext context) => (context.TenantId, context.ConnectionId, context.AuthenticationRevision);

    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ProviderAccessToken? Token { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
