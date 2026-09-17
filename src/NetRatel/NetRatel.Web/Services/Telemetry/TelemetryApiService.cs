using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts;

namespace NetRatel.Web.Services.Telemetry;

public sealed class TelemetryApiService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TelemetryApiService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<AgentTelemetrySnapshotDto>> GetOverviewAsync(CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        return await client.GetFromJsonAsync<List<AgentTelemetrySnapshotDto>>("/api/v1/telemetry/overview", ct)
            ?? new List<AgentTelemetrySnapshotDto>();
    }

    public async Task<AgentTelemetrySnapshotDto?> GetClientSnapshotAsync(string clientIdentity, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("OrchestratorApi");
        return await client.GetFromJsonAsync<AgentTelemetrySnapshotDto>($"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/telemetry", ct);
    }
}
