using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Realtime;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using ApplicationTelemetryCpu = NetRatel.Application.Telemetry.TelemetryCpu;
using ApplicationTelemetryDisk = NetRatel.Application.Telemetry.TelemetryDisk;
using ApplicationTelemetryMemory = NetRatel.Application.Telemetry.TelemetryMemory;
using ApplicationTelemetryNetwork = NetRatel.Application.Telemetry.TelemetryNetwork;
using ApplicationTelemetryTransportHealth = NetRatel.Application.Telemetry.TelemetryTransportHealth;

namespace NetRatel.API.Gateway;

[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentTelemetryGatewayService(
    IClientTelemetryRouter telemetryRouter,
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentTelemetryCompatibilityRegistry compatibilityRegistry,
    NetRatelAkkaMigrationOptions options,
    TimeProvider timeProvider,
    ILogger<AgentTelemetryGatewayService> logger)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGateway.AgentTelemetryGatewayBase
{
    public override async Task<TelemetryPublishSummary> PublishTelemetry(
        IAsyncStreamReader<TelemetryFrame> requestStream,
        ServerCallContext context)
    {
        if (!options.Enabled || !options.GatewayEnabled || !options.TelemetryShadowEnabled)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Telemetry shadow mode is disabled."));
        }

        if (!AgentGatewayIdentityResolver.TryResolve(
                context.GetHttpContext().User,
                out var authenticatedIdentity,
                out var identityError) ||
            authenticatedIdentity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, identityError));
        }

        await EnsureAgentIsActiveAsync(authenticatedIdentity, context.CancellationToken).ConfigureAwait(false);

        var client = new ClientKey(authenticatedIdentity.TenantId, authenticatedIdentity.AgentId);
        ulong acceptedCount = 0;
        ulong rejectedCount = 0;
        ulong lastAcceptedSequence = 0;

        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            var frame = requestStream.Current;
            ThrowIfInvalid(AgentTelemetryProtocolValidator.Validate(
                frame,
                authenticatedIdentity,
                options.ProtocolVersion,
                options.MaxTelemetryScopesPerFrame));

            var presence = await presenceRouter.GetSnapshotAsync(client, context.CancellationToken)
                .ConfigureAwait(false);
            var connectionId = Guid.Parse(frame.ConnectionId);
            var connectionEpoch = checked((long)frame.ConnectionEpoch);
            if (presence.Status != ShadowPresenceStatus.Online ||
                presence.ConnectionId != connectionId ||
                presence.ConnectionEpoch != connectionEpoch)
            {
                throw new RpcException(new Status(
                    StatusCode.Aborted,
                    "Telemetry frame does not match the active authenticated presence session."));
            }

            var receivedAtUtc = timeProvider.GetUtcNow();
            var snapshot = MapSnapshot(
                frame,
                client,
                connectionEpoch,
                receivedAtUtc,
                isAuthoritative: false);
            var result = await telemetryRouter.RecordAsync(
                new RecordTelemetrySnapshot(snapshot),
                context.CancellationToken).ConfigureAwait(false);

            if (result.Disposition == TelemetryMessageDisposition.Accepted)
            {
                compatibilityRegistry.Upsert(snapshot);
                acceptedCount = IncrementSaturating(acceptedCount);
                lastAcceptedSequence = result.LastAcceptedSequence;
            }
            else
            {
                rejectedCount = IncrementSaturating(rejectedCount);
            }
        }

        return new TelemetryPublishSummary
        {
            AcceptedCount = acceptedCount,
            RejectedCount = rejectedCount,
            LastAcceptedSequence = lastAcceptedSequence,
            // This original wire contract is permanently shadow-only. Gateway
            // telemetry authority uses the additive V2 bidi contract.
            TelemetryAuthority = "unavailable"
        };
    }

    private async Task EnsureAgentIsActiveAsync(
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        AgentDetailDto? agent;
        try
        {
            agent = await agentManagement.GetAsync(identity.TenantId, identity.AgentId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Telemetry gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}",
                identity.TenantId,
                identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }

        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        }
    }

    internal static TelemetrySnapshot MapSnapshot(
        TelemetryFrame frame,
        ClientKey client,
        long connectionEpoch,
        DateTimeOffset receivedAtUtc,
        bool isAuthoritative) =>
        new(
            client,
            connectionEpoch,
            frame.Sequence,
            frame.ObservedAtUtc.ToDateTimeOffset(),
            receivedAtUtc,
            frame.Cpu is null
                ? null
                : new ApplicationTelemetryCpu(
                    frame.Cpu.UsagePercent,
                    frame.Cpu.HasLoadAverage ? frame.Cpu.LoadAverage : null,
                    frame.Cpu.HasProcessCount ? frame.Cpu.ProcessCount : null),
            frame.Memory is null
                ? null
                : new ApplicationTelemetryMemory(
                    frame.Memory.TotalMb,
                    frame.Memory.UsedMb,
                    frame.Memory.AvailableMb,
                    frame.Memory.UsagePercent),
            frame.Disks.Select(disk => new ApplicationTelemetryDisk(
                disk.Scope.Trim(),
                disk.TotalGb,
                disk.UsedGb,
                disk.FreeGb,
                disk.UsagePercent)).ToArray(),
            frame.Networks.Select(network => new ApplicationTelemetryNetwork(
                network.Scope.Trim(),
                network.RxBytesPerSec,
                network.TxBytesPerSec)).ToArray(),
            frame.TransportHealth is null
                ? null
                : new ApplicationTelemetryTransportHealth(
                    frame.TransportHealth.UptimeSeconds,
                    NullIfWhiteSpace(frame.TransportHealth.AgentVersion),
                    NullIfWhiteSpace(frame.TransportHealth.OsVersion),
                    frame.TransportHealth.LastHeartbeat?.ToDateTimeOffset()),
            isAuthoritative ? "akka" : "unavailable",
            isAuthoritative);

    internal static void ThrowIfInvalid(AgentFrameValidationResult validation)
    {
        if (!validation.IsValid)
        {
            throw new RpcException(new Status(validation.StatusCode, validation.Error));
        }
    }

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
