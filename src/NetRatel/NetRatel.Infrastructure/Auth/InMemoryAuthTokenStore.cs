using NetRatel.Shared.Abstractions;

namespace NetRatel.Infrastructure.Auth;

public sealed class InMemoryAuthTokenStore : IAuthTokenStore
{
    private readonly object _gate = new();
    private string? _token;

    public Task<string?> LoadAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(_token);
        }
    }

    public Task SaveAsync(string token)
    {
        lock (_gate)
        {
            _token = token;
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            _token = null;
        }

        return Task.CompletedTask;
    }
}
