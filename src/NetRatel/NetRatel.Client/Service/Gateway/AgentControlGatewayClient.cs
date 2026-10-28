using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Offboarding;
using NetRatel.Shared.Contracts.ComputerUse;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Owns the non-terminal control stream for one fenced presence session.
/// Terminal and PTY traffic deliberately do not use this stream.
/// </summary>
public sealed class AgentControlGatewayClient(
    GatewayClientOptions options,
    Action<string> log,
    ClientOffboardingCoordinator? offboarding = null,
    Func<ComputerUseCommand, CancellationToken, Task<ComputerUseResult>>? executeComputerUse = null,
    Func<CancellationToken, Task>? revokeComputerUse = null)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Control gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

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
                log($"Control gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(
        Uri endpoint,
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentControlGateway.AgentControlGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {session.GetAccessToken(accessToken)}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var writerGate = new SemaphoreSlim(1, 1);
        ulong sequence = 0;
        async Task WriteResponseAsync(AgentControlFrame frame, CancellationToken cancellationToken)
        {
            await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                frame.ProtocolVersion = options.ProtocolVersion;
                frame.TenantId = session.TenantId;
                frame.ClientId = session.AgentId.ToString("D");
                frame.ConnectionEpoch = session.ConnectionEpoch;
                frame.ConnectionId = session.ConnectionId.ToString("D");
                frame.Sequence = checked(++sequence);
                await call.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            finally { writerGate.Release(); }
        }
        var computerUse = new AgentComputerUseDispatcher(executeComputerUse, revokeComputerUse,
            WriteResponseAsync, streamLifetime.Cancel, streamLifetime.Token, log);
        try
        {
            var hello = new AgentControlHello { Capabilities = { "ping" } };
            if (offboarding is not null) hello.Capabilities.Add(ClientOffboardingCoordinator.Capability);
            if (executeComputerUse is not null) hello.Capabilities.Add(ComputerUseContract.Capability);
            await call.RequestStream.WriteAsync(new AgentControlFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = session.TenantId,
                ClientId = session.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Hello = hello
            }).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayControlFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Control gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session);
            log($"Control gateway admitted. authority={call.ResponseStream.Current.Accepted.ControlAuthority}.");

            ulong lastInboundSequence = 0;
            while (await call.ResponseStream.MoveNext(streamLifetime.Token).ConfigureAwait(false))
            {
                var request = call.ResponseStream.Current;
                ValidateRequest(request, session, lastInboundSequence);
                lastInboundSequence = request.Sequence;
                if (request.PayloadCase == GatewayControlFrame.PayloadOneofCase.ComputerUseDispatch)
                {
                    computerUse.Dispatch(request.ComputerUseDispatch);
                    continue;
                }
                if (request.PayloadCase == GatewayControlFrame.PayloadOneofCase.ComputerUseCancellation)
                {
                    computerUse.Cancel(request.ComputerUseCancellation);
                    continue;
                }
                if (request.PayloadCase == GatewayControlFrame.PayloadOneofCase.UninstallRequest)
                {
                    var acknowledgement = offboarding is null
                        ? new UninstallAcknowledgement { OperationId = request.UninstallRequest.OperationId, FailureCode = "uninstall_not_supported" }
                        : await offboarding.PrepareAsync(session, request.UninstallRequest, stoppingToken).ConfigureAwait(false);
                    await WriteResponseAsync(new AgentControlFrame
                    {
                        UninstallAcknowledgement = acknowledgement
                    }, stoppingToken).ConfigureAwait(false);
                    if (acknowledgement.Accepted && offboarding is not null)
                        await offboarding.StartPreparedAsync(acknowledgement.OperationId, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                if (request.PayloadCase != GatewayControlFrame.PayloadOneofCase.PingRequest)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an unsupported frame."));
                }

                if (!Guid.TryParse(request.PingRequest.RequestId, out var requestId) || requestId == Guid.Empty)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an invalid ping request ID."));
                }

                await WriteResponseAsync(new AgentControlFrame
                {
                    PingResponse = new PingResponse
                    {
                        RequestId = requestId.ToString("D"),
                        RespondedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                    }
                }, stoppingToken).ConfigureAwait(false);
            }

            throw new RpcException(new Status(StatusCode.Unavailable, "Control gateway closed the session."));
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && streamLifetime.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Control gateway helper execution failed."));
        }
        finally
        {
            await computerUse.DisposeAsync().ConfigureAwait(false);
            if (!await TryCompleteStreamAsync(call.RequestStream).ConfigureAwait(false))
                log("Control gateway stream was already closed during cleanup.");
        }
    }

    private static async Task<bool> TryCompleteStreamAsync(IClientStreamWriter<AgentControlFrame> stream)
    {
        try
        {
            await stream.CompleteAsync().ConfigureAwait(false);
            return true;
        }
        catch (RpcException) { return false; }
    }

    private void ValidateAccepted(GatewayControlFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence != 0 ||
            !GatewayWireProtocol.HasAkkaAuthority(frame.Accepted.ControlAuthority))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an invalid connect acknowledgement."));
        }
    }

    internal void ValidateRequest(GatewayControlFrame frame, GatewayPresenceSession session, ulong lastInboundSequence)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence <= lastInboundSequence)
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned a frame for another presence session."));
        }
    }
}
