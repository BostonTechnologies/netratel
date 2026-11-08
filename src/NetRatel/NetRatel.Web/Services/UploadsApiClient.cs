using NetRatel.Web.Configuration;
using System.Net;
using System.Net.Http;
using NetRatel.Web.Services.Authentication;
using Microsoft.Extensions.Logging;

namespace NetRatel.Web.Services;

public interface IUploadsApiClient
{
    HttpClient Http { get; }
}

public sealed class UploadsApiClient : IDisposable, IUploadsApiClient
{
    public HttpClient Http { get; }

    private readonly HttpMessageHandler _rootHandler; // so we can dispose properly

    public UploadsApiClient(
        WebGatewayOptions gatewayOptions,
        OperatorApiCredentialProvider credentials,
        ILogger<TokenAuthorizationHandler> tokenLogger)
    {
        // Base transport
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5) // good hygiene
        };

        // Streaming bodies are sent once. A redirect is surfaced to the caller;
        // it must never buffer/reissue an upload or an uncertain mutation.
        var authorized = new TokenAuthorizationHandler(tokenLogger)
        {
            InnerHandler = sockets
        };

        var forwarding = new OperatorApiCredentialForwardingHandler(credentials)
        {
            InnerHandler = authorized
        };

        _rootHandler = forwarding;

        Http = new HttpClient(forwarding, disposeHandler: false)
        {
            BaseAddress = gatewayOptions.ApiBaseUri,
            Timeout = Timeout.InfiniteTimeSpan // critical for streaming uploads
        };
    }

    public void Dispose()
    {
        Http.Dispose();
        _rootHandler.Dispose(); // disposes inner chain down to SocketsHttpHandler
    }
}
