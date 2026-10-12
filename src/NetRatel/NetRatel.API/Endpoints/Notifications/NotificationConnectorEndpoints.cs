using System.Security.Cryptography;
using NetRatel.Application.Notifications;
using NetRatel.Application.RatelDesk;
using NetRatel.Application.Tenants;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.API.Endpoints.Notifications;

public static class NotificationConnectorEndpoints
{
    public static IEndpointRouteBuilder MapNotificationConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/connectors/notifications/tenants", (HttpContext http, ITenantService tenants,
            IRatelDeskConnectorAuthorization authorization, CancellationToken ct) => ExecuteAsync(http, false, async () =>
        {
            var allowed = new List<NotificationConnectorTenantDto>();
            foreach (var tenant in await tenants.ListAsync(ct))
                if (await authorization.CanManageAsync(http.User, tenant.TenantId, ct)) allowed.Add(new(tenant.TenantId, tenant.Name));
            return Results.Ok(allowed);
        })).WithTags("Notification Connectors").RequireAuthorization();
        var group = app.MapGroup("/api/v2/tenants/{tenantId:int}/connectors/notifications")
            .WithTags("Notification Connectors").RequireAuthorization();
        group.MapGet("", (int tenantId, HttpContext http, INotificationConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, false, async () => Results.Ok(await service.ListAsync(tenantId, http.User, ct))));
        group.MapPut("/{id:guid}", (int tenantId, Guid id, SaveNotificationConnectorRequest request, HttpContext http,
            INotificationConnectorService service, CancellationToken ct) => ExecuteAsync(http, true,
            async () => Results.Ok(await service.SaveAsync(tenantId, id, request, http.User, ct))));
        group.MapDelete("/{id:guid}", (int tenantId, Guid id, long expectedRevision, HttpContext http,
            INotificationConnectorService service, CancellationToken ct) => ExecuteAsync(http, true, async () =>
        {
            await service.DeleteAsync(tenantId, id, expectedRevision, http.User, ct);
            return Results.NoContent();
        }));
        group.MapPost("/{id:guid}/test-notification", (int tenantId, Guid id, HttpContext http,
            INotificationConnectorService service, CancellationToken ct) => ExecuteAsync(http, true,
            async () => Results.Ok(await service.SendTestAsync(tenantId, id, http.User, ct))));
        return app;
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, bool mutation, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (mutation && http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1")
            return Results.Forbid();
        try { return await action().ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "invalid-notification-connector-request" }); }
        catch (CryptographicException) { return Results.Json(new { code = "notification-credential-unavailable" }, statusCode: 503); }
        catch (InvalidOperationException error) when (error.Message == "notification-connector-conflict")
        { return Results.Conflict(new { code = "notification-connector-conflict" }); }
        catch (InvalidOperationException error) when (error.Message == "notification-connector-capacity-exhausted")
        { return Results.Json(new { code = "notification-connector-capacity-exhausted" }, statusCode: 429); }
    }
}
