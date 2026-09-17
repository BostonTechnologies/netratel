using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

public interface IClientApiService
{
    Task<PagedResult<ClientDto>> GetClientsAsync(
        int tenantId,
        ClientEnvironment environment,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default);
}
