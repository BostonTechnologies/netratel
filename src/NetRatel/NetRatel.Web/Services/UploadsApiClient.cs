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
        IConfiguration cfg,
        ITokenService tokenService,
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

        // Your redirect re-issuer (avoid replaying huge multipart bodies if you like)
        var redirect = new RedirectReissueHandler
        {
            InnerHandler = sockets
        };

        // Your token injector
        var token = new TokenAuthorizationHandler(tokenService, tokenLogger)
        {
            InnerHandler = redirect
        };

        _rootHandler = token;

        Http = new HttpClient(token, disposeHandler: false)
        {
            BaseAddress = new Uri(cfg["ApiBaseUrl"] ?? "https://localhost:5001/"),
            Timeout = Timeout.InfiniteTimeSpan // critical for streaming uploads
        };
    }

    public void Dispose()
    {
        Http.Dispose();
        _rootHandler.Dispose(); // disposes inner chain down to SocketsHttpHandler
    }
}
