using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.Services;

public interface INetclawApiService
{
    Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default);
    Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default);
    Task<NetclawConnectionDiagnosticsDto?> ConnectionDiagnosticsAsync(int tenantId, CancellationToken ct = default) =>
        Task.FromResult<NetclawConnectionDiagnosticsDto?>(null);
    Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default);
    Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default);
    Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default);
    async Task<InvestigationClientPageDto> SearchClientsAsync(int tenantId, string? search = null, int offset = 0, CancellationToken ct = default)
    {
        var clients = await ClientsAsync(tenantId, ct);
        var term = search?.Trim() ?? "";
        var matches = clients.Where(x => term.Length == 0 || x.HostName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            x.FriendlyName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true || x.PrimaryIpAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) == true ||
            x.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))).Skip(offset).Take(100).ToArray();
        return new(matches, null);
    }
    Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default);
    Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default);
}

public sealed class NetclawApiService(IHttpClientFactory factory) : INetclawApiService
{
    // Shares the existing authenticated, non-retrying API transport. Mutating messages keep their original request IDs.
    private readonly HttpClient http = factory.CreateClient(FlowApiService.ClientName);
    private static string Root(int tenant) => $"/api/v1/tenants/{tenant}/ai";
    private static string Item(int tenant, Guid id) => $"{Root(tenant)}/investigations/{id:D}";
    public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) => Read<IReadOnlyList<NetclawTenantDto>>("/api/v1/ai/tenants", ct);
    public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Read<NetclawConnectionDto>(Root(tenantId) + "/connection", ct);
    public async Task<NetclawConnectionDiagnosticsDto?> ConnectionDiagnosticsAsync(int tenantId, CancellationToken ct = default) =>
        await Read<NetclawConnectionDiagnosticsDto>(Root(tenantId) + "/connection/diagnostics", ct);
    public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default) => Write<NetclawTestResult>(Root(tenantId) + "/connection/pair", request, ct);
    public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) => Write<NetclawTestResult>(Root(tenantId) + "/connection/test", null, ct);
    public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => Send<NetclawConnectionDto>(HttpMethod.Put, Root(tenantId) + "/connection/enabled", request, ct);
    public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => Read<IReadOnlyList<InvestigationClientDto>>(Root(tenantId) + "/clients", ct);
    public Task<InvestigationClientPageDto> SearchClientsAsync(int tenantId, string? search = null, int offset = 0, CancellationToken ct = default) =>
        Read<InvestigationClientPageDto>(Root(tenantId) + $"/clients/search?offset={offset}&search={Uri.EscapeDataString(search ?? "")}", ct);
    public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => Read<IReadOnlyList<InvestigationSummaryDto>>(Root(tenantId) + "/investigations", ct);
    public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => Write<InvestigationSnapshotDto>(Root(tenantId) + "/investigations", request, ct);
    public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default) => Read<InvestigationSnapshotDto>(Item(tenantId, id) + $"?after={after}", ct);
    public Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default) => Write<InvestigationSnapshotDto>(Item(tenantId, id) + "/messages", request, ct);
    public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default) => Write<InvestigationSnapshotDto>(Item(tenantId, id) + "/approvals/" + Uri.EscapeDataString(callId), new InvestigationApprovalRequest(key), ct);
    public Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default) => Write<InvestigationSnapshotDto>(Item(tenantId, id) + "/reconcile", null, ct);
    public Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default) => Write<InvestigationSnapshotDto>(Item(tenantId, id) + "/archive", request, ct);
    private Task<T> Read<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, path, null, ct);
    private Task<T> Write<T>(string path, object? body, CancellationToken ct) => Send<T>(HttpMethod.Post, path, body, ct);
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch { HttpStatusCode.Forbidden => "Your current permissions do not allow this operation in this tenant.",
                HttpStatusCode.NotFound => "The investigation or connection is unavailable in this tenant.", _ => "The request failed. Reload the saved state before trying again." };
            if (response.Content.Headers.ContentLength is <= 8192)
            {
                try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token); if (error.TryGetProperty("message", out var text) && text.GetString() is { Length: > 0 and < 1024 } detail) message = detail; }
                catch (JsonException) { message = "The API returned an unreadable response. Reload the saved state."; }
            }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(timeout.Token) ?? throw new HttpRequestException("The API returned no saved state.");
    }
}
