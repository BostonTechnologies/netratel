using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Services.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Endpoints.Monitoring;

internal static class MetricHistoryEndpoints
{
    public static void MapMetricHistory(this RouteGroupBuilder group)
    {
        var metrics = group.MapGroup("/metrics");
        metrics.MapGet("/agents/{agentId:guid}/catalogue", (int tenantId, Guid agentId, int? limit, string? cursor,
            HttpContext http, [FromServices] MetricHistoryApiService service, CancellationToken ct) =>
            MonitoringEndpoints.ExecuteAsync(() => service.CatalogueAsync(tenantId, agentId, limit ?? 50, cursor, http.User, ct)))
            .RequireAuthorization(MonitoringAuthorization.ReadPolicy).Produces<MetricCatalogue>();
        metrics.MapPost("/agents/{agentId:guid}/query", (int tenantId, Guid agentId, [FromBody] MetricHistoryRange range,
            HttpContext http, [FromServices] MetricHistoryApiService service, CancellationToken ct) =>
            MonitoringEndpoints.ExecuteAsync(() => service.QueryAsync(tenantId, agentId, range, http.User, ct)))
            .RequireAuthorization(MonitoringAuthorization.ReadPolicy).Produces<MetricHistoryResponse>();
        metrics.MapGet("/settings", (int tenantId, Guid? agentId, HttpContext http, [FromServices] MetricHistoryApiService service, CancellationToken ct) =>
            MonitoringEndpoints.ExecuteAsync(() => service.SettingsAsync(tenantId, agentId, http.User, ct)))
            .RequireAuthorization(MonitoringAuthorization.ReadOrManagePolicy).Produces<MetricHistorySettings>();
        metrics.MapPut("/settings", (int tenantId, [FromBody] MetricHistoryPolicyWrite write, HttpContext http,
            [FromServices] MetricHistoryApiService service, CancellationToken ct) =>
            MonitoringEndpoints.ExecuteAsync(() => service.SaveAsync(tenantId, write, http.User, ct)))
            .RequireAuthorization(MonitoringAuthorization.ManagePolicy).Produces<MetricHistorySettings>();
    }
}
