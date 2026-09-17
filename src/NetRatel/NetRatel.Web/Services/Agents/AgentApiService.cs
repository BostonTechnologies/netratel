using System.Net.Http.Json;
using System.Web;

namespace NetRatel.Web.Services.Agents;

public sealed record AgentListItem(
    int TenantId,
    Guid AgentId,
    string? DisplayName,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastTokenIssuedAtUtc);

public sealed record AgentListResponse(
    List<AgentListItem> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record AgentDetailResponse(
    int TenantId,
    Guid AgentId,
    string? DisplayName,
    bool IsEnabled,
    string? DisabledReason,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastTokenIssuedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public interface IAgentApiService
{
    Task<AgentListResponse> ListAsync(int tenantId, string? search, bool? enabled, int page, int pageSize, CancellationToken ct = default);
    Task<AgentDetailResponse?> GetAsync(int tenantId, Guid agentId, CancellationToken ct = default);
    Task DisableAsync(int tenantId, Guid agentId, string reason, CancellationToken ct = default);
    Task EnableAsync(int tenantId, Guid agentId, CancellationToken ct = default);
}

public sealed class AgentApiService : IAgentApiService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AgentApiService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<AgentListResponse> ListAsync(int tenantId, string? search, bool? enabled, int page, int pageSize, CancellationToken ct = default)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(search)) q["search"] = search;
        if (enabled.HasValue) q["enabled"] = enabled.Value.ToString().ToLowerInvariant();
        q["page"] = page.ToString();
        q["pageSize"] = pageSize.ToString();

        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var uri = $"/api/v1/tenants/{tenantId}/agents?{q}";
        return await client.GetFromJsonAsync<AgentListResponse>(uri, ct) ?? new AgentListResponse(new List<AgentListItem>(), page, pageSize, 0);
    }

    public async Task<AgentDetailResponse?> GetAsync(int tenantId, Guid agentId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        return await client.GetFromJsonAsync<AgentDetailResponse>($"/api/v1/tenants/{tenantId}/agents/{agentId}", ct);
    }

    public async Task DisableAsync(int tenantId, Guid agentId, string reason, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var response = await client.PostAsJsonAsync($"/api/v1/tenants/{tenantId}/agents/{agentId}/disable", new { reason }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnableAsync(int tenantId, Guid agentId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        var response = await client.PostAsync($"/api/v1/tenants/{tenantId}/agents/{agentId}/enable", content: null, ct);
        response.EnsureSuccessStatusCode();
    }
}
