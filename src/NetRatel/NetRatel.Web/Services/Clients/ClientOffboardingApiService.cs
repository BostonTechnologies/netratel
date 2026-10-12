using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

public interface IClientOffboardingApiService
{
    Task<ClientOffboardingDetailsDto> GetAsync(int tenantId, Guid agentId, CancellationToken ct = default);
    Task<ClientOffboardingDetailsDto> UninstallAsync(int tenantId, Guid agentId, ClientUninstallRequest request, CancellationToken ct = default);
    Task<ClientOffboardingDetailsDto> RemoveAsync(int tenantId, Guid agentId, ClientRemovalRequest request, CancellationToken ct = default);
}

public sealed class ClientOffboardingApiException(HttpStatusCode status, string? code)
    : HttpRequestException("The client operation was not accepted.", null, status)
{
    public string? Code { get; } = code;
}

public sealed class ClientOffboardingApiService(IHttpClientFactory factory) : IClientOffboardingApiService
{
    public Task<ClientOffboardingDetailsDto> GetAsync(int tenantId, Guid agentId, CancellationToken ct = default) =>
        SendAsync(tenantId, agentId, HttpMethod.Get, "", null, ct);

    public Task<ClientOffboardingDetailsDto> UninstallAsync(int tenantId, Guid agentId, ClientUninstallRequest request, CancellationToken ct = default) =>
        SendAsync(tenantId, agentId, HttpMethod.Post, "/uninstall", JsonContent.Create(request), ct);

    public Task<ClientOffboardingDetailsDto> RemoveAsync(int tenantId, Guid agentId, ClientRemovalRequest request, CancellationToken ct = default) =>
        SendAsync(tenantId, agentId, HttpMethod.Delete, "", JsonContent.Create(request), ct);

    private async Task<ClientOffboardingDetailsDto> SendAsync(int tenantId, Guid agentId, HttpMethod method, string suffix,
        HttpContent? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(method, $"/api/v2/agents/{tenantId}/{agentId:D}/offboarding{suffix}") { Content = body };
        using var response = await factory.CreateClient("OrchestratorApi").SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            string? code = null;
            if (content.Length is > 0 and < 8192 && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var problem = JsonDocument.Parse(content);
                if (problem.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                    code = value.GetString();
            }
            throw new ClientOffboardingApiException(response.StatusCode, code);
        }
        var result = await response.Content.ReadFromJsonAsync<ClientOffboardingDetailsDto>(cancellationToken: timeout.Token).ConfigureAwait(false);
        if (result is null || result.TenantId != tenantId || result.AgentId != agentId)
            throw new JsonException("The offboarding response did not match the selected client.");
        return result;
    }
}
