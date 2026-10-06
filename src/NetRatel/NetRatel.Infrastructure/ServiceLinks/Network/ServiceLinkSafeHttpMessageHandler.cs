using System.Net;
using System.Net.Sockets;

namespace NetRatel.Infrastructure.ServiceLinks.Network;

/// <summary>Validate every resolved address and connect directly to the approved IP; never follow a redirect or proxy.</summary>
public static class ServiceLinkSafeHttpMessageHandler
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateHttpOption = new("NetRatel.ServiceLinks.AllowPrivateHttp");

    public static SocketsHttpHandler Create(bool allowPrivateHttp = false, Func<bool>? currentAllowPrivateHttp = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // Every operation needs a new checked socket. Reusing a formerly
            // approved private HTTPS connection would bypass opt-in removal.
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero
        };
        handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, cancellationToken, allowPrivateHttp, currentAllowPrivateHttp);
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken, bool defaultAllowPrivateHttp, Func<bool>? currentAllowPrivateHttp)
    {
        var request = context.InitialRequestMessage;
        var requestUri = request.RequestUri ?? throw new HttpRequestException("The outbound integration request did not contain a URI.");
        var allowPrivateHttp = currentAllowPrivateHttp is not null ? currentAllowPrivateHttp() :
            defaultAllowPrivateHttp || request.Options.TryGetValue(AllowPrivateHttpOption, out var allowed) && allowed;
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal] : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (currentAllowPrivateHttp is not null) allowPrivateHttp = currentAllowPrivateHttp();
        ServiceLinkEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", allowPrivateHttp);
        SocketException? lastConnectionError = null;
        foreach (var address in addresses)
        {
            if (currentAllowPrivateHttp is not null)
                ServiceLinkEndpointPolicy.ValidateResolvedAddresses(requestUri, addresses, "Outbound integration endpoint", currentAllowPrivateHttp());
            try { return await OpenSocketAsync(address, context.DnsEndPoint.Port, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (SocketException exception) { lastConnectionError = exception; }
        }
        throw new HttpRequestException("The outbound integration endpoint could not be reached.", lastConnectionError);
    }

    private static async ValueTask<Stream> OpenSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
