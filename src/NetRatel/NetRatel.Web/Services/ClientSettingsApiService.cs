using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services;

public class ClientSettingsApiService
{
    private readonly IHttpClientFactory _factory;

    public ClientSettingsApiService(IHttpClientFactory factory) => _factory = factory;

    private HttpClient Create() => _factory.CreateClient("OrchestratorApi");

    public async Task<ClientSettingsDto> GetAsync(CancellationToken ct = default)
        => await Create().GetFromJsonAsync<ClientSettingsDto>("/api/v1/clientsettings", ct)
           ?? new ClientSettingsDto(15, false, null, null);

    public async Task UpdateAsync(UpdateClientSettingsRequest req, CancellationToken ct = default)
    {
        var res = await Create().PutAsJsonAsync("/api/v1/clientsettings", req, ct);
        res.EnsureSuccessStatusCode();
    }
}
