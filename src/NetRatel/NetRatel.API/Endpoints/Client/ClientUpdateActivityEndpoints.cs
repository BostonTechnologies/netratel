using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Local;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Client;

public static class ClientUpdateActivityEndpoints
{
    public static IEndpointRouteBuilder MapClientUpdateActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/client-updates/activity/stream", StreamAsync)
            .RequireAuthorization("ArtifactPublisher").WithTags("Client Updates");
        return app;
    }

    private static async Task StreamAsync(HttpContext http, [FromQuery] int? tenantId,
        IEffectiveAccessService access, ClientUpdateActivityBroker broker, TimeProvider timeProvider)
    {
        var ct = http.RequestAborted;
        var authorizedTenants = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.ArtifactPublication, ct);
        if (!await IsAuthorizedAsync(http, access, tenantId, authorizedTenants, timeProvider, ct))
        { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        using var subscription = broker.Subscribe(tenantId, authorizedTenants);
        if (subscription is null) { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            // Subscribe before ready. Each ready causes a snapshot, and notifications received
            // during that snapshot queue another refresh: there is no subscribe/snapshot gap.
            await WriteAsync(http, new { kind = "ready", id = Guid.NewGuid(), connected = broker.IsConnected }, ct);
            Task<bool>? pendingRead = null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
            Task<bool>? heartbeat = null;
            while (!ct.IsCancellationRequested)
            {
                pendingRead ??= subscription.Reader.WaitToReadAsync(ct).AsTask();
                heartbeat ??= timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingRead, heartbeat);
                var currentTenants = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.ArtifactPublication, ct);
                if (!await IsAuthorizedAsync(http, access, tenantId, currentTenants, timeProvider, ct) ||
                    !SameScope(authorizedTenants, currentTenants)) return;
                if (completed == pendingRead)
                {
                    if (!await pendingRead) return;
                    pendingRead = null;
                    if (subscription.Reader.TryRead(out var notification))
                        await WriteAsync(http, new { kind = "invalidate", id = notification.Id, connected = broker.IsConnected }, ct);
                }
                else
                {
                    if (!await heartbeat) return;
                    heartbeat = null;
                    // A data heartbeat lets the existing client detect a stale connection.
                    await WriteAsync(http, new { kind = "heartbeat", connected = broker.IsConnected }, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }

    private static bool SameScope(int[]? original, int[]? current) =>
        original is null ? current is null : current is not null && original.Order().SequenceEqual(current.Order());

    private static async Task<bool> IsAuthorizedAsync(HttpContext http, IEffectiveAccessService access, int? tenantId,
        int[]? authorizedTenants, TimeProvider timeProvider, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true || authorizedTenants is { Length: 0 } ||
            tenantId is { } requested && authorizedTenants is { } allowed && !allowed.Contains(requested)) return false;
        if (long.TryParse(http.User.FindFirstValue("exp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiration) &&
            timeProvider.GetUtcNow().ToUnixTimeSeconds() >= expiration) return false;
        var ticket = await http.AuthenticateAsync();
        if (ticket.Properties?.ExpiresUtc is { } expires && timeProvider.GetUtcNow() >= expires) return false;
        if (http.User.HasClaim("auth_mode", "local"))
        {
            var identityDb = http.RequestServices.GetRequiredService<NetRatelIdentityDbContext>();
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await identityDb.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null || !LocalSessionValidator.IsValid(http.User, user)) return false;
        }
        return await access.AuthorizeAsync(http.User, NetRatelPermissions.ArtifactPublication, tenantId, ct);
    }

    private static async Task WriteAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
