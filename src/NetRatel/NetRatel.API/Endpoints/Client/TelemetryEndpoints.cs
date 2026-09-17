using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Realtime;

namespace NetRatel.API.Endpoints.Client;

public static class TelemetryEndpoints
{
    private static readonly JsonSerializerOptions SseSerializerOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/telemetry/overview", (IAgentTelemetryCompatibilityRegistry registry) =>
            Results.Ok(registry.GetSnapshots()))
            .WithTags("Client Telemetry")
            .WithSummary("List latest client telemetry snapshots");

        app.MapGet("/api/v1/telemetry/stream", async (
            HttpContext http,
            IAgentTelemetryCompatibilityRegistry registry) =>
        {
            PrepareSse(http.Response);
            var ct = http.RequestAborted;

            foreach (var snapshot in registry.GetSnapshots())
            {
                var json = JsonSerializer.Serialize(snapshot, SseSerializerOptions);
                await http.Response.WriteAsync($"event: telemetry\ndata: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await StreamSnapshotsAsync(http.Response, registry.StreamAsync(ct), static _ => true, ct);
            return Results.Empty;
        })
            .WithTags("Client Telemetry")
            .WithSummary("Stream client telemetry snapshots");

        app.MapGet("/api/v1/clients/{clientIdentity}/telemetry", (
            string clientIdentity,
            IAgentTelemetryCompatibilityRegistry registry) =>
        {
            var snapshot = registry.GetSnapshot(clientIdentity);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        })
            .WithTags("Client Telemetry")
            .WithSummary("Get latest telemetry for a client");

        app.MapGet("/api/v1/clients/{clientIdentity}/telemetry/stream", async (
            string clientIdentity,
            HttpContext http,
            IAgentTelemetryCompatibilityRegistry registry) =>
        {
            PrepareSse(http.Response);
            var ct = http.RequestAborted;

            var initial = registry.GetSnapshot(clientIdentity);
            if (initial is not null)
            {
                var json = JsonSerializer.Serialize(initial, SseSerializerOptions);
                await http.Response.WriteAsync($"event: telemetry\ndata: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await StreamSnapshotsAsync(
                http.Response,
                registry.StreamAsync(ct),
                snapshot => string.Equals(snapshot.ClientIdentity, clientIdentity, StringComparison.OrdinalIgnoreCase),
                ct);

            return Results.Empty;
        })
            .WithTags("Client Telemetry")
            .WithSummary("Stream telemetry for a client");

        return app;
    }

    private static void PrepareSse(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        response.ContentType = "text/event-stream";
    }

    private static async Task StreamSnapshotsAsync(
        HttpResponse response,
        IAsyncEnumerable<NetRatel.Shared.Contracts.AgentTelemetrySnapshotDto> stream,
        Func<NetRatel.Shared.Contracts.AgentTelemetrySnapshotDto, bool> predicate,
        CancellationToken ct)
    {
        var keepAliveInterval = TimeSpan.FromSeconds(15);
        var enumerator = stream.GetAsyncEnumerator(ct);
        Task<bool>? pendingMoveNext = null;
        using var keepAliveTimer = new PeriodicTimer(keepAliveInterval);
        Task<bool>? pendingTick = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                pendingMoveNext ??= enumerator.MoveNextAsync().AsTask();
                pendingTick ??= keepAliveTimer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingMoveNext, pendingTick);

                if (completed == pendingMoveNext)
                {
                    var hasItem = await pendingMoveNext;
                    pendingMoveNext = null;
                    if (!hasItem)
                    {
                        break;
                    }

                    var snapshot = enumerator.Current;
                    if (!predicate(snapshot))
                    {
                        continue;
                    }

                    var json = JsonSerializer.Serialize(snapshot, SseSerializerOptions);
                    await response.WriteAsync($"event: telemetry\ndata: {json}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
                else
                {
                    var hasTick = await pendingTick;
                    pendingTick = null;
                    if (!hasTick)
                    {
                        break;
                    }

                    await response.WriteAsync(": keep-alive\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                if (pendingMoveNext is not null)
                {
                    try { await pendingMoveNext; } catch { }
                }

                if (pendingTick is not null)
                {
                    try { await pendingTick; } catch { }
                }

                await enumerator.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}
