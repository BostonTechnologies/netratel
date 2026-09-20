using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Telemetry;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Channels;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Gateway-native telemetry read surface keyed by the enrolled agent directory ID.
/// It does not reinterpret the established v1 client-identity routes.
/// </summary>
public static class AgentTelemetryReadEndpoints
{
    private static readonly JsonSerializerOptions SseSerializerOptions = new(JsonSerializerDefaults.Web);
    public static IEndpointRouteBuilder MapAgentTelemetryReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/agent-telemetry", ListAsync)
            .WithName("AgentTelemetry_List")
            .WithTags("Agent Telemetry")
            .RequireAuthorization("InstanceAdministrator")
            .Produces<IReadOnlyList<AgentTelemetrySnapshotResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/v2/agents/{tenantId:int}/{agentId:guid}/telemetry", GetAsync)
            .WithName("AgentTelemetry_Get")
            .WithTags("Agent Telemetry")
            .RequireAuthorization("TelemetryReader")
            .Produces<AgentTelemetrySnapshotResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/v2/agents/{tenantId:int}/{agentId:guid}/telemetry/stream", StreamAsync)
            .WithName("AgentTelemetry_Stream")
            .WithTags("Agent Telemetry")
            .RequireAuthorization("TelemetryReader")
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    private static async Task<IResult> ListAsync(
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsTelemetryAuthorityActive)
        {
            return Results.NotFound();
        }

        var telemetry = services.GetRequiredService<IClientTelemetryRouter>();
        var readModel = await telemetry.GetReadModelAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(readModel.Snapshots.Select(Map).ToArray());
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsTelemetryAuthorityActive)
        {
            return Results.NotFound();
        }

        var telemetry = services.GetRequiredService<IClientTelemetryRouter>();
        var state = await telemetry.GetSnapshotAsync(new(tenantId, agentId), cancellationToken).ConfigureAwait(false);
        return state.Latest is null ? Results.NotFound() : Results.Ok(Map(state.Latest));
    }

    private static AgentTelemetrySnapshotResponse Map(TelemetrySnapshot snapshot) => new(
        snapshot.Client.TenantId,
        snapshot.Client.AgentId,
        snapshot.ObservedAtUtc,
        snapshot.ReceivedAtUtc,
        snapshot.Cpu,
        snapshot.Memory,
        snapshot.Disks,
        snapshot.Networks,
        snapshot.TransportHealth,
        snapshot.Source,
        snapshot.IsAuthoritative);

    private static async Task StreamAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] int? samplePeriodMs,
        HttpContext http,
        [FromServices] NetRatelAkkaMigrationOptions options,
        [FromServices] IClientTelemetryRouter telemetry,
        [FromServices] IGatewayTelemetryLiveRegistry live,
        [FromServices] ITelemetryInteractiveDemandRegistry demand,
        [FromServices] IAgentTelemetryGatewaySessionRegistry sessions,
        [FromServices] TimeProvider timeProvider)
    {
        if (!options.IsTelemetryAuthorityActive)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var client = new NetRatel.Application.Presence.ClientKey(tenantId, agentId);
        var period = Math.Clamp(samplePeriodMs ?? 1000, 1000, 60_000);
        TelemetryInteractiveLease? lease = null;
        GatewayTelemetryLiveSubscription? subscription = null;
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        Task<GatewayTelemetryLiveUpdate>? pendingRead = null;
        Task<bool>? pendingTick = null;
        try
        {
            lease = demand.Acquire(client, period);
            subscription = live.Subscribe(client);
            PrepareSse(http.Response);
            var sentEpoch = -1L;
            ulong sentSequence = 0;
            var state = await telemetry.GetSnapshotAsync(client, streamCancellation.Token).ConfigureAwait(false);
            var initialMode = live.GetMode(client) ?? CreateMode(demand.GetPolicy(client), sessions.GetStatus(client));
            if (state.Latest is not null)
            {
                await WriteAsync(http.Response, CreateEnvelope(state.Latest, initialMode, period), streamCancellation.Token).ConfigureAwait(false);
                sentEpoch = state.Latest.ConnectionEpoch;
                sentSequence = state.Latest.Sequence;
            }
            else
            {
                await WriteAsync(http.Response, CreateEnvelope(null, initialMode, period), streamCancellation.Token).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
            while (!streamCancellation.IsCancellationRequested)
            {
                pendingRead ??= subscription.Reader.ReadAsync(streamCancellation.Token).AsTask();
                pendingTick ??= timer.WaitForNextTickAsync(streamCancellation.Token).AsTask();
                var completed = await Task.WhenAny(pendingRead, pendingTick).ConfigureAwait(false);
                if (completed == pendingTick)
                {
                    if (!await pendingTick.ConfigureAwait(false)) break;
                    pendingTick = null;
                    demand.Renew(lease);
                    await http.Response.WriteAsync(": keep-alive\n\n", streamCancellation.Token).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(streamCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                var update = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                if (update.Mode is { } mode)
                {
                    await WriteAsync(http.Response, CreateEnvelope(null, mode, period), streamCancellation.Token).ConfigureAwait(false);
                    continue;
                }
                if (update.Snapshot is not { } snapshot || snapshot.Snapshot.ConnectionEpoch < sentEpoch ||
                    snapshot.Snapshot.ConnectionEpoch == sentEpoch && snapshot.Snapshot.Sequence <= sentSequence)
                {
                    continue;
                }
                var currentMode = live.GetMode(client) ?? CreateMode(demand.GetPolicy(client), sessions.GetStatus(client));
                await WriteAsync(http.Response, CreateEnvelope(snapshot.Snapshot, currentMode, period), streamCancellation.Token).ConfigureAwait(false);
                sentEpoch = snapshot.Snapshot.ConnectionEpoch;
                sentSequence = snapshot.Snapshot.Sequence;
                demand.Renew(lease);
            }
        }
        catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            streamCancellation.Cancel();
            await ObserveCancellationAsync(pendingRead).ConfigureAwait(false);
            await ObserveCancellationAsync(pendingTick).ConfigureAwait(false);
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
            if (lease is not null) demand.Release(lease);
        }
    }

    private static void PrepareSse(HttpResponse response)
    {
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        response.ContentType = "text/event-stream";
        response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static Task WriteAsync(HttpResponse response, AgentTelemetryLiveResponse envelope, CancellationToken cancellationToken) =>
        WriteAndFlushAsync(response, $"event: telemetry\ndata: {JsonSerializer.Serialize(envelope, SseSerializerOptions)}\n\n", cancellationToken);

    private static async Task WriteAndFlushAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveCancellationAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine("Telemetry SSE pending operation was cancelled during response cleanup.");
        }
        catch (ChannelClosedException)
        {
            System.Diagnostics.Trace.WriteLine("Telemetry SSE subscription completed during response cleanup.");
        }
    }

    private static GatewayTelemetryLiveMode CreateMode(TelemetrySamplingPolicyState policy, AgentTelemetryGatewaySessionStatus session)
    {
        var effectiveInteractive = policy.Interactive && session.SupportsDynamicSampling;
        return new(
        session.Connected ? session.SupportsDynamicSampling ? "Live" : "Unsupported" : "Offline",
        session.SupportsDynamicSampling,
        policy.Interactive,
        effectiveInteractive,
        effectiveInteractive ? policy.FastIntervalMilliseconds : 5000,
        session.AgentVersion,
        policy.Revision,
        policy.ExpiresAtUtc,
        effectiveInteractive ? policy.Reason : session.Connected ? "client-upgrade-required" : "telemetry-session-unavailable");
    }

    private static AgentTelemetryLiveResponse CreateEnvelope(TelemetrySnapshot? snapshot, GatewayTelemetryLiveMode mode, int requestedPeriodMilliseconds) => new(
        mode.ConnectionState,
        mode.ConnectionState,
        snapshot?.ConnectionEpoch,
        snapshot?.Sequence,
        snapshot is null ? null : Map(snapshot),
        mode.EffectiveSamplePeriodMilliseconds,
        mode.InteractiveRequested,
        mode.InteractiveEffective,
        mode.SupportsDynamicSampling,
        requestedPeriodMilliseconds,
        mode.AgentVersion,
        mode.PolicyRevision,
        mode.PolicyExpiresAtUtc,
        mode.StateReason);
}

public sealed record AgentTelemetrySnapshotResponse(
    int TenantId,
    Guid AgentId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    TelemetryCpu? Cpu,
    TelemetryMemory? Memory,
    IReadOnlyList<TelemetryDisk> Disks,
    IReadOnlyList<TelemetryNetwork> Networks,
    TelemetryTransportHealth? TransportHealth,
    string Source,
    bool IsAuthoritative);

public sealed record AgentTelemetryLiveResponse(
    string State,
    string ConnectionState,
    long? ConnectionEpoch,
    ulong? Sequence,
    AgentTelemetrySnapshotResponse? Snapshot,
    int EffectiveSamplePeriodMilliseconds,
    bool InteractiveRequested,
    bool InteractiveEffective,
    bool SupportsDynamicSampling,
    int RequestedSamplePeriodMilliseconds,
    string? AgentVersion,
    long PolicyRevision,
    DateTimeOffset PolicyExpiresAtUtc,
    string StateReason);
