using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Local;
using NetRatel.API.Services;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Client;

public static class TaskExecutionStreamEndpoints
{
    public static IEndpointRouteBuilder MapTaskExecutionStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/tasks/stream", StreamAsync).RequireAuthorization().WithTags("Agent Tasks");
        return app;
    }

    private static async Task StreamAsync(HttpContext http, [FromQuery] string requestId,
        IJobRunService tasks, IEffectiveAccessService access, ExecutionActivityBroker broker, TimeProvider timeProvider)
    {
        var ct = http.RequestAborted;
        var task = await tasks.GetActivityByRequestIdAsync(requestId, ct);
        if (task is null) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!await IsAuthorizedAsync(http, access, task.TenantId, timeProvider, ct))
        { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        using var subscription = broker.Subscribe(task.TenantId, requestId: requestId);
        if (subscription is null) { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            // Subscribe before ready so a commit during snapshot recovery queues another refresh.
            await WriteAsync(http, new { kind = "ready", requestId, connected = broker.IsConnected }, ct);
            Task<bool>? pendingRead = null;
            Task<bool>? heartbeat = null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
            while (!ct.IsCancellationRequested)
            {
                pendingRead ??= subscription.Reader.WaitToReadAsync(ct).AsTask();
                heartbeat ??= timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingRead, heartbeat);
                if (!await IsAuthorizedAsync(http, access, task.TenantId, timeProvider, ct)) return;
                if (completed == pendingRead)
                {
                    if (!await pendingRead) return;
                    pendingRead = null;
                    if (subscription.Reader.TryRead(out var notification))
                        await WriteAsync(http, new { kind = "invalidate", id = notification.Id, requestId, connected = broker.IsConnected }, ct);
                }
                else
                {
                    if (!await heartbeat) return;
                    heartbeat = null;
                    await WriteAsync(http, new { kind = "heartbeat", requestId, connected = broker.IsConnected }, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<bool> IsAuthorizedAsync(HttpContext http, IEffectiveAccessService access,
        int? tenantId, TimeProvider timeProvider, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true) return false;
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
        return await access.AuthorizeAsync(http.User, NetRatelPermissions.ScriptExecute, tenantId, ct);
    }

    private static async Task WriteAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
