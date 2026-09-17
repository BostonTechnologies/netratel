using System.Net.Http.Json;
using System.Security.Claims;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Notifications;

public interface INetRatelNotificationApiClient
{
    Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
        int page = 1,
        int pageSize = 20,
        string? eventType = null,
        string? correlationId = null,
        string? entityId = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? searchTerm = null,
        string? source = null,
        NetRatelNotificationSeverity? severity = null,
        CancellationToken ct = default);

    Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default);
    Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default);
    Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task RetryAsync(Guid id, CancellationToken ct = default);
}

public sealed class NetRatelNotificationApiClient(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor) : INetRatelNotificationApiClient
{
    private readonly HttpClient _http = httpClientFactory.CreateClient("OrchestratorApi");
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public async Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
        int page = 1,
        int pageSize = 20,
        string? eventType = null,
        string? correlationId = null,
        string? entityId = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? searchTerm = null,
        string? source = null,
        NetRatelNotificationSeverity? severity = null,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (!string.IsNullOrWhiteSpace(eventType)) query.Add($"eventType={Uri.EscapeDataString(eventType)}");
        if (!string.IsNullOrWhiteSpace(correlationId)) query.Add($"correlationId={Uri.EscapeDataString(correlationId)}");
        if (!string.IsNullOrWhiteSpace(entityId)) query.Add($"entityId={Uri.EscapeDataString(entityId)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (from.HasValue) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(searchTerm)) query.Add($"search={Uri.EscapeDataString(searchTerm)}");
        if (!string.IsNullOrWhiteSpace(source)) query.Add($"source={Uri.EscapeDataString(source)}");
        if (severity.HasValue) query.Add($"severity={Uri.EscapeDataString(severity.Value.ToString())}");

        using var request = CreateRequest(HttpMethod.Get, $"/api/v1/notifications?{string.Join("&", query)}");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new PagedResult<NetRatelNotificationDto>(Array.Empty<NetRatelNotificationDto>(), page, pageSize, 0);

        return await response.Content.ReadFromJsonAsync<PagedResult<NetRatelNotificationDto>>(cancellationToken: ct)
            ?? new PagedResult<NetRatelNotificationDto>(Array.Empty<NetRatelNotificationDto>(), page, pageSize, 0);
    }

    public async Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty)
            return null;

        using var request = CreateRequest(HttpMethod.Get, $"/api/v1/notifications/{id}");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<NetRatelNotificationDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/api/v1/notifications/unread-errors?take={take}");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<NetRatelNotificationDto>();

        return await response.Content.ReadFromJsonAsync<List<NetRatelNotificationDto>>(cancellationToken: ct)
            ?? new List<NetRatelNotificationDto>();
    }

    public async Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/v1/notifications/summary");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new NetRatelNotificationSummaryDto();

        return await response.Content.ReadFromJsonAsync<NetRatelNotificationSummaryDto>(cancellationToken: ct)
            ?? new NetRatelNotificationSummaryDto();
    }

    public async Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var valid = ids.Where(x => x != Guid.Empty).Distinct().ToList();
        if (valid.Count == 0)
            return 0;

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/notifications/mark-read");
        request.Content = JsonContent.Create(new { ids = valid });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<BulkMarkReadResult>(cancellationToken: ct);
        return payload?.Updated ?? 0;
    }

    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty)
            return;

        using var request = CreateRequest(HttpMethod.Post, $"/api/v1/events/{id}/retry");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var userId = ResolveUserId();
        if (!string.IsNullOrWhiteSpace(userId))
            request.Headers.TryAddWithoutValidation("X-NetRatel-UserId", userId);

        return request;
    }

    private string? ResolveUserId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null)
            return null;

        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("preferred_username")
            ?? user.Identity?.Name;
    }

    private sealed record BulkMarkReadResult(int Updated);
}
