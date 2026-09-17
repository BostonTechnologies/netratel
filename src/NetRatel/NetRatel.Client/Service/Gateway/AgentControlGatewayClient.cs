using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
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
    Action<string> log)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken stoppingToken)
    {
        if (!options.ControlGatewayEnabled)
        {
            return;
        }

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
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        try
        {
            await call.RequestStream.WriteAsync(new AgentControlFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = session.TenantId,
                ClientId = session.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Hello = new AgentControlHello { Capabilities = { "ping" } }
            }).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayControlFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Control gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session);
            log($"Control gateway admitted. authority={call.ResponseStream.Current.Accepted.ControlAuthority}.");

            ulong sequence = 0;
            while (await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false))
            {
                var request = call.ResponseStream.Current;
                ValidateRequest(request, session);
                if (request.PayloadCase != GatewayControlFrame.PayloadOneofCase.PingRequest)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an unsupported frame."));
                }

                if (!Guid.TryParse(request.PingRequest.RequestId, out var requestId) || requestId == Guid.Empty)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an invalid ping request ID."));
                }

                await call.RequestStream.WriteAsync(new AgentControlFrame
                {
                    ProtocolVersion = options.ProtocolVersion,
                    TenantId = session.TenantId,
                    ClientId = session.AgentId.ToString("D"),
                    ConnectionEpoch = session.ConnectionEpoch,
                    ConnectionId = session.ConnectionId.ToString("D"),
                    Sequence = checked(++sequence),
                    PingResponse = new PingResponse
                    {
                        RequestId = requestId.ToString("D"),
                        RespondedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                    }
                }).ConfigureAwait(false);
            }

            throw new RpcException(new Status(StatusCode.Unavailable, "Control gateway closed the session."));
        }
        finally
        {
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // The server already closed the stream.
            }
        }
    }

    private void ValidateAccepted(GatewayControlFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence != 0 ||
            string.IsNullOrWhiteSpace(frame.Accepted.ControlAuthority))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned an invalid connect acknowledgement."));
        }
    }

    private void ValidateRequest(GatewayControlFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence == 0)
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Control gateway returned a frame for another presence session."));
        }
    }
}
