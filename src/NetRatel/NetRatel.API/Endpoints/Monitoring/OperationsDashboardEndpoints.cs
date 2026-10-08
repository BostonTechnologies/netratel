using NetRatel.API.Middleware;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Services.Dashboard;
using NetRatel.API.Services.Monitoring;

namespace NetRatel.API.Endpoints.Monitoring;

public static class OperationsDashboardEndpoints
{
    public static IEndpointRouteBuilder MapOperationsDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/tenants/{tenantId:int}/monitoring/dashboard", async (int tenantId,
            int? page, int? pageSize, HttpContext http, OperationsDashboardService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ReadAsync(tenantId, page ?? 0, pageSize ?? 25, http.User, ct).ConfigureAwait(false)); }
            catch (MonitoringApiException exception) { return Results.Problem(statusCode: exception.StatusCode, title: exception.Code); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "dashboard_unavailable"); }
        }).RequireAuthorization(MonitoringAuthorization.ReadPolicy).WithTags("Monitoring").WithMetadata(new MonitoringHttpBounds());
        app.MapGet("/api/v2/tenants/{tenantId:int}/monitoring/dashboard/jobs", async (int tenantId,
            HttpContext http, OperationsRecentJobsService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.ReadAsync(tenantId, http.User, ct).ConfigureAwait(false)); }
            catch (MonitoringApiException exception) { return Results.Problem(statusCode: exception.StatusCode, title: exception.Code); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "dashboard_jobs_unavailable"); }
        }).RequireAuthorization(MonitoringAuthorization.ReadPolicy).WithTags("Monitoring").WithMetadata(new MonitoringHttpBounds());
        return app;
    }
}
