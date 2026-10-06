using System.Net;
using System.Net.Sockets;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.ServiceLinks.Network;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Every exchange resolves afresh and connects to a policy-checked address.</summary>
public static class RatelDeskReceiverSafeHttpMessageHandler
{
    public static readonly HttpRequestOptionsKey<string> ApprovedApiBaseOption = new("NetRatel.RatelDesk.ApprovedApiBase");

    public static SocketsHttpHandler Create(RatelDeskAuthenticationMode mode, RatelDeskReceiverNetworkPolicy policy)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero
        };
        handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, mode, policy, cancellationToken);
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        RatelDeskAuthenticationMode mode, RatelDeskReceiverNetworkPolicy policy, CancellationToken cancellationToken)
    {
        var request = context.InitialRequestMessage;
        var uri = request.RequestUri ?? throw new HttpRequestException("receiver-operation-endpoint-invalid");
        if (!request.Options.TryGetValue(ApprovedApiBaseOption, out var approvedApiBase))
            throw new HttpRequestException("receiver-approved-api-base-missing");
        policy.ValidateEndpoint(mode, approvedApiBase, uri.OriginalString);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal] : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        policy.ValidateEndpoint(mode, approvedApiBase, uri.OriginalString);
        ServiceLinkEndpointPolicy.ValidateResolvedAddresses(uri, addresses, "RatelDesk receiver endpoint",
            policy.CurrentAllowPrivateHttp(mode));
        SocketException? lastConnectionError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            policy.ValidateEndpoint(mode, approvedApiBase, uri.OriginalString);
            ServiceLinkEndpointPolicy.ValidateResolvedAddresses(uri, addresses, "RatelDesk receiver endpoint",
                policy.CurrentAllowPrivateHttp(mode));
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                policy.ValidateEndpoint(mode, approvedApiBase, uri.OriginalString);
                ServiceLinkEndpointPolicy.ValidateResolvedAddresses(uri, addresses, "RatelDesk receiver endpoint",
                    policy.CurrentAllowPrivateHttp(mode));
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException error)
            {
                socket.Dispose();
                lastConnectionError = error;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("receiver-endpoint-unreachable", lastConnectionError);
    }
}
