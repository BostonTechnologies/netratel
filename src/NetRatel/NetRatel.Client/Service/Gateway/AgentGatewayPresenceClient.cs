using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Updates;
using NetRatel.Client.Service.Auth;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Maintains the authenticated Akka gateway presence session.
/// It deliberately has no SpacetimeDB fallback: an unavailable or wrongly configured
/// gateway leaves the agent offline rather than silently restoring legacy ownership.
/// </summary>
public sealed class AgentGatewayPresenceClient(
    GatewayClientOptions options,
    IAgentTokenService tokenService,
    int tenantId,
    Guid agentId,
    string agentVersion,
    IReadOnlyList<string> terminalShells,
    Action<string> log,
    Func<GatewayPresenceSession, string, CancellationToken, Task>? runForPresenceSession = null,
    IAgentGatewayUpdateHandler? updateHandler = null,
    Func<Uri, GrpcChannel>? createChannel = null,
    TimeSpan? extensionShutdownTimeout = null,
    Func<Uri, HttpMessageHandler>? createHttpHandler = null)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultExtensionShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _extensionShutdownTimeout = ResolveExtensionShutdownTimeout(extensionShutdownTimeout);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var retryDelay = InitialRetryDelay;
        string? lastFailureKey = null;
        long lastFailureLog = 0;
        var suppressedFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            GatewaySessionDiagnostics? diagnostics = null;
            try
            {
                var token = await DisabledAgentTokenRetry.GetAccessTokenAsync(tokenService, log, stoppingToken).ConfigureAwait(false);
                diagnostics = new GatewaySessionDiagnostics(options.Endpoint);
                await RunSessionAsync(token.AccessToken, token.ExpiresAtUtc, diagnostics, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException exception) when (stoppingToken.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
            {
                break;
            }
            catch (AgentClientAuthException exception)
            {
                log($"Agent token acquisition failed: {exception.GetType().Name}; presence remains offline.");
                throw;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException or OperationCanceledException)
            {
                var failure = (diagnostics ?? new GatewaySessionDiagnostics(options.Endpoint)).Failure(exception, retryDelay);
                if (failure.Key != lastFailureKey || Stopwatch.GetElapsedTime(lastFailureLog) >= TimeSpan.FromSeconds(30))
                {
                    log($"{failure.Message} suppressedRepeatedFailures={suppressedFailures}.");
                    lastFailureKey = failure.Key;
                    lastFailureLog = Stopwatch.GetTimestamp();
                    suppressedFailures = 0;
                }
                else
                {
                    suppressedFailures++;
                }
                try
                {
                    await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunSessionAsync(string accessToken, DateTimeOffset expiresAtUtc, GatewaySessionDiagnostics diagnostics, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Gateway:Endpoint must be an absolute HTTPS URL.");
        }

        using var channel = createChannel?.Invoke(endpoint) ?? GrpcChannel.ForAddress(endpoint,
            new GrpcChannelOptions
            {
                HttpHandler = new GatewayHttpDiagnosticsHandler(diagnostics,
                    createHttpHandler?.Invoke(endpoint) ?? new SocketsHttpHandler { EnableMultipleHttp2Connections = true }),
                DisposeHttpClient = true
            });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        var connectionId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        try
        {
            var hello = new ConnectHello
            {
                AgentVersion = agentVersion
            };
            hello.Capabilities.Add("presence");
            hello.Capabilities.Add("heartbeat-latency");
            // Retain this legacy wire token verbatim; telemetry itself now uses the V2 stream.
            hello.Capabilities.Add("telemetry-shadow");
            hello.Capabilities.Add("file-gateway");
            hello.Capabilities.Add("log-gateway");
            hello.Capabilities.Add("remote-support-gateway");
            hello.Capabilities.Add("remote-support-v2-inventory");
            hello.Capabilities.Add("terminal-gateway");
            hello.TerminalCapability = new TerminalCapability
            {
                Supported = true,
                AvailableShells = { terminalShells }
            };
            updateHandler?.PopulateHello(hello);

            await call.RequestStream.WriteAsync(new AgentFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = tenantId,
                ClientId = agentId.ToString("D"),
                ConnectionId = connectionId.ToString("D"),
                OperationId = operationId.ToString("D"),
                Sequence = 0,
                Hello = hello
            }).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.Connected)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before accepting the presence session."));
            }

            var accepted = call.ResponseStream.Current;
            ValidateConnectedFrame(accepted, operationId, diagnostics);
            if (!GatewayWireProtocol.HasAkkaAuthority(accepted.Connected.PresenceAuthority))
            {
                diagnostics.ProtocolFailure("unsupported presence authority token");
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "Gateway returned an unsupported presence authority token."));
            }

            var heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp((int)accepted.Connected.HeartbeatIntervalSeconds, 1, 60));
            NotifyUpdateHandler(
                accepted.Connected.UpdateOffer,
                accepted.Connected.UpdatePolicy,
                confirmation: null);
            updateHandler?.OnPresenceConnected(accepted.ConnectionEpoch);
            var acceptedConnectionId = Guid.Parse(accepted.ConnectionId);
            diagnostics.Admitted(acceptedConnectionId);
            log($"Presence admitted. {diagnostics.AdmissionSummary}, connectionEpoch={accepted.ConnectionEpoch}, heartbeatInterval={heartbeatInterval.TotalSeconds:0}s.");

            using var sessionStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var session = new GatewayPresenceSession(
                tenantId,
                agentId,
                accepted.ConnectionEpoch,
                acceptedConnectionId);
            ulong sequence = 0;
            double? lastAcknowledgedRoundTripMs = null;
            ulong lastAcknowledgedSequence = 0;
            var maximumReportedRoundTripMs = TimeSpan.FromSeconds(Math.Clamp((int)accepted.Connected.HeartbeatTimeoutSeconds, 1, 300)).TotalMilliseconds;
            Task? sessionTask = null;
            try
            {
                async Task SendHeartbeatAsync(ulong heartbeatSequence)
                {
                    var heartbeatOperationId = Guid.NewGuid();
                    updateHandler?.OnActivationHeartbeatSent(accepted.ConnectionEpoch);
                    var presenceHeartbeat = new PresenceHeartbeat
                    {
                        ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                    };
                    if (lastAcknowledgedRoundTripMs is { } roundTripMs)
                    {
                        presenceHeartbeat.AcknowledgedHeartbeatRoundTripMs = roundTripMs;
                        presenceHeartbeat.AcknowledgedHeartbeatSequence = lastAcknowledgedSequence;
                    }
                    var heartbeatStarted = Stopwatch.GetTimestamp();
                    await call.RequestStream.WriteAsync(new AgentFrame
                    {
                        ProtocolVersion = options.ProtocolVersion,
                        TenantId = tenantId,
                        ClientId = agentId.ToString("D"),
                        ConnectionEpoch = accepted.ConnectionEpoch,
                        ConnectionId = accepted.ConnectionId,
                        OperationId = heartbeatOperationId.ToString("D"),
                        Sequence = heartbeatSequence,
                        Heartbeat = presenceHeartbeat
                    }).ConfigureAwait(false);

                    if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                        call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.HeartbeatAccepted)
                    {
                        throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before acknowledging the heartbeat."));
                    }

                    var heartbeat = call.ResponseStream.Current;
                    ValidateHeartbeatFrame(heartbeat, accepted, heartbeatOperationId, heartbeatSequence, diagnostics);
                    if (!GatewayWireProtocol.HasAkkaAuthority(heartbeat.HeartbeatAccepted.PresenceAuthority))
                    {
                        diagnostics.ProtocolFailure("unsupported heartbeat authority token");
                        throw new RpcException(new Status(
                            StatusCode.FailedPrecondition,
                            "Gateway returned an unsupported heartbeat authority token."));
                    }

                    diagnostics.AcknowledgeHeartbeat();
                    var measuredRoundTripMs = Stopwatch.GetElapsedTime(heartbeatStarted).TotalMilliseconds;
                    lastAcknowledgedRoundTripMs = double.IsFinite(measuredRoundTripMs) && measuredRoundTripMs >= 0 && measuredRoundTripMs <= maximumReportedRoundTripMs
                        ? measuredRoundTripMs : null;
                    lastAcknowledgedSequence = heartbeatSequence;
                    updateHandler?.OnActivationHeartbeatAccepted(accepted.ConnectionEpoch);
                    NotifyUpdateHandler(
                        heartbeat.HeartbeatAccepted.UpdateOffer,
                        heartbeat.HeartbeatAccepted.UpdatePolicy,
                        heartbeat.HeartbeatAccepted.UpdateConfirmation);
                }

                // A pending activation must prove readiness before optional gateway extensions
                // can consume startup time or fail. This is also a safe first presence heartbeat
                // for non-activation sessions.
                await SendHeartbeatAsync(++sequence).ConfigureAwait(false);
                sessionTask = runForPresenceSession?.Invoke(session, accessToken, sessionStopping.Token);
                using var timer = new PeriodicTimer(heartbeatInterval);
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    // Reconnect shortly before the token expires so the next session carries a fresh JWT.
                    if (DateTimeOffset.UtcNow >= expiresAtUtc.AddMinutes(-1))
                    {
                        log("Refreshing the gateway session before the agent token expires.");
                        return;
                    }

                    await SendHeartbeatAsync(++sequence).ConfigureAwait(false);
                }
            }
            catch
            {
                // Record transport timing before optional extension shutdown consumes time.
                diagnostics.SessionFailed();
                throw;
            }
            finally
            {
                sessionStopping.Cancel();
                await StopSessionExtensionsAsync(sessionTask, sessionStopping.Token, stoppingToken).ConfigureAwait(false);
            }
        }
        catch
        {
            diagnostics.SessionFailed();
            throw;
        }
        finally
        {
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                // The server already closed the stream; there is nothing left to acknowledge.
                log("Gateway stream was already closed before a graceful completion could be sent.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Service shutdown cancels the stream before a graceful completion can be acknowledged.
                log("Gateway stream completion was canceled by service shutdown.");
            }
            catch (OperationCanceledException)
            {
                // A token-refresh hand-off may cancel the transport while the old stream is completing.
                log("Gateway stream was canceled during session hand-off; reconnecting.");
            }
        }
    }

    private async Task StopSessionExtensionsAsync(
        Task? sessionTask,
        CancellationToken sessionStoppingToken,
        CancellationToken applicationStoppingToken)
    {
        if (sessionTask is null) return;

        try
        {
            await sessionTask.WaitAsync(_extensionShutdownTimeout, applicationStoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (applicationStoppingToken.IsCancellationRequested)
        {
            // Do not delay host shutdown while a non-presence extension is unwinding.
            log("Non-presence gateway extension shutdown was interrupted by service shutdown.");
        }
        catch (OperationCanceledException) when (sessionStoppingToken.IsCancellationRequested)
        {
            log("Non-presence gateway session extension stopped with its presence owner.");
        }
        catch (TimeoutException)
        {
            // The extension was cancelled above. It must not prevent presence from obtaining a fresh token and re-admitting.
            log($"Non-presence gateway session extension did not stop within {_extensionShutdownTimeout.TotalSeconds:0.###}s; reconnecting presence.");
        }
        catch (Exception exception)
        {
            // A non-authoritative extension must not take down the fenced presence stream.
            log($"Non-presence gateway session extension ended unexpectedly: {exception.GetType().Name}.");
        }
    }

    private static TimeSpan ResolveExtensionShutdownTimeout(TimeSpan? extensionShutdownTimeout)
    {
        var timeout = extensionShutdownTimeout ?? DefaultExtensionShutdownTimeout;
        return timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(extensionShutdownTimeout), "Extension shutdown timeout must be greater than zero.");
    }

    private void NotifyUpdateHandler(
        ClientUpdateOffer? offer,
        ClientUpdatePolicy? policy,
        UpdateActivationConfirmation? confirmation)
    {
        if (updateHandler is null) return;
        try
        {
            updateHandler.OnAcknowledgement(offer, policy, confirmation);
        }
        catch (Exception exception)
        {
            updateHandler?.RecordAcknowledgementFailure(exception);
            log($"Client update acknowledgement was ignored without affecting presence: {exception.GetType().Name}.");
        }
    }

    private void ValidateConnectedFrame(GatewayFrame frame, Guid operationId, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch == 0 || frame.Sequence != 0)
        {
            diagnostics.ProtocolFailure("invalid connect acknowledgement");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid connect acknowledgement."));
        }
    }

    private void ValidateHeartbeatFrame(GatewayFrame frame, GatewayFrame accepted, Guid operationId, ulong sequence, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != accepted.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, accepted.ConnectionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence != sequence)
        {
            diagnostics.ProtocolFailure("invalid heartbeat acknowledgement");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid heartbeat acknowledgement."));
        }
    }
}

public sealed record GatewayPresenceSession(
    int TenantId,
    Guid AgentId,
    ulong ConnectionEpoch,
    Guid ConnectionId);
