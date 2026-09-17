using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Updates;
using NetRatel.Client.Service.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Maintains the presence-only authenticated gateway session used by the Akka DEV canary.
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
    TimeSpan? extensionShutdownTimeout = null)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultExtensionShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _extensionShutdownTimeout = ResolveExtensionShutdownTimeout(extensionShutdownTimeout);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var token = await DisabledAgentTokenRetry.GetAccessTokenAsync(tokenService, log, stoppingToken).ConfigureAwait(false);
                await RunSessionAsync(token.AccessToken, token.ExpiresAtUtc, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentClientAuthException exception)
            {
                log($"Agent token acquisition failed: {exception.Message}");
                throw;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException or OperationCanceledException)
            {
                log($"Gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
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

    private async Task RunSessionAsync(string accessToken, DateTimeOffset expiresAtUtc, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Gateway:Endpoint must be an absolute HTTPS URL.");
        }

        using var channel = createChannel?.Invoke(endpoint) ?? GrpcChannel.ForAddress(endpoint);
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
            if (options.TelemetryShadowEnabled)
            {
                hello.Capabilities.Add("telemetry-shadow");
            }
            if (options.FileGatewayEnabled)
            {
                hello.Capabilities.Add("file-gateway");
            }
            if (options.LogGatewayEnabled)
            {
                hello.Capabilities.Add("log-gateway");
            }
            if (options.RemoteSupportGatewayEnabled)
            {
                hello.Capabilities.Add("remote-support-gateway");
            }
            if (options.RemoteSupportV2InventoryEnabled)
            {
                hello.Capabilities.Add("remote-support-v2-inventory");
            }
            if (options.TerminalGatewayEnabled && options.TerminalAuthorityEnabled)
            {
                hello.Capabilities.Add("terminal-gateway");
                hello.TerminalCapability = new TerminalCapability
                {
                    Supported = true,
                    AvailableShells = { terminalShells }
                };
            }
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
            ValidateConnectedFrame(accepted, operationId);
            if (!GatewayAuthority.MatchesRequired(accepted.Connected.PresenceAuthority, options.RequiredPresenceAuthority))
            {
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    $"Gateway reported authority '{accepted.Connected.PresenceAuthority}', but '{options.RequiredPresenceAuthority}' is required."));
            }

            var heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp((int)accepted.Connected.HeartbeatIntervalSeconds, 1, 60));
            NotifyUpdateHandler(
                accepted.Connected.UpdateOffer,
                accepted.Connected.UpdatePolicy,
                confirmation: null);
            updateHandler?.OnPresenceConnected(accepted.ConnectionEpoch);
            log($"Presence admitted. authority={accepted.Connected.PresenceAuthority}, connectionEpoch={accepted.ConnectionEpoch}, heartbeatInterval={heartbeatInterval.TotalSeconds:0}s.");

            using var sessionStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var session = new GatewayPresenceSession(
                tenantId,
                agentId,
                accepted.ConnectionEpoch,
                Guid.Parse(accepted.ConnectionId));
            ulong sequence = 0;
            Task? sessionTask = null;
            try
            {
                async Task SendHeartbeatAsync(ulong heartbeatSequence)
                {
                    var heartbeatOperationId = Guid.NewGuid();
                    updateHandler?.OnActivationHeartbeatSent(accepted.ConnectionEpoch);
                    await call.RequestStream.WriteAsync(new AgentFrame
                    {
                        ProtocolVersion = options.ProtocolVersion,
                        TenantId = tenantId,
                        ClientId = agentId.ToString("D"),
                        ConnectionEpoch = accepted.ConnectionEpoch,
                        ConnectionId = accepted.ConnectionId,
                        OperationId = heartbeatOperationId.ToString("D"),
                        Sequence = heartbeatSequence,
                        Heartbeat = new PresenceHeartbeat
                        {
                            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                        }
                    }).ConfigureAwait(false);

                    if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                        call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.HeartbeatAccepted)
                    {
                        throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before acknowledging the heartbeat."));
                    }

                    var heartbeat = call.ResponseStream.Current;
                    ValidateHeartbeatFrame(heartbeat, accepted, heartbeatOperationId, heartbeatSequence);
                    if (!GatewayAuthority.MatchesRequired(heartbeat.HeartbeatAccepted.PresenceAuthority, options.RequiredPresenceAuthority))
                    {
                        throw new RpcException(new Status(
                            StatusCode.FailedPrecondition,
                            $"Gateway changed authority to '{heartbeat.HeartbeatAccepted.PresenceAuthority}'."));
                    }

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
            finally
            {
                sessionStopping.Cancel();
                await StopSessionExtensionsAsync(sessionTask, sessionStopping.Token, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (RpcException)
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
            log($"Non-presence gateway session extension ended unexpectedly: {exception.GetType().Name}: {exception.Message}");
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
            log($"Client update acknowledgement was ignored without affecting presence: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ValidateConnectedFrame(GatewayFrame frame, Guid operationId)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch == 0 || frame.Sequence != 0)
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid connect acknowledgement."));
        }
    }

    private void ValidateHeartbeatFrame(GatewayFrame frame, GatewayFrame accepted, Guid operationId, ulong sequence)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != accepted.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, accepted.ConnectionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence != sequence)
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid heartbeat acknowledgement."));
        }
    }
}

public sealed record GatewayPresenceSession(
    int TenantId,
    Guid AgentId,
    ulong ConnectionEpoch,
    Guid ConnectionId);
