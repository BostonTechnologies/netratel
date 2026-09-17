using System.Net.Http.Json;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Clients;

public interface IGatewayLogApiService
{
    Task<IReadOnlyList<GatewayLogSourceDescriptorDto>> GetSourcesAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<GatewayLogPageDto> GetHistoryAsync(int tenantId, Guid agentId, string sourceId, string? cursor = null, bool after = false, CancellationToken cancellationToken = default, GatewayLogQueryFilters? filters = null);
}

/// <summary>Browser-safe REST access to the Agent-ID keyed gateway log read model.</summary>
public sealed class GatewayLogApiService(IHttpClientFactory factory) : IGatewayLogApiService
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<GatewayLogSourceDescriptorDto>> GetSourcesAsync(
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/logs/sources", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<GatewayLogSourceDescriptorDto>>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<GatewayLogPageDto> GetHistoryAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        string? cursor = null,
        bool after = false,
        CancellationToken cancellationToken = default,
        GatewayLogQueryFilters? filters = null)
    {
        var query = $"sourceId={Uri.EscapeDataString(sourceId)}&pageSize=100";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var typedCursor = cursor.StartsWith("before:", StringComparison.Ordinal) || cursor.StartsWith("after:", StringComparison.Ordinal)
                ? cursor
                : $"{(after ? "after" : "before")}:{cursor}";
            query += $"&cursor={Uri.EscapeDataString(typedCursor)}";
        }
        if (filters?.FromUtc is { } from) query += $"&fromUtc={Uri.EscapeDataString(from.ToString("O"))}";
        if (filters?.ToUtc is { } to) query += $"&toUtc={Uri.EscapeDataString(to.ToString("O"))}";
        if (filters?.Text is { Length: > 0 } text) query += $"&text={Uri.EscapeDataString(text)}";
        foreach (var severity in filters?.Severities ?? []) query += $"&severity={Uri.EscapeDataString(severity)}";
        foreach (var prefix in filters?.Prefixes ?? []) query += $"&prefix={Uri.EscapeDataString(prefix)}";
        foreach (var category in filters?.Categories ?? []) query += $"&category={Uri.EscapeDataString(category)}";
        foreach (var provider in filters?.Providers ?? []) query += $"&provider={Uri.EscapeDataString(provider)}";
        foreach (var eventId in filters?.EventIds ?? []) query += $"&eventId={eventId}";
        using var response = await _http.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/logs/history?{query}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayLogPageDto>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway log API returned an empty page.");
    }
}
