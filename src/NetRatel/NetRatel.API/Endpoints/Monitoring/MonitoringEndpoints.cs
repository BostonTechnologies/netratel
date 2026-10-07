using Microsoft.AspNetCore.Mvc;
using System.Collections.Immutable;
using NetRatel.API.Services.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Monitoring;

public static class MonitoringEndpoints
{
    private static readonly HashSet<string> CapacityErrors = new(StringComparer.Ordinal)
    {
        "monitoring_configuration_size_exceeded", "monitoring_client_state_size_exceeded",
        "monitoring_mutation_capacity_exceeded", "service_watch_capacity_exceeded",
        "series_capacity_exceeded", "eligible_agent_capacity_exceeded"
    };
    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/monitoring/tenants", (HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(async () => MonitoringFlowPermissionAuthorization.RestrictDiscoveryRows(
                http, NetRatelPermissions.MonitoringRead,
                await service.GetTenantsAsync(http.User, ct).ConfigureAwait(false), tenant => tenant.TenantId)))
            .RequireAuthorization(MonitoringAuthorization.DiscoveryPolicy).WithTags("Monitoring").WithMetadata(new MonitoringHttpBounds());
        var group = app.MapGroup("/api/v2/tenants/{tenantId:int}/monitoring").WithTags("Monitoring").WithMetadata(new MonitoringHttpBounds());
        group.MapGet("/permissions", (int tenantId, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetPermissionsAsync(tenantId, http.User, ct))).RequireAuthorization(MonitoringAuthorization.PermissionsSummaryPolicy);
        group.MapGet("/configuration", (int tenantId, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetConfigurationAsync(tenantId, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadPolicy);
        group.MapGet("/summary", (int tenantId, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetSummaryAsync(tenantId, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadPolicy);
        group.MapGet("/series", (int tenantId, HttpContext http, MonitoringApiService service, int? maximumCount, string? cursor, CancellationToken ct) =>
            ExecuteAsync(() => service.GetSeriesAsync(tenantId, maximumCount ?? 50, cursor, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadPolicy);
        group.MapGet("/events", (int tenantId, HttpContext http, MonitoringApiService service, int? maximumCount, string? cursor, CancellationToken ct) =>
            ExecuteAsync(() => service.GetEventsAsync(tenantId, maximumCount ?? 50, cursor, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadPolicy);
        group.MapGet("/published-flows", (int tenantId, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetPublishedFlowsAsync(tenantId, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadOrManagePolicy);
        group.MapGet("/clients", (int tenantId, HttpContext http, MonitoringApiService service, int? maximumCount, string? cursor, string? search, CancellationToken ct) =>
            ExecuteAsync(() => service.GetClientsAsync(tenantId, maximumCount ?? 25, cursor, http.User, ct, search))).RequireAuthorization(MonitoringAuthorization.ReadOrManagePolicy);
        group.MapPost("/clients/identities", (int tenantId, [FromBody] ImmutableArray<Guid> ids, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetClientIdentitiesAsync(tenantId, ids, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadOrManagePolicy);
        group.MapGet("/agents/{agentId:guid}/series", (int tenantId, Guid agentId, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.GetClientAsync(tenantId, agentId, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ReadPolicy);
        group.MapPost("/targets/preview", (int tenantId, [FromBody] MonitoringTargetPreviewRequest body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.PreviewTargetsAsync(tenantId, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ManagePolicy);
        group.MapPut("/rules/{ruleId:guid}", (int tenantId, Guid ruleId, [FromBody] MonitoringRuleWriteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.SaveRuleAsync(tenantId, ruleId, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ManagePolicy);
        group.MapPut("/groups/{groupId:guid}", (int tenantId, Guid groupId, [FromBody] MonitoringGroupWriteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.SaveGroupAsync(tenantId, groupId, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ManagePolicy);
        group.MapPut("/bypasses/{bypassId:guid}", (int tenantId, Guid bypassId, [FromBody] MonitoringBypassWriteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.SaveBypassAsync(tenantId, bypassId, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.BypassPolicy);
        group.MapDelete("/rules/{ruleId:guid}", (int tenantId, Guid ruleId, [FromBody] MonitoringDeleteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.DeleteAsync(tenantId, ruleId, MonitoringResourceKind.Rule, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ManagePolicy);
        group.MapDelete("/groups/{groupId:guid}", (int tenantId, Guid groupId, [FromBody] MonitoringDeleteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.DeleteAsync(tenantId, groupId, MonitoringResourceKind.Group, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ManagePolicy);
        group.MapDelete("/bypasses/{bypassId:guid}", (int tenantId, Guid bypassId, [FromBody] MonitoringDeleteDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.DeleteAsync(tenantId, bypassId, MonitoringResourceKind.Bypass, body, http.User, ct))).RequireAuthorization(MonitoringAuthorization.BypassPolicy);
        group.MapPost("/agents/{agentId:guid}/rules/{ruleId:guid}/ack", (int tenantId, Guid agentId, Guid ruleId, string resourceKey,
            [FromBody] MonitoringOperatorActionDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.ActAsync(tenantId, agentId, ruleId, resourceKey, body, false, http.User, ct))).RequireAuthorization(MonitoringAuthorization.AcknowledgePolicy);
        group.MapPost("/agents/{agentId:guid}/rules/{ruleId:guid}/clear", (int tenantId, Guid agentId, Guid ruleId, string resourceKey,
            [FromBody] MonitoringOperatorActionDto body, HttpContext http, MonitoringApiService service, CancellationToken ct) =>
            ExecuteAsync(() => service.ActAsync(tenantId, agentId, ruleId, resourceKey, body, true, http.User, ct))).RequireAuthorization(MonitoringAuthorization.ClearPolicy);
        return app;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (MonitoringApiException exception) { return Results.Problem(statusCode: exception.StatusCode, title: exception.Code); }
        catch (ArgumentException exception) when (CapacityErrors.Contains(exception.Message)) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: exception.Message); }
        catch (InvalidOperationException exception) when (CapacityErrors.Contains(exception.Message)) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: exception.Message); }
        catch (ArgumentException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_monitoring_request"); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "monitoring_target_authority_required"); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "monitoring_unavailable"); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "monitoring_unavailable"); }
    }
}
