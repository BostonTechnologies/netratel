using System.Threading.Tasks;

namespace NetRatel.Shared.Abstractions;

public interface IAuthTokenStore
{
    Task<string?> LoadAsync();
    Task SaveAsync(string token);
    Task ClearAsync();
}
