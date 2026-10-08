using NetRatel.Application.RatelDesk;
using NetRatel.Application.Tenants;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.API.Endpoints.RatelDesk;

public static class RatelDeskConnectorEndpoints
{
    public static IEndpointRouteBuilder MapRatelDeskConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/connectors/rateldesk/tenants", (HttpContext http, ITenantService tenants,
            IRatelDeskConnectorAuthorization authorization, CancellationToken ct) => ExecuteAsync(http, false, async () =>
        {
            var allowed = new List<RatelDeskConnectorTenantDto>();
            foreach (var tenant in await tenants.ListAsync(ct))
                if (await authorization.CanManageAsync(http.User, tenant.TenantId, ct))
                    allowed.Add(new(tenant.TenantId, tenant.Name));
            return Results.Ok(allowed);
        })).WithTags("RatelDesk Connectors").RequireAuthorization();
        var group = app.MapGroup("/api/v2/tenants/{tenantId:int}/connectors/rateldesk").WithTags("RatelDesk Connectors").RequireAuthorization();
        group.MapGet("/setup", (int tenantId, HttpContext http, IRatelDeskConnectorSetupService service, CancellationToken ct) =>
            ExecuteAsync(http, false, async () => Results.Ok(await service.GetAsync(tenantId, http.User, ct))));
        group.MapPost("/setup/source", (int tenantId, AdoptRatelDeskFlowSourceRequest request, HttpContext http,
            IRatelDeskConnectorSetupService service, CancellationToken ct) => ExecuteAsync(http, true,
                async () => Results.Ok(await service.AdoptAsync(tenantId, request.ExpectedIdentityRevision, http.User, ct)))).RequireAuthorization("InteractiveAccount");
        group.MapPost("/setup/complete", (int tenantId, CompleteRatelDeskConnectionRequest request, HttpContext http,
            IRatelDeskConnectorSetupService service, CancellationToken ct) => ExecuteAsync(http, true,
                async () => Results.Ok(await service.CompleteAsync(tenantId, request.LinkId, http.User, ct)))).RequireAuthorization("InteractiveAccount");
        group.MapGet("", (int tenantId, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, false, async () => Results.Ok(await service.ListAsync(tenantId, http.User, ct))));
        group.MapGet("/{id:guid}", (int tenantId, Guid id, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, false, async () => await service.GetAsync(tenantId, id, http.User, ct) is { } value ? Results.Ok(value) : Results.NotFound()));
        group.MapPut("/{id:guid}", (int tenantId, Guid id, SaveRatelDeskConnectorRequest request, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, true, async () => Results.Ok(await service.SaveAsync(tenantId, id, request, http.User, ct))));
        group.MapPost("/{id:guid}/credential", (int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, true, async () => Results.Ok(await service.RotateAsync(tenantId, id, request, http.User, ct))));
        group.MapPost("/{id:guid}/connection-test", (int tenantId, Guid id, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, true, async () => Results.Ok(await service.TestAsync(tenantId, id, http.User, ct))));
        group.MapPost("/{id:guid}/dry-run", (int tenantId, Guid id, RatelDeskDryRunRequest request, HttpContext http, IRatelDeskConnectorService service, CancellationToken ct) =>
            ExecuteAsync(http, false, async () => Results.Ok(await service.DryRunAsync(tenantId, id, request, http.User, ct))));
        return app;
    }
    private static async Task<IResult> ExecuteAsync(HttpContext http, bool mutation, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (mutation && http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1") return Results.Forbid();
        try { return await action().ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ServiceLinkProtocolException error)
        {
            var code = error.Code is "source-identity-conflict" or "identity-revision-conflict" or "identity-configuration-drift" or "connection-pending" or "incident-grant-incomplete" or "connector-mapping-conflict"
                ? error.Code : "receiver-current-profile-unavailable";
            return Results.Json(new { code }, statusCode: error.StatusCode is 400 or 401 or 403 or 409 or 422 or 503 ? error.StatusCode : 503);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "invalid-connector-request" }); }
        catch (InvalidOperationException e) when (e.Message == "connector-conflict") { return Results.Conflict(new { code = "connector-conflict" }); }
        catch (InvalidOperationException e) when (e.Message == "connector-capacity-exhausted")
        { return Results.Json(new { code = "connector-capacity-exhausted" }, statusCode: StatusCodes.Status429TooManyRequests); }
    }
}
