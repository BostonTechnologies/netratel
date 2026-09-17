using System.Net;
using System.Net.Http.Json;

namespace NetRatel.Web.Services.Telemetry;

/// <summary>Reads the additive Agent-ID keyed telemetry canary surface.</summary>
public sealed class GatewayTelemetryApiService(IHttpClientFactory factory)
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<GatewayTelemetrySummary>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/v2/agent-telemetry", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<GatewayTelemetrySummary>>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    public async Task<GatewayTelemetrySummary?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/telemetry", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayTelemetrySummary>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public sealed record GatewayTelemetrySummary(
    int TenantId,
    Guid AgentId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    GatewayTelemetryCpu? Cpu,
    GatewayTelemetryMemory? Memory,
    IReadOnlyList<GatewayTelemetryDisk> Disks,
    IReadOnlyList<GatewayTelemetryNetwork> Networks,
    GatewayTelemetryTransportHealth? TransportHealth,
    string Source,
    bool IsAuthoritative);

public sealed record GatewayTelemetryCpu(double UsagePercent, double? LoadAverage, int? ProcessCount);

public sealed record GatewayTelemetryMemory(double TotalMb, double UsedMb, double AvailableMb, double UsagePercent);

public sealed record GatewayTelemetryDisk(string Scope, double TotalGb, double UsedGb, double FreeGb, double UsagePercent);

public sealed record GatewayTelemetryNetwork(string Scope, double RxBytesPerSec, double TxBytesPerSec);

public sealed record GatewayTelemetryTransportHealth(long UptimeSeconds, string? AgentVersion, string? OsVersion, DateTimeOffset? LastHeartbeat);
