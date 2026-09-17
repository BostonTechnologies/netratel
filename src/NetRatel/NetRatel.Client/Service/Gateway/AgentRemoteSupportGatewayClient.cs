using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Shared.Contracts.RemoteSupport;
using V2Negotiation = NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2NegotiationEnvelope;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Agent leg for the V2 remote-support signalling gateway. The stream only
/// carries bounded signalling envelopes; terminal/PTY traffic and WebRTC media
/// remain outside this transport.
/// </summary>
public sealed class AgentRemoteSupportGatewayClient(GatewayClientOptions options, Action<string> log)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly object _helperPipeHostSync = new();
    private RemoteDesktopUserHelperPipeHost? _helperPipeHost;
    private bool _helperPipeHostInitialized;

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!options.RemoteSupportGatewayEnabled)
        {
            return;
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Remote-support gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

        using var transitionEffects = options.RemoteSupportV2MediaEnabled && options.RemoteSupportV2InventoryEnabled
            ? new RemoteSupportTransitionEffectQueue()
            : null;
        using var v2Media = options.RemoteSupportV2MediaEnabled
            ? new V2GatewayRemoteSupportBridge(EnsureGatewayHelperPipeHost, log, session, transitionEffects)
            : null;
        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var signalling = RunStreamAsync(endpoint, session, accessToken, v2Media, stoppingToken);
                var preparation = options.RemoteSupportV2InventoryEnabled
                    ? new AgentRemoteSupportPreparationGatewayClient(
                            options,
                            log,
                            EnsureGatewayHelperPipeHost,
                            v2Media is null ? null : v2Media.RememberPreparedRouteAsync,
                            v2Media is null ? null : v2Media.PrepareConsoleProviderAsync,
                            transitionEffects)
                        .RunForPresenceSessionAsync(endpoint, session, accessToken, stoppingToken)
                    : Task.CompletedTask;
                await Task.WhenAll(signalling, preparation).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                log($"Remote-support gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(
        Uri endpoint,
        GatewayPresenceSession session,
        string accessToken,
        V2GatewayRemoteSupportBridge? v2Media,
        CancellationToken stoppingToken)
    {
        var helperPipeHost = EnsureGatewayHelperPipeHost();
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new AgentRemoteSupportGateway.AgentRemoteSupportGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        using var writer = new RemoteSupportGatewayWriter(call.RequestStream, session, options.ProtocolVersion);
        var localSignals = Channel.CreateBounded<RemoteSupportPipeSignal>(new BoundedChannelOptions(64)
        {
            // WebRTC signalling is ordered control data. Do not discard an
            // SDP/ICE envelope under pressure: terminate this stream and let
            // the presence-session retry establish a clean gateway session.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var localSignalOverflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = v2Media is null ? new GatewayRemoteSupportBridge(signal =>
        {
            if (localSignals.Writer.TryWrite(signal))
            {
                return true;
            }

            localSignalOverflow.TrySetException(new RpcException(new Status(
                StatusCode.ResourceExhausted,
                "The local remote-support signalling queue is full.")));
            return false;
        }, helperPipeHost) : null;
        var localSignalWriter = bridge is null
            ? Task.CompletedTask
            : WriteLocalSignalsAsync(localSignals.Reader, writer, stoppingToken);

        try
        {
            await writer.WriteAsync(new AgentRemoteSupportFrame
            {
                Sequence = 0,
                Hello = new AgentRemoteSupportHello { Capabilities = { "webrtc-signalling", "ice", "ordered-session-signals" } }
            }, stoppingToken).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayRemoteSupportFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Remote-support gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session);
            log($"Remote-support gateway admitted. authority={call.ResponseStream.Current.Accepted.SupportAuthority}.");
            if (v2Media is not null)
            {
                await v2Media.AttachAsync(writer, stoppingToken).ConfigureAwait(false);
            }

            ulong lastServerSequence = 0;
            while (true)
            {
                var nextFrame = call.ResponseStream.MoveNext(stoppingToken);
                if (await Task.WhenAny(nextFrame, localSignalOverflow.Task).ConfigureAwait(false) == localSignalOverflow.Task)
                {
                    await localSignalOverflow.Task.ConfigureAwait(false);
                }

                if (!await nextFrame.ConfigureAwait(false))
                {
                    break;
                }

                var frame = call.ResponseStream.Current;
                ValidateFrame(frame, session);
                if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support gateway returned a stale or out-of-order frame."));
                }

                lastServerSequence = frame.Sequence;
                switch (frame.PayloadCase)
                {
                    case GatewayRemoteSupportFrame.PayloadOneofCase.Signal:
                        if (bridge is null)
                        {
                            throw new RpcException(new Status(StatusCode.DataLoss, "The V2 media gateway returned a legacy signalling frame."));
                        }

                        await bridge.HandleAsync(frame.Signal, writer, stoppingToken).ConfigureAwait(false);
                        break;
                    case GatewayRemoteSupportFrame.PayloadOneofCase.V2Envelope when v2Media is not null:
                        await v2Media.HandleAsync(frame.V2Envelope, stoppingToken).ConfigureAwait(false);
                        break;
                    case GatewayRemoteSupportFrame.PayloadOneofCase.Closed:
                        if (bridge is not null)
                        {
                            await bridge.CloseAsync(frame.Closed.SessionId, frame.Closed.Reason).ConfigureAwait(false);
                            await writer.WriteClosedAsync(frame.Closed.SessionId, "agent_acknowledged_close", stoppingToken).ConfigureAwait(false);
                        }
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support gateway returned an unsupported frame."));
                }
            }
        }
        finally
        {
            v2Media?.Detach();
            localSignals.Writer.TryComplete();
            try
            {
                await localSignalWriter.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                log("Remote-support gateway local signal writer stopped with the owning presence session.");
            }
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (RpcException exception)
            {
                log($"Remote-support gateway request stream was already closed by the server: {exception.StatusCode}.");
            }
        }
    }

    private static async Task WriteLocalSignalsAsync(
        ChannelReader<RemoteSupportPipeSignal> reader,
        RemoteSupportGatewayWriter writer,
        CancellationToken cancellationToken)
    {
        await foreach (var signal in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteSignalAsync(signal, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ValidateAccepted(GatewayRemoteSupportFrame frame, GatewayPresenceSession session)
    {
        ValidateFrame(frame, session);
        if (!GatewayAuthority.IsAkka(frame.Accepted.SupportAuthority))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Remote-support gateway did not admit the expected authority."));
        }
    }

    private void ValidateFrame(GatewayRemoteSupportFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support gateway returned a frame for another presence session."));
        }
    }

    private RemoteDesktopUserHelperPipeHost? EnsureGatewayHelperPipeHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        lock (_helperPipeHostSync)
        {
            if (_helperPipeHostInitialized)
            {
                return _helperPipeHost;
            }

            _helperPipeHostInitialized = true;
            try
            {
#pragma warning disable CA1416
                _helperPipeHost = new RemoteDesktopUserHelperPipeHost();
                _helperPipeHost.Start();
                var helperTask = new RemoteDesktopUserHelperTask();
                helperTask.EnsureLauncherAndRunKey();
                helperTask.TryRegisterScheduledTask();
                helperTask.StartForActiveConsoleSession();
#pragma warning restore CA1416
                log("Remote-support gateway Windows helper host started without Spacetime transport.");
            }
            catch (Exception exception)
            {
                _helperPipeHost?.Dispose();
                _helperPipeHost = null;
                log($"Remote-support gateway could not start the Windows helper host: {exception.Message}");
            }

            return _helperPipeHost;
        }
    }

    private sealed class RemoteSupportGatewayWriter(IClientStreamWriter<AgentRemoteSupportFrame> stream, GatewayPresenceSession session, string protocolVersion) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, ulong> _sessionSequences = new(StringComparer.Ordinal);
        private ulong _sequence;

        public async Task WriteSignalAsync(RemoteSupportPipeSignal signal, CancellationToken cancellationToken)
        {
            var sessionSequence = _sessionSequences.AddOrUpdate(signal.SessionId, 1, static (_, current) => current + 1);
            await WriteAsync(new AgentRemoteSupportFrame
            {
                Signal = new RemoteSupportSignal
                {
                    SessionId = signal.SessionId,
                    MessageId = Guid.NewGuid().ToString("N"),
                    SignalType = signal.SignalType,
                    SessionSequence = sessionSequence,
                    Payload = ByteString.CopyFromUtf8(signal.PayloadJson)
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public Task WriteClosedAsync(string sessionId, string reason, CancellationToken cancellationToken) =>
            WriteAsync(new AgentRemoteSupportFrame { Closed = new RemoteSupportSessionClosed { SessionId = sessionId, Reason = reason } }, cancellationToken);

        public Task WriteV2RegistrationAsync(RemoteSupportSessionKey session, long routeGeneration, CancellationToken cancellationToken) =>
            WriteAsync(new AgentRemoteSupportFrame
            {
                V2EdgeRegistration = new RemoteSupportV2AgentEdgeRegistration
                {
                    Session = ToProto(session),
                    RouteGeneration = checked((ulong)routeGeneration)
                }
            }, cancellationToken);

        public Task WriteV2EnvelopeAsync(
            V2Negotiation envelope,
            Guid edgeRouteId,
            long routeGeneration,
            CancellationToken cancellationToken)
        {
            var negotiation = new NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2NegotiationEnvelope
            {
                Session = ToProto(envelope.Session),
                NegotiationGeneration = checked((ulong)envelope.Generation),
                Direction = envelope.Direction,
                Sequence = checked((ulong)envelope.Sequence),
                MessageId = envelope.MessageId.ToString("D"),
                SignalType = envelope.SignalType,
                Payload = ByteString.CopyFrom(envelope.Payload),
                HasIceConfiguration = envelope.IceConfiguration is not null,
                IceExpiresUnixMs = envelope.IceConfiguration?.ExpiresAtUtc.ToUnixTimeMilliseconds() ?? 0
            };
            if (envelope.IceConfiguration is { } configuration)
            {
                negotiation.IceServers.Add(configuration.Servers.Select(server =>
                {
                    var mapped = new NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2IceServer
                    {
                        Username = server.Username ?? string.Empty,
                        Credential = server.Credential ?? string.Empty
                    };
                    mapped.Urls.Add(server.Urls);
                    return mapped;
                }));
            }

            return WriteAsync(new AgentRemoteSupportFrame
            {
                V2Envelope = new RemoteSupportV2RouteEnvelope
                {
                    Session = ToProto(envelope.Session),
                    EdgeRouteId = edgeRouteId.ToString("D"),
                    RouteGeneration = checked((ulong)routeGeneration),
                    Kind = "v2_negotiation",
                    Negotiation = negotiation
                }
            }, cancellationToken);
        }

        public Task WriteV2TransitionEvidenceAsync(
            NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2TransitionEvidence evidence,
            Guid edgeRouteId,
            long routeGeneration,
            CancellationToken cancellationToken) =>
            WriteAsync(new AgentRemoteSupportFrame
            {
                V2Envelope = new RemoteSupportV2RouteEnvelope
                {
                    Session = ToProto(evidence.Session),
                    EdgeRouteId = edgeRouteId.ToString("D"),
                    RouteGeneration = checked((ulong)routeGeneration),
                    Kind = "v2_transition_evidence",
                    Payload = ByteString.CopyFrom(new NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2TransitionEvidence
                    {
                        EvidenceId = evidence.EvidenceId.ToString("D"),
                        NegotiationGeneration = checked((ulong)evidence.NegotiationGeneration),
                        TransitionSequence = evidence.TransitionSequence,
                        ObservedUnixMs = evidence.ObservedAtUtc.ToUnixTimeMilliseconds(),
                        EvidenceKind = evidence.EvidenceKind,
                        WindowsSessionId = evidence.WindowsSessionId ?? 0,
                        HasWindowsSessionId = evidence.WindowsSessionId.HasValue,
                        UserSidHash = evidence.UserSidHash ?? string.Empty,
                        ActiveConsoleSessionId = evidence.ActiveConsoleSessionId ?? 0,
                        HasActiveConsoleSessionId = evidence.ActiveConsoleSessionId.HasValue,
                        WindowsSessionState = evidence.WindowsSessionState ?? string.Empty,
                        DesktopKind = evidence.DesktopKind ?? string.Empty,
                        ProviderKind = evidence.ProviderKind ?? string.Empty,
                        HelperRouteId = evidence.HelperRouteId?.ToString("D") ?? string.Empty,
                        HasHelperRouteId = evidence.HelperRouteId.HasValue,
                        InventorySequence = evidence.InventorySequence ?? 0,
                        HasInventorySequence = evidence.InventorySequence.HasValue
                    }.ToByteArray())
                }
            }, cancellationToken);

        private static RemoteSupportV2SessionKey ToProto(RemoteSupportSessionKey session) => new()
        {
            TenantId = session.TenantId,
            AgentId = session.AgentId.ToString("D"),
            RemoteSupportSessionId = session.RemoteSupportSessionId.ToString("D")
        };

        public async Task WriteAsync(AgentRemoteSupportFrame frame, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                frame.ProtocolVersion = protocolVersion;
                frame.TenantId = session.TenantId;
                frame.ClientId = session.AgentId.ToString("D");
                frame.ConnectionEpoch = session.ConnectionEpoch;
                frame.ConnectionId = session.ConnectionId.ToString("D");
                frame.Sequence = frame.PayloadCase == AgentRemoteSupportFrame.PayloadOneofCase.Hello ? 0 : checked(++_sequence);
                await stream.WriteAsync(frame).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }

    /// <summary>
    /// Owns prepared V2 helper routes for the whole presence lifetime. A gRPC
    /// retry only detaches its writer; it never disposes the retained WebRTC
    /// manager or forgets an authenticated preparation.
    /// </summary>
    private sealed class V2GatewayRemoteSupportBridge : IDisposable, IRemoteSupportTransitionEvidenceSink
    {
        private readonly object _sync = new();
        private readonly GatewayPresenceSession _presence;
        private readonly Action<string> _log;
        private readonly RemoteSupportTransitionEffectQueue? _transitionEffects;
        private readonly RemoteSupportSessionManager _providerRuntime;
        private readonly ConcurrentDictionary<RemoteSupportSessionKey, V2PreparedRoute> _routes = [];
        private RemoteSupportGatewayWriter? _writer;
        private bool _disposed;

        public V2GatewayRemoteSupportBridge(
            Func<RemoteDesktopUserHelperPipeHost?> helperPipeHost,
            Action<string> log,
            GatewayPresenceSession presence,
            RemoteSupportTransitionEffectQueue? transitionEffects)
        {
            _presence = presence;
            _log = log;
            _transitionEffects = transitionEffects;
            _providerRuntime = new RemoteSupportSessionManager(
                OperatingSystem.IsWindows() ? helperPipeHost() : null,
                PublishProviderSignal,
                transitionEvidenceSink: this);
        }

        public async Task RememberPreparedRouteAsync(RemoteSupportPreparedTargetResult prepared, CancellationToken cancellationToken)
        {
            if (prepared.Session is not { } session || session.TenantId != _presence.TenantId || session.AgentId != _presence.AgentId ||
                !prepared.TargetValid || !prepared.ProviderReady || prepared.HelperRoute is null ||
                !RemoteSupportV2PreparationValidator.TryValidate(prepared, out _))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "The V2 preparation result is not an exact active helper route."));
            }

            var route = _routes.AddOrUpdate(session,
                _ => new V2PreparedRoute(prepared),
                (_, existing) => existing.Replace(prepared));
            var writer = GetWriter();
            if (writer is not null)
            {
                await writer.WriteV2RegistrationAsync(session, route.RouteGeneration, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<RemoteSupportConsoleProviderReadiness?> PrepareConsoleProviderAsync(
            RemoteSupportPrepareTargetCommand command,
            CancellationToken cancellationToken) =>
            _providerRuntime.PrepareV2ConsoleProviderAsync(command, cancellationToken);

        public async Task AttachAsync(RemoteSupportGatewayWriter writer, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _writer = writer;
            }

            foreach (var entry in _routes)
            {
                await writer.WriteV2RegistrationAsync(entry.Key, entry.Value.RouteGeneration, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Detach()
        {
            lock (_sync)
            {
                _writer = null;
            }
        }

        public async Task HandleAsync(RemoteSupportV2RouteEnvelope candidate, CancellationToken cancellationToken)
        {
            if (!TryGetSession(candidate.Session, _presence, out var session) ||
                !_routes.TryGetValue(session!, out var route) || !Guid.TryParse(candidate.EdgeRouteId, out var edgeRouteId) ||
                candidate.RouteGeneration is 0 or > long.MaxValue || checked((long)candidate.RouteGeneration) != route.RouteGeneration)
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "The V2 media edge envelope is not bound to a prepared route."));
            }

            var supportSession = session!;
            route.SetEdgeRoute(edgeRouteId);
            if (string.Equals(candidate.Kind, "v2_transition_effect", StringComparison.Ordinal))
            {
                RemoteSupportTransitionEffect? effect;
                try
                {
                    effect = JsonSerializer.Deserialize(
                        candidate.Payload.Span,
                        RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect);
                }
                catch (JsonException)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "The V2 transition effect payload is invalid."));
                }

                if (effect is null || effect.Session != supportSession || effect.EffectId == Guid.Empty ||
                    effect.TransitionId == Guid.Empty || effect.PresenceEpoch != _presence.ConnectionEpoch ||
                    effect.NegotiationGeneration != route.NegotiationGeneration ||
                    !IsSupportedTransitionEffect(effect))
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "The V2 transition effect is stale or not bound to the prepared route."));
                }

                if (_transitionEffects is null || !_transitionEffects.TryEnqueue(effect))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "The V2 transition effect queue is unavailable."));
                }

                return;
            }

            if (string.Equals(candidate.Kind, "v2_control", StringComparison.Ordinal))
            {
                RemoteSupportV2PrivilegedControl? control;
                try
                {
                    control = JsonSerializer.Deserialize(
                        candidate.Payload.Span,
                        RemoteSupportV2JsonContext.Default.RemoteSupportV2PrivilegedControl);
                }
                catch (JsonException)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "The V2 privileged control payload is invalid."));
                }

                if (control is null || control.Session != supportSession ||
                    control.NegotiationGeneration != route.NegotiationGeneration ||
                    control.Target != route.Prepared.Target ||
                    control.HelperRouteId != route.Prepared.HelperRoute?.HelperRouteId)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "The V2 privileged control is stale or not bound to the prepared route."));
                }

                var resultCode = await _providerRuntime.ExecuteV2PrivilegedControlAsync(
                    supportSession.RemoteSupportSessionId.ToString("D"), route.Prepared, control, cancellationToken).ConfigureAwait(false);
                await PublishStatusAsync(route, resultCode, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (candidate.Negotiation is null)
            {
                // A reconnect control marker contains no SDP/ICE. The browser
                // receives its own transient edge loss and will wait for a
                // fresh ReadyForOffer lifecycle generation.
                _providerRuntime.CloseGatewaySession(supportSession.RemoteSupportSessionId.ToString("D"), "v2_edge_renegotiation_required");
                return;
            }

            if (!TryMapNegotiation(candidate.Negotiation, supportSession, out var envelope))
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "The V2 negotiation envelope is invalid or stale."));
            }

            if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Status &&
                System.Text.Encoding.UTF8.GetString(envelope.Payload).Contains("\"renegotiation_required\"", StringComparison.Ordinal))
            {
                route.SetNegotiationGeneration(envelope.Generation);
                _providerRuntime.CloseGatewaySession(supportSession.RemoteSupportSessionId.ToString("D"), "v2_edge_renegotiation_required");
                return;
            }

            if (envelope.Generation != route.NegotiationGeneration)
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "The V2 negotiation generation is stale."));
            }

            var logicalSessionId = supportSession.RemoteSupportSessionId.ToString("D");
            if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Offer &&
                !_providerRuntime.OpenPreparedV2Session(logicalSessionId, route.Prepared, envelope.Generation, envelope.IceConfiguration))
            {
                await PublishStatusAsync(route, "helper_route_invalid", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_providerRuntime.ProcessPreparedV2Signal(
                    new RemoteSupportPipeSignal(logicalSessionId, envelope.SignalType, System.Text.Encoding.UTF8.GetString(envelope.Payload)),
                    checked((ulong)envelope.Sequence), envelope.Generation))
            {
                await PublishStatusAsync(route, "helper_route_invalid", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Offer)
            {
                await PublishStatusAsync(route, "input_ready", cancellationToken).ConfigureAwait(false);
            }
        }

        private void PublishProviderSignal(RemoteSupportPipeSignal signal)
        {
            V2PreparedRoute? route = null;
            foreach (var entry in _routes)
            {
                if (string.Equals(entry.Key.RemoteSupportSessionId.ToString("D"), signal.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    route = entry.Value;
                    break;
                }
            }
            if (route is null || route.EdgeRouteId == Guid.Empty || !TryMapProviderSignalType(signal.SignalType, out var signalType))
            {
                return;
            }

            var writer = GetWriter();
            if (writer is null)
            {
                return;
            }

            var envelope = new V2Negotiation(
                route.Session,
                route.NegotiationGeneration,
                RemoteSupportV2NegotiationDirections.Agent,
                route.NextSequence(),
                Guid.NewGuid(),
                signalType,
                System.Text.Encoding.UTF8.GetBytes(signal.PayloadJson));
            _ = SendAsync(writer, envelope, route);
        }

        public void Report(RemoteSupportTransitionEvidenceFact fact)
        {
            V2PreparedRoute? route = null;
            foreach (var entry in _routes)
            {
                if (string.Equals(entry.Key.RemoteSupportSessionId.ToString("D"), fact.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    route = entry.Value;
                    break;
                }
            }

            var writer = GetWriter();
            if (route is null || writer is null || route.EdgeRouteId == Guid.Empty)
            {
                return;
            }

            var evidence = new NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2TransitionEvidence(
                route.Session,
                Guid.NewGuid(),
                route.NegotiationGeneration,
                route.NextTransitionSequence(),
                DateTimeOffset.UtcNow,
                fact.Kind,
                fact.WindowsSessionId,
                fact.UserSidHash,
                fact.ActiveConsoleSessionId,
                fact.WindowsSessionState,
                fact.DesktopKind,
                fact.ProviderKind,
                fact.HelperRouteId ?? route.Prepared.HelperRoute?.HelperRouteId);
            _ = SendTransitionEvidenceAsync(writer, evidence, route);
        }

        private async Task PublishStatusAsync(V2PreparedRoute route, string code, CancellationToken cancellationToken)
        {
            var writer = GetWriter();
            if (writer is null || route.EdgeRouteId == Guid.Empty)
            {
                return;
            }

            var envelope = new V2Negotiation(
                route.Session,
                route.NegotiationGeneration,
                RemoteSupportV2NegotiationDirections.Agent,
                route.NextSequence(),
                Guid.NewGuid(),
                RemoteSupportV2NegotiationSignalTypes.Status,
                System.Text.Encoding.UTF8.GetBytes($"{{\"code\":\"{code}\"}}"));
            await SendAsync(writer, envelope, route, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendAsync(RemoteSupportGatewayWriter writer, V2Negotiation envelope, V2PreparedRoute route, CancellationToken cancellationToken = default)
        {
            try
            {
                await writer.WriteV2EnvelopeAsync(envelope, route.EdgeRouteId, route.RouteGeneration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RpcException or IOException or OperationCanceledException)
            {
                _log($"Remote-support V2 media signal will renegotiate after edge recovery: {exception.GetType().Name}.");
            }
        }

        private async Task SendTransitionEvidenceAsync(
            RemoteSupportGatewayWriter writer,
            NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2TransitionEvidence evidence,
            V2PreparedRoute route)
        {
            try
            {
                await writer.WriteV2TransitionEvidenceAsync(evidence, route.EdgeRouteId, route.RouteGeneration, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RpcException or IOException or OperationCanceledException)
            {
                _log($"Remote-support transition evidence will be reacquired after edge recovery: {exception.GetType().Name}.");
            }
        }

        private RemoteSupportGatewayWriter? GetWriter()
        {
            lock (_sync)
            {
                return _writer;
            }
        }

        private static bool TryGetSession(RemoteSupportV2SessionKey? candidate, GatewayPresenceSession presence, out RemoteSupportSessionKey? session)
        {
            session = null;
            if (candidate is null || candidate.TenantId != presence.TenantId || !Guid.TryParse(candidate.AgentId, out var agentId) ||
                agentId != presence.AgentId || !Guid.TryParse(candidate.RemoteSupportSessionId, out var sessionId) || sessionId == Guid.Empty)
            {
                return false;
            }

            session = new RemoteSupportSessionKey(presence.TenantId, presence.AgentId, sessionId);
            return true;
        }

        private static bool TryMapNegotiation(
            NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2NegotiationEnvelope candidate,
            RemoteSupportSessionKey session,
            out V2Negotiation envelope)
        {
            envelope = null!;
            if (candidate.Session is null || candidate.Session.TenantId != session.TenantId ||
                !Guid.TryParse(candidate.Session.AgentId, out var agentId) || agentId != session.AgentId ||
                !Guid.TryParse(candidate.Session.RemoteSupportSessionId, out var sessionId) || sessionId != session.RemoteSupportSessionId ||
                candidate.NegotiationGeneration is 0 or > long.MaxValue || candidate.Sequence is 0 or > long.MaxValue ||
                !Guid.TryParse(candidate.MessageId, out var messageId) || messageId == Guid.Empty)
            {
                return false;
            }

            RemoteSupportSessionIceConfiguration? iceConfiguration = null;
            if (candidate.HasIceConfiguration)
            {
                if (candidate.IceExpiresUnixMs <= 0 || candidate.IceServers.Any(server => server.Urls.Count == 0))
                {
                    return false;
                }

                iceConfiguration = new RemoteSupportSessionIceConfiguration(
                    session,
                    checked((long)candidate.NegotiationGeneration),
                    candidate.IceServers.Select(server => new RemoteSupportIceServerDto(server.Urls.ToArray(), server.Username, server.Credential)).ToArray(),
                    DateTimeOffset.FromUnixTimeMilliseconds(candidate.IceExpiresUnixMs));
            }

            envelope = new V2Negotiation(session, checked((long)candidate.NegotiationGeneration), candidate.Direction,
                checked((long)candidate.Sequence), messageId, candidate.SignalType, candidate.Payload.ToByteArray(), iceConfiguration);
            return RemoteSupportV2ContractValidator.TryValidate(envelope, out _);
        }

        private static bool TryMapProviderSignalType(string signalType, out string mapped)
        {
            mapped = signalType.Trim().ToLowerInvariant() switch
            {
                RemoteSupportSignalTypes.Answer => RemoteSupportV2NegotiationSignalTypes.Answer,
                RemoteSupportSignalTypes.Ice => RemoteSupportV2NegotiationSignalTypes.Ice,
                _ => string.Empty
            };
            return mapped.Length > 0;
        }

        private static bool IsSupportedTransitionEffect(RemoteSupportTransitionEffect effect) => effect.Kind switch
        {
            RemoteSupportTransitionEffectKinds.ReacquireInventory => effect.Target is null,
            RemoteSupportTransitionEffectKinds.PrepareReplacementTarget => effect.Target is { WindowsSessionId: > 0 } target &&
                string.Equals(target.Kind, RemoteSupportV2TargetKinds.InteractiveUser, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(target.UserSidHash) && target.InventorySequence > 0,
            RemoteSupportTransitionEffectKinds.Cancel => effect.Target is null,
            _ => false
        };

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(V2GatewayRemoteSupportBridge));
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _writer = null;
            }

            _providerRuntime.Dispose();
        }

        private sealed class V2PreparedRoute
        {
            private long _agentSequence;
            private long _transitionSequence;

            public V2PreparedRoute(RemoteSupportPreparedTargetResult prepared)
            {
                Prepared = prepared;
                Session = prepared.Session!;
            }

            public RemoteSupportPreparedTargetResult Prepared { get; private set; }
            public RemoteSupportSessionKey Session { get; }
            public long NegotiationGeneration { get; private set; } = 1;
            public long RouteGeneration { get; } = 1;
            public Guid EdgeRouteId { get; private set; }
            public long NextSequence() => Interlocked.Increment(ref _agentSequence);
            public ulong NextTransitionSequence() => checked((ulong)Interlocked.Increment(ref _transitionSequence));
            public void SetEdgeRoute(Guid edgeRouteId) => EdgeRouteId = edgeRouteId;
            public void SetNegotiationGeneration(long negotiationGeneration)
            {
                if (negotiationGeneration > 0)
                {
                    NegotiationGeneration = negotiationGeneration;
                    Interlocked.Exchange(ref _agentSequence, 0);
                }
            }
            public V2PreparedRoute Replace(RemoteSupportPreparedTargetResult prepared)
            {
                Prepared = prepared;
                EdgeRouteId = Guid.Empty;
                NegotiationGeneration = 1;
                Interlocked.Exchange(ref _agentSequence, 0);
                Interlocked.Exchange(ref _transitionSequence, 0);
                return this;
            }
        }
    }

    private sealed class GatewayRemoteSupportBridge : IDisposable
    {
        private readonly Func<RemoteSupportPipeSignal, bool> _publish;
        private readonly RemoteSupportSessionManager _providerRuntime;

        public GatewayRemoteSupportBridge(
            Func<RemoteSupportPipeSignal, bool> publish,
            RemoteDesktopUserHelperPipeHost? helperPipeHost)
        {
            _publish = publish;
            _providerRuntime = new RemoteSupportSessionManager(helperPipeHost, signal => PublishOrThrow(signal));
        }

        public async Task HandleAsync(RemoteSupportSignal signal, RemoteSupportGatewayWriter writer, CancellationToken cancellationToken)
        {
            if (signal.Payload.Length > 32 * 1024 || string.IsNullOrWhiteSpace(signal.SessionId) || string.IsNullOrWhiteSpace(signal.SignalType))
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "Remote-support gateway returned an invalid signalling envelope."));
            }

            var payload = signal.Payload.ToStringUtf8();
            switch (signal.SignalType.Trim().ToLowerInvariant())
            {
                case "open":
                    _providerRuntime.OpenGatewaySession(signal.SessionId, payload);
                    break;
                case RemoteSupportSignalTypes.Offer:
                    _providerRuntime.ProcessGatewaySignal(
                        new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, payload),
                        signal.SessionSequence);
                    break;
                case RemoteSupportSignalTypes.Ice:
                    _providerRuntime.ProcessGatewaySignal(
                        new RemoteSupportPipeSignal(signal.SessionId, signal.SignalType, payload),
                        signal.SessionSequence);
                    break;
                case RemoteSupportSignalTypes.Close:
                    _providerRuntime.CloseGatewaySession(signal.SessionId, "browser_closed");
                    await writer.WriteClosedAsync(signal.SessionId, "browser_closed", cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    PublishOrThrow(new RemoteSupportPipeSignal(signal.SessionId, RemoteSupportSignalTypes.Error, "{\"code\":\"unsupported_signal\"}"));
                    break;
            }
        }

        public Task CloseAsync(string sessionId, string reason)
        {
            _providerRuntime.CloseGatewaySession(sessionId, reason);
            return Task.CompletedTask;
        }

        private void PublishOrThrow(RemoteSupportPipeSignal signal)
        {
            if (!_publish(signal))
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "The local remote-support signalling queue is full."));
            }
        }

        public void Dispose()
        {
            _providerRuntime.Dispose();
        }
    }
}
