using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Web.Services.Authentication;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class OidcSessionRefreshTests
{
    [Fact]
    public async Task Expiring_Oidc_Token_Is_Rotated_And_Reissued_In_The_Cookie()
    {
        var (context, authentication) = CreateContext(
            accessToken: "old-access-token",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(2),
            refreshToken: "old-refresh-token");
        var factory = new StubHttpClientFactory(new RecordingHandler(_ => JsonResponse("""
            { "access_token": "new-access-token", "refresh_token": "new-refresh-token", "expires_in": 3600 }
            """)));
        var service = CreateService(factory, context);

        var token = await service.GetValidAccessTokenAsync();

        Assert.Equal("new-access-token", token);
        Assert.NotNull(authentication.LastSignInProperties);
        Assert.Equal("new-access-token", authentication.LastSignInProperties!.GetTokenValue("access_token"));
        Assert.Equal("new-refresh-token", authentication.LastSignInProperties.GetTokenValue("refresh_token"));
        Assert.True(authentication.LastSignInProperties.IsPersistent);
        Assert.True(authentication.LastSignInProperties.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(7));
    }

    [Fact]
    public async Task Concurrent_Refreshes_For_The_Same_Ticket_Use_One_Refresh_Grant()
    {
        var handler = new BlockingTokenHandler("""
            { "access_token": "new-access-token", "refresh_token": "new-refresh-token", "expires_in": 3600 }
            """);
        var factory = new StubHttpClientFactory(handler);
        var (firstContext, firstAuthentication) = CreateContext(
            "old-access-token", DateTimeOffset.UtcNow.AddMinutes(2), "refresh-token-concurrent");
        var (secondContext, secondAuthentication) = CreateContext(
            "old-access-token", DateTimeOffset.UtcNow.AddMinutes(2), "refresh-token-concurrent");
        var service = CreateService(factory);

        var firstRefresh = service.TryRefreshSessionAsync(firstContext, await firstContext.AuthenticateAsync());
        await handler.FirstRequestStarted;
        var secondRefresh = service.TryRefreshSessionAsync(secondContext, await secondContext.AuthenticateAsync());
        handler.Release();

        Assert.All(await Task.WhenAll(firstRefresh, secondRefresh), Assert.True);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("new-access-token", firstAuthentication.LastSignInProperties?.GetTokenValue("access_token"));
        Assert.Equal("new-access-token", secondAuthentication.LastSignInProperties?.GetTokenValue("access_token"));
    }

    [Fact]
    public async Task Expired_Token_Without_Refresh_Token_Requires_Reauthentication()
    {
        var (context, authentication) = CreateContext(
            "expired-access-token", DateTimeOffset.UtcNow.AddMinutes(-1), refreshToken: null);
        var service = CreateService(new StubHttpClientFactory(new RecordingHandler(_ => throw new InvalidOperationException("The token endpoint must not be called."))));

        var renewed = await service.TryRefreshSessionAsync(context, await context.AuthenticateAsync());

        Assert.False(renewed);
        Assert.Null(authentication.LastSignInProperties);
    }

    [Fact]
    public async Task Cookie_Validation_Renews_A_Session_Refreshed_By_The_Token_Service()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator@example.invalid")],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext { User = principal };
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme,
            typeof(CookieAuthenticationHandler));
        var validationContext = new CookieValidatePrincipalContext(
            context,
            scheme,
            new CookieAuthenticationOptions(),
            ticket);
        var events = new CookieOidcSessionEvents(
            new RefreshingTokenService(),
            NullLogger<CookieOidcSessionEvents>.Instance);

        await events.ValidatePrincipal(validationContext);

        Assert.True(validationContext.ShouldRenew);
    }

    private static TokenService CreateService(IHttpClientFactory factory, HttpContext? context = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OIDC_CLIENT_SECRET"] = "test-secret",
                ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid",
                ["Authentication:Oidc:TokenEndpoint"] = "https://issuer.example.invalid/oauth/token",
                ["Authentication:Oidc:ClientId"] = "test-client",
                ["Authentication:Oidc:ApiScope"] = "netratel.api"
            })
            .Build();
        return new TokenService(
            accessor,
            factory,
            configuration,
            new StubSystemTokenService(),
            NullLogger<TokenService>.Instance);
    }

    private static (DefaultHttpContext Context, RecordingAuthenticationService Authentication) CreateContext(
        string accessToken,
        DateTimeOffset expiresAt,
        string? refreshToken)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator@example.invalid")],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            IsPersistent = true
        };
        var tokens = new List<AuthenticationToken>
        {
            new() { Name = "access_token", Value = accessToken },
            new() { Name = "expires_at", Value = expiresAt.UtcDateTime.ToString("o") }
        };
        if (refreshToken is not null)
        {
            tokens.Add(new AuthenticationToken { Name = "refresh_token", Value = refreshToken });
        }

        properties.StoreTokens(tokens);
        var authentication = new RecordingAuthenticationService(new AuthenticationTicket(
            principal,
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme));
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        return (new DefaultHttpContext { RequestServices = services, User = principal }, authentication);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubSystemTokenService : ISystemTokenService
    {
        public Task<string> GetTokenAsync() => Task.FromResult("development-token");
    }

    private sealed class RecordingAuthenticationService(AuthenticationTicket ticket) : IAuthenticationService
    {
        private readonly AuthenticationTicket _ticket = ticket;

        public AuthenticationProperties? LastSignInProperties { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(_ticket));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastSignInProperties = properties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class BlockingTokenHandler(string json) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => _requestCount;
        public Task FirstRequestStarted => _firstRequestStarted.Task;
        private int _requestCount;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _firstRequestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return JsonResponse(json);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class RefreshingTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => Task.FromResult("refreshed-access-token");

        public Task<bool> TryRefreshSessionAsync(
            HttpContext context,
            AuthenticateResult authentication,
            CancellationToken cancellationToken = default)
        {
            context.Items[TokenService.SessionRefreshedItemKey] = true;
            return Task.FromResult(true);
        }
    }
}
