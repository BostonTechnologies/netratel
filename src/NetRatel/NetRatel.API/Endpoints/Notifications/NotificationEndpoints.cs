using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Notifications;
using NetRatel.Application.Observability;
using NetRatel.API.Services.Orchestration;

namespace NetRatel.API.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var userNotifications = app.MapGroup("/api/v1/notifications")
            .WithTags("Notifications")
            .RequireAuthorization("AuditReader");

        userNotifications.MapGet("", async (
            HttpContext http,
            INetRatelNotificationService notifications,
            int page = 1,
            int pageSize = 20,
            string? eventType = null,
            string? correlationId = null,
            string? entityId = null,
            string? status = null,
            string? search = null,
            string? source = null,
            NetRatelNotificationSeverity? severity = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(http);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var pageResult = await notifications.GetPageAsync(
                userId,
                page,
                pageSize,
                eventType,
                correlationId,
                entityId,
                status,
                from,
                to,
                search,
                source,
                severity,
                ct);

            return Results.Ok(pageResult);
        });

        userNotifications.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            INetRatelNotificationService notifications,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(http);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var found = await notifications.GetByIdAsync(id, userId, ct);
            return found is null ? Results.NotFound() : Results.Ok(found);
        });

        userNotifications.MapGet("/unread-errors", async (
            HttpContext http,
            INetRatelNotificationService notifications,
            int take = 20,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(http);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var items = await notifications.GetUnreadErrorsAsync(userId, take, ct);
            return Results.Ok(items);
        });

        userNotifications.MapGet("/summary", async (
            HttpContext http,
            INetRatelNotificationService notifications,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(http);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var summary = await notifications.GetSummaryAsync(userId, ct);
            return Results.Ok(summary);
        });

        userNotifications.MapPost("/mark-read", async (
            HttpContext http,
            [FromBody] BulkMarkReadRequest body,
            INetRatelNotificationService notifications,
            CancellationToken ct = default) =>
        {
            var userId = ResolveUserId(http);
            if (string.IsNullOrWhiteSpace(userId))
                return Results.Unauthorized();

            var updated = await notifications.MarkReadAsync(userId, body.Ids ?? Array.Empty<Guid>(), ct);
            return Results.Ok(new BulkMarkReadResult(updated));
        });

        userNotifications.MapGet("/stream", StreamNotificationsAsync);

        var events = app.MapGroup("/api/v1/events")
            .WithTags("Events")
            .RequireAuthorization("AuditReader");

        events.MapGet("", async (
            INetRatelNotificationService notifications,
            int page = 1,
            int pageSize = 50,
            string? type = null,
            string? correlationId = null,
            string? entityId = null,
            string? status = null,
            string? search = null,
            string? source = null,
            NetRatelNotificationSeverity? severity = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            CancellationToken ct = default) =>
        {
            const string systemUser = "system.events.viewer";
            var pageResult = await notifications.GetPageAsync(
                systemUser,
                page,
                pageSize,
                type,
                correlationId,
                entityId,
                status,
                from,
                to,
                search,
                source,
                severity,
                ct);
            return Results.Ok(pageResult);
        });

        events.MapGet("/{id:guid}", async (
            Guid id,
            INetRatelNotificationService notifications,
            CancellationToken ct = default) =>
        {
            var item = await notifications.GetByIdAsync(id, "system.events.viewer", ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        events.MapPost("/{id:guid}/retry", async (
            Guid id,
            INetRatelNotificationService notifications,
            INetRatelExternalServiceCallbackReplayService callbackReplay,
            CancellationToken ct = default) =>
        {
            var item = await notifications.GetByIdAsync(id, "system.events.viewer", ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (callbackReplay.CanReplay(item))
            {
                await callbackReplay.ReplayAsync(item, ct);
                return Results.Accepted();
            }

            await notifications.RetryAsync(id, ct);
            return Results.Accepted();
        });

        events.MapPost("/{id:guid}/disable", async (
            Guid id,
            INetRatelNotificationService notifications,
            CancellationToken ct = default) =>
        {
            await notifications.DisableAsync(id, ct);
            return Results.Accepted();
        });

        return app;
    }

    private static async Task StreamNotificationsAsync(
        HttpContext http,
        INetRatelNotificationEventBus bus,
        ILoggerFactory loggerFactory,
        [FromQuery] string? tenantId,
        [FromQuery] string? eventType,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("NetRatel.API.Endpoints.NotificationStream");
        http.Response.Headers.Append("Cache-Control", "no-cache");
        http.Response.Headers.Append("Connection", "keep-alive");
        http.Response.Headers.Append("X-Accel-Buffering", "no");
        http.Response.ContentType = "text/event-stream";

        var reader = bus.Subscribe(tenantId);
        NetRatelTelemetry.RecordNotificationStreamConnected(tenantId, eventType);
        logger.LogInformation(
            "NetRatel notification stream connected for tenant scope {TenantScope} and event type {EventType}",
            string.IsNullOrWhiteSpace(tenantId) ? "all" : "tenant",
            string.IsNullOrWhiteSpace(eventType) ? "all" : eventType);

        var abort = http.RequestAborted;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, abort);
        var token = linkedCts.Token;

        try
        {
            await foreach (var notification in reader.ReadAllAsync(token))
            {
                if (!string.IsNullOrWhiteSpace(eventType) &&
                    !string.Equals(notification.EventType, eventType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = JsonSerializer.Serialize(notification);
                await http.Response.WriteAsync("event: notification\n", token);
                await http.Response.WriteAsync($"id: {notification.Id}\n", token);
                await http.Response.WriteAsync($"data: {payload}\n\n", token);
                await http.Response.Body.FlushAsync(token);
                NetRatelTelemetry.RecordNotificationStreamEventSent(tenantId, eventType);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || abort.IsCancellationRequested)
        {
            logger.LogDebug("NetRatel notification stream ended after a client cancellation.");
        }
        catch (IOException) when (abort.IsCancellationRequested)
        {
            logger.LogDebug("NetRatel notification stream ended after a client disconnect.");
        }
        catch (Exception ex)
        {
            NetRatelTelemetry.RecordNotificationStreamError(tenantId, eventType);
            logger.LogWarning(
                ex,
                "NetRatel notification stream failed for tenant scope {TenantScope} and event type {EventType}",
                string.IsNullOrWhiteSpace(tenantId) ? "all" : "tenant",
                string.IsNullOrWhiteSpace(eventType) ? "all" : eventType);
            throw;
        }
        finally
        {
            bus.Unsubscribe(tenantId, reader);
            NetRatelTelemetry.RecordNotificationStreamDisconnected(tenantId, eventType);
            logger.LogInformation(
                "NetRatel notification stream disconnected for tenant scope {TenantScope} and event type {EventType}",
                string.IsNullOrWhiteSpace(tenantId) ? "all" : "tenant",
                string.IsNullOrWhiteSpace(eventType) ? "all" : eventType);
        }
    }

    private static string? ResolveUserId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-NetRatel-UserId", out var explicitUser) &&
            !string.IsNullOrWhiteSpace(explicitUser))
        {
            return explicitUser.ToString().Trim();
        }

        var user = context.User;
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("preferred_username")
            ?? user.Identity?.Name;
    }

    private sealed record BulkMarkReadRequest(IReadOnlyCollection<Guid>? Ids);
    private sealed record BulkMarkReadResult(int Updated);
}
