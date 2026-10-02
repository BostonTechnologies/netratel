using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Endpoints.Client;

public static class ClientServicesEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapClientServicesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/services")
            .WithTags("Client Services").RequireAuthorization("TelemetryReader");
        group.MapGet("", ReadAsync).WithName("ClientServices_Read").Produces<ClientServicesReadModelDto>();
        group.MapPost("/refresh", RefreshAsync).WithName("ClientServices_Refresh").Produces<ClientServicesRefreshResponse>();
        group.MapGet("/watch-policy", PolicyAsync).WithName("ClientServices_WatchPolicy").Produces<ClientServiceWatchPolicyDto>();
        group.MapGet("/events", EventsAsync).WithName("ClientServices_Events");
        return app;
    }

    private static async Task<IResult> ReadAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, IAgentManagementService agents, ClientServicesCoordinator services, CancellationToken cancellationToken)
    {
        var rejection = await AuthorizeTargetAsync(tenantId, agentId, http, access, agents, cancellationToken).ConfigureAwait(false);
        if (rejection is not null) return rejection;
        return Results.Ok(await services.ReadAsync(new(tenantId, agentId), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> RefreshAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, IAgentManagementService agents, ClientServicesCoordinator services, CancellationToken cancellationToken)
    {
        var rejection = await AuthorizeTargetAsync(tenantId, agentId, http, access, agents, cancellationToken).ConfigureAwait(false);
        if (rejection is not null) return rejection;
        return Results.Ok(await services.RefreshAsync(new(tenantId, agentId), cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PolicyAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, IAgentManagementService agents, ClientServicesCoordinator services, CancellationToken cancellationToken)
    {
        var rejection = await AuthorizeTargetAsync(tenantId, agentId, http, access, agents, cancellationToken).ConfigureAwait(false);
        if (rejection is not null) return rejection;
        return Results.Ok(await services.GetPolicyAsync(new(tenantId, agentId), cancellationToken).ConfigureAwait(false));
    }

    private static async Task EventsAsync(int tenantId, Guid agentId, HttpContext http, IEffectiveAccessService access,
        IAgentManagementService agents, ClientServicesCoordinator services, [FromServices] TimeProvider timeProvider)
    {
        var rejection = await AuthorizeTargetAsync(tenantId, agentId, http, access, agents, http.RequestAborted).ConfigureAwait(false);
        if (rejection is not null) { await rejection.ExecuteAsync(http).ConfigureAwait(false); return; }
        var client = new ClientKey(tenantId, agentId);
        using var lease = services.TryAcquireViewer(client);
        if (lease is null) { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        (long Revision, bool Connected, bool Supported)? previous = null;
        var keepAliveAt = timeProvider.GetUtcNow();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
            do
            {
                if (await AuthorizeTargetAsync(tenantId, agentId, http, access, agents, http.RequestAborted).ConfigureAwait(false) is not null) return;
                // Read the persisted, selected-client projection only. A viewer
                // never invokes an OS collection or starts watches implicitly.
                var model = await services.ReadAsync(client, http.RequestAborted).ConfigureAwait(false);
                var version = (model.Revision, model.Connected, model.SupportsServices);
                if (previous != version)
                {
                    previous = version;
                    await http.Response.WriteAsync("event: services\ndata: " + JsonSerializer.Serialize(model, JsonOptions) + "\n\n", http.RequestAborted).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                    keepAliveAt = timeProvider.GetUtcNow();
                }
                else if (timeProvider.GetUtcNow() >= keepAliveAt.AddSeconds(15))
                {
                    await http.Response.WriteAsync(": keep-alive\n\n", http.RequestAborted).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                    keepAliveAt = timeProvider.GetUtcNow();
                }
            } while (await timer.WaitForNextTickAsync(http.RequestAborted).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task<IResult?> AuthorizeTargetAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, IAgentManagementService agents, CancellationToken cancellationToken)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.TelemetryRead, tenantId, cancellationToken).ConfigureAwait(false)) return Results.Forbid();
        var agent = await agents.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        return agent is null || agent.TenantId != tenantId || agent.AgentId != agentId || agent.DeletedAtUtc is not null
            ? Results.NotFound() : null;
    }
}
