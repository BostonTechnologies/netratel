using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Publishes agent-owned WTS readiness and handles exact-target preparation.
/// This stream is deliberately separate from retained WebRTC signalling.
/// </summary>
internal sealed class AgentRemoteSupportPreparationGatewayClient(
    GatewayClientOptions options,
    Action<string> log,
    Func<RemoteDesktopUserHelperPipeHost?> helperPipeHost,
    Func<RemoteSupportPreparedTargetResult, CancellationToken, Task>? preparedRouteSink = null,
    Func<RemoteSupportPrepareTargetCommand, CancellationToken, Task<RemoteSupportConsoleProviderReadiness?>>? prepareConsoleProvider = null,
    RemoteSupportTransitionEffectQueue? transitionEffects = null)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(
        Uri endpoint,
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken stoppingToken)
    {
        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunStreamAsync(endpoint, session, accessToken, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                log($"Remote-support V2 preparation gateway failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new AgentRemoteSupportPreparationGateway.AgentRemoteSupportPreparationGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        using var writer = new PreparationGatewayWriter(call.RequestStream, session, options.ProtocolVersion);
        var supervisor = CreateSupervisor();
        using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        try
        {
            await writer.WriteAsync(new AgentRemoteSupportPreparationFrame
            {
                Sequence = 0,
                Hello = new AgentRemoteSupportPreparationHello
                {
                    Capabilities =
                    {
                        GetAdvertisedCapabilities()
                    }
                }
            }, stoppingToken).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayRemoteSupportPreparationFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Remote-support V2 preparation gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session);
            log($"Remote-support V2 preparation gateway admitted. authority={call.ResponseStream.Current.Accepted.PreparationAuthority}.");
            await PublishInventoryAsync(supervisor, writer, session, stoppingToken).ConfigureAwait(false);

            var publisher = PublishInventoryPeriodicallyAsync(supervisor, writer, session, publishCts.Token);
            var effectWorker = transitionEffects is null
                ? Task.CompletedTask
                : ProcessTransitionEffectsAsync(transitionEffects.Reader, supervisor, writer, session, publishCts.Token);
            try
            {
                ulong lastServerSequence = 0;
                while (true)
                {
                    var nextFrame = call.ResponseStream.MoveNext(stoppingToken);
                    if (transitionEffects is not null &&
                        await Task.WhenAny(nextFrame, effectWorker).ConfigureAwait(false) == effectWorker)
                    {
                        await effectWorker.ConfigureAwait(false);
                    }

                    if (!await nextFrame.ConfigureAwait(false))
                    {
                        break;
                    }

                    var frame = call.ResponseStream.Current;
                    ValidateFrame(frame, session);
                    if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                    {
                        throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support V2 preparation gateway returned a stale or out-of-order frame."));
                    }

                    lastServerSequence = frame.Sequence;
                    switch (frame.PayloadCase)
                    {
                        case GatewayRemoteSupportPreparationFrame.PayloadOneofCase.InventoryRefresh:
                            await PublishInventoryAsync(supervisor, writer, session, stoppingToken).ConfigureAwait(false);
                            break;
                        case GatewayRemoteSupportPreparationFrame.PayloadOneofCase.PrepareTarget:
                            await PrepareTargetAsync(frame.PrepareTarget, supervisor, writer, session, stoppingToken).ConfigureAwait(false);
                            break;
                        default:
                            throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support V2 preparation gateway returned an unsupported frame."));
                    }
                }
            }
            finally
            {
                publishCts.Cancel();
                try
                {
                    await publisher.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (publishCts.IsCancellationRequested)
                {
                    log("Remote-support V2 inventory publisher stopped with its preparation stream.");
                }

                try
                {
                    await effectWorker.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (publishCts.IsCancellationRequested)
                {
                    log("Remote-support V2 transition effect worker stopped with its preparation stream.");
                }
            }
        }
        finally
        {
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (RpcException exception)
            {
                log($"Remote-support V2 preparation request stream was already closed by the gateway: {exception.StatusCode}.");
            }
        }
    }

    private RemoteSupportV2TargetSupervisor CreateSupervisor()
    {
#pragma warning disable CA1416
        var pipeHost = helperPipeHost();
        var serviceVersion = typeof(AgentRemoteSupportPreparationGatewayClient).Assembly.GetName().Version?.ToString() ?? string.Empty;
        var source = new GatewayRemoteSupportWindowsSessionSource(pipeHost, serviceVersion);
        RemoteSupportInteractiveHelperRepairService? repair = OperatingSystem.IsWindows()
            ? new RemoteSupportInteractiveHelperRepairService(pipeHost, null, serviceVersion)
            : null;
        return new RemoteSupportV2TargetSupervisor(
            source,
            sessionId => pipeHost?.GetConnectedHelper(sessionId),
            async (requestId, sessionId, staleHelper, cancellationToken) =>
            {
                if (repair is null)
                {
                    return null;
                }

                var result = staleHelper is null
                    ? await repair.TryLaunchAsync(requestId.ToString("N"), sessionId, cancellationToken).ConfigureAwait(false)
                    : await repair.TryRepairAsync(requestId.ToString("N"), sessionId, staleHelper, cancellationToken).ConfigureAwait(false);
                return result.VersionRestored ? pipeHost?.GetConnectedHelper(sessionId) : null;
            },
            serviceVersion,
            TimeProvider.System,
            prepareConsoleProvider);
#pragma warning restore CA1416
    }

    private IReadOnlyList<string> GetAdvertisedCapabilities()
    {
        var capabilities = new List<string>
        {
            RemoteSupportV2CapabilityNames.RemoteSupportV2,
            RemoteSupportV2CapabilityNames.InteractiveAssist,
            RemoteSupportV2CapabilityNames.SessionInventory,
            RemoteSupportV2CapabilityNames.TargetPreflight,
            "nonce-bound-helper-route"
        };

        // The preparation stream may run on non-Windows hosts and media is
        // independently feature-gated. Never advertise controls that cannot
        // be exercised by this edge instance.
        if (!options.RemoteSupportV2MediaEnabled || !OperatingSystem.IsWindows())
        {
            return capabilities;
        }

        capabilities.AddRange(
        [
            RemoteSupportV2CapabilityNames.DirectWebRtcMedia,
            RemoteSupportV2CapabilityNames.InputControl,
            RemoteSupportV2CapabilityNames.ConsoleLogin,
            RemoteSupportV2CapabilityNames.LockScreen,
            RemoteSupportV2CapabilityNames.SecureAttention,
            RemoteSupportV2CapabilityNames.HelperRepair,
            RemoteSupportV2CapabilityNames.DesktopTransitionRecovery
        ]);

        return capabilities;
    }

    private static async Task PublishInventoryPeriodicallyAsync(
        RemoteSupportV2TargetSupervisor supervisor,
        PreparationGatewayWriter writer,
        GatewayPresenceSession session,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(InventoryInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await PublishInventoryAsync(supervisor, writer, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task PublishInventoryAsync(
        RemoteSupportV2TargetSupervisor supervisor,
        PreparationGatewayWriter writer,
        GatewayPresenceSession session,
        CancellationToken cancellationToken) =>
        writer.WriteAsync(new AgentRemoteSupportPreparationFrame
        {
            InventorySnapshot = ToProto(supervisor.CaptureInventory(session.TenantId, session.AgentId))
        }, cancellationToken);

    private async Task ProcessTransitionEffectsAsync(
        ChannelReader<RemoteSupportTransitionEffect> effects,
        RemoteSupportV2TargetSupervisor supervisor,
        PreparationGatewayWriter writer,
        GatewayPresenceSession presence,
        CancellationToken cancellationToken)
    {
        await foreach (var effect in effects.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (effect.Session.TenantId != presence.TenantId || effect.Session.AgentId != presence.AgentId ||
                effect.EffectId == Guid.Empty || effect.TransitionId == Guid.Empty || effect.PresenceEpoch != presence.ConnectionEpoch)
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "The V2 transition effect is not bound to this presence session."));
            }

            var correlation = ToProto(effect);
            switch (effect.Kind)
            {
                case RemoteSupportTransitionEffectKinds.ReacquireInventory:
                    await writer.WriteAsync(new AgentRemoteSupportPreparationFrame
                    {
                        InventorySnapshot = ToProto(supervisor.CaptureInventory(presence.TenantId, presence.AgentId), correlation)
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case RemoteSupportTransitionEffectKinds.PrepareReplacementTarget when effect.Target is not null:
                    var now = DateTimeOffset.UtcNow;
                    var command = new RemoteSupportPrepareTargetCommand(
                        RemoteSupportV2ContractVersions.Current,
                        presence.TenantId,
                        presence.AgentId,
                        effect.EffectId,
                        effect.Operator,
                        effect.Target,
                        Guid.NewGuid(),
                        now,
                        now.AddSeconds(20),
                        effect.Session);
                    if (!RemoteSupportV2PreparationValidator.TryValidate(command, out var error))
                    {
                        throw new RpcException(new Status(StatusCode.DataLoss, error!.Message));
                    }

                    var result = await supervisor.PrepareAsync(command, cancellationToken).ConfigureAwait(false);
                    if (result.Session is not null && result.TargetValid && result.ProviderReady && preparedRouteSink is not null)
                    {
                        await preparedRouteSink(result, cancellationToken).ConfigureAwait(false);
                    }

                    await writer.WriteAsync(new AgentRemoteSupportPreparationFrame
                    {
                        PreparedTarget = ToProto(result, correlation)
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case RemoteSupportTransitionEffectKinds.Cancel:
                    // Preparation is sequential and cancellation only fences
                    // queued work; any late result remains rejected by the actor.
                    break;
                default:
                    throw new RpcException(new Status(StatusCode.DataLoss, "The V2 transition effect is unsupported."));
            }
        }
    }

    private async Task PrepareTargetAsync(
        RemoteSupportV2PrepareTarget request,
        RemoteSupportV2TargetSupervisor supervisor,
        PreparationGatewayWriter writer,
        GatewayPresenceSession session,
        CancellationToken cancellationToken)
    {
        var result = await supervisor.PrepareAsync(ToContract(request, session), cancellationToken).ConfigureAwait(false);
        if (result.Session is not null && result.TargetValid && result.ProviderReady && preparedRouteSink is not null)
        {
            await preparedRouteSink(result, cancellationToken).ConfigureAwait(false);
        }
        await writer.WriteAsync(new AgentRemoteSupportPreparationFrame
        {
            PreparedTarget = ToProto(result)
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RemoteSupportPrepareTargetCommand ToContract(RemoteSupportV2PrepareTarget request, GatewayPresenceSession session) =>
        new(
            checked((int)request.ContractVersion),
            session.TenantId,
            session.AgentId,
            ParseRequiredGuid(request.RequestId),
            new RemoteSupportOperatorBinding(request.Operator?.OperatorId ?? string.Empty),
            ToContract(request.Target),
            ParseRequiredGuid(request.RouteNonce),
            DateTimeOffset.FromUnixTimeMilliseconds(request.RequestedUnixMs),
            DateTimeOffset.FromUnixTimeMilliseconds(request.ExpiresUnixMs),
            request.HasSession ? ToContract(request.Session, session) : null);

    private static RemoteSupportTargetDescriptor ToContract(RemoteSupportV2Target? target) =>
        new(
            target?.Kind ?? string.Empty,
            target?.HasWindowsSessionId == true ? target.WindowsSessionId : null,
            string.IsNullOrWhiteSpace(target?.UserSidHash) ? null : target.UserSidHash,
            target?.HasInventorySequence == true ? target.InventorySequence : null);

    private static RemoteSupportV2InventorySnapshot ToProto(
        RemoteSupportTargetInventorySnapshot snapshot,
        RemoteSupportV2TransitionCorrelation? correlation = null)
    {
        var result = new RemoteSupportV2InventorySnapshot
        {
            ContractVersion = checked((uint)snapshot.ContractVersion),
            InventorySequence = snapshot.InventorySequence,
            ObservedUnixMs = snapshot.ObservedAtUtc.ToUnixTimeMilliseconds(),
            ExpiresUnixMs = snapshot.ExpiresAtUtc.ToUnixTimeMilliseconds()
        };
        result.Entries.AddRange(snapshot.Entries.Select(entry => new RemoteSupportV2WindowsSession
        {
            WindowsSessionId = entry.WindowsSessionId,
            State = entry.State,
            UserSidHash = entry.UserSidHash ?? string.Empty,
            IsConsoleSession = entry.IsConsoleSession,
            IsConnected = entry.IsConnected,
            IsLocked = entry.IsLocked,
            IsWinlogon = entry.IsWinlogon,
            HelperConnected = entry.HelperConnected,
            HelperVersionMatches = entry.HelperVersionMatches,
            HelperVersion = entry.HelperVersion ?? string.Empty,
            DisplayLabel = entry.DisplayLabel ?? string.Empty
        }));
        if (correlation is not null)
        {
            result.TransitionCorrelations.Add(correlation);
        }
        return result;
    }

    private static RemoteSupportV2PreparedTarget ToProto(
        RemoteSupportPreparedTargetResult result,
        RemoteSupportV2TransitionCorrelation? correlation = null) =>
        new()
        {
            ContractVersion = checked((uint)result.ContractVersion),
            RequestId = result.RequestId.ToString("D"),
            RouteNonce = result.RouteNonce.ToString("D"),
            Target = ToProto(result.Target),
            InventorySequence = result.InventorySequence,
            TargetValid = result.TargetValid,
            ProviderReady = result.ProviderReady,
            Code = result.Code,
            Message = result.Message,
            HelperRoute = result.HelperRoute is null ? null : new RemoteSupportV2HelperRoute
            {
                HelperRouteId = result.HelperRoute.HelperRouteId.ToString("D"),
                WindowsSessionId = result.HelperRoute.WindowsSessionId,
                UserSidHash = result.HelperRoute.UserSidHash,
                HelperVersion = result.HelperRoute.HelperVersion
            },
            HasHelperRoute = result.HelperRoute is not null,
            ObservedUnixMs = result.ObservedAtUtc.ToUnixTimeMilliseconds(),
            HasSession = result.Session is not null,
            Session = result.Session is { } session ? ToProto(session) : null,
            HasTransitionCorrelation = correlation is not null,
            TransitionCorrelation = correlation
        };

    private static RemoteSupportV2TransitionCorrelation ToProto(RemoteSupportTransitionEffect effect) => new()
    {
        EffectId = effect.EffectId.ToString("D"),
        Session = ToProto(effect.Session),
        TransitionId = effect.TransitionId.ToString("D"),
        PresenceEpoch = effect.PresenceEpoch
    };

    private static RemoteSupportV2Target ToProto(RemoteSupportTargetDescriptor target) =>
        new()
        {
            Kind = target.Kind,
            WindowsSessionId = target.WindowsSessionId ?? 0,
            UserSidHash = target.UserSidHash ?? string.Empty,
            InventorySequence = target.InventorySequence ?? 0,
            HasWindowsSessionId = target.WindowsSessionId.HasValue,
            HasInventorySequence = target.InventorySequence.HasValue
        };

    private static RemoteSupportSessionKey? ToContract(RemoteSupportV2SessionKey? value, GatewayPresenceSession presence) =>
        value is not null && value.TenantId == presence.TenantId &&
        Guid.TryParse(value.AgentId, out var agentId) && agentId == presence.AgentId &&
        Guid.TryParse(value.RemoteSupportSessionId, out var sessionId) && sessionId != Guid.Empty
            ? new RemoteSupportSessionKey(presence.TenantId, presence.AgentId, sessionId)
            : null;

    private static RemoteSupportV2SessionKey ToProto(RemoteSupportSessionKey session) => new()
    {
        TenantId = session.TenantId,
        AgentId = session.AgentId.ToString("D"),
        RemoteSupportSessionId = session.RemoteSupportSessionId.ToString("D")
    };

    private static Guid ParseRequiredGuid(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : Guid.Empty;

    private void ValidateAccepted(GatewayRemoteSupportPreparationFrame frame, GatewayPresenceSession session)
    {
        ValidateFrame(frame, session);
        if (!GatewayAuthority.IsAkka(frame.Accepted.PreparationAuthority))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Remote-support V2 preparation gateway did not admit the expected authority."));
        }
    }

    private void ValidateFrame(GatewayRemoteSupportPreparationFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support V2 preparation gateway returned a frame for another presence session."));
        }
    }

    private sealed class PreparationGatewayWriter(
        IClientStreamWriter<AgentRemoteSupportPreparationFrame> stream,
        GatewayPresenceSession session,
        string protocolVersion) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ulong _sequence;

        public async Task WriteAsync(AgentRemoteSupportPreparationFrame frame, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                frame.ProtocolVersion = protocolVersion;
                frame.TenantId = session.TenantId;
                frame.ClientId = session.AgentId.ToString("D");
                frame.ConnectionEpoch = session.ConnectionEpoch;
                frame.ConnectionId = session.ConnectionId.ToString("D");
                frame.Sequence = frame.PayloadCase == AgentRemoteSupportPreparationFrame.PayloadOneofCase.Hello
                    ? 0
                    : checked(++_sequence);
                await stream.WriteAsync(frame).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }
}
