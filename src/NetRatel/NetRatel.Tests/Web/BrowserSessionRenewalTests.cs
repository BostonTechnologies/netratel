using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NetRatel.Web.Controllers;
using NetRatel.Web.Services.Authentication;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class BrowserSessionRenewalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Circuit_Renews_On_A_Fresh_Http_Response_And_Reload_Uses_The_Rotated_Cookie(bool hasStartedContext)
    {
        await using var fixture = await Fixture.CreateAsync("operator");
        var state = new OperatorApiCredentialState();
        var principal = Principal("operator");
        state.BindPrincipal(principal);
        var js = new BrowserFetch(fixture.Client, fixture.Cookies);
        var service = fixture.CreateCircuitService(principal, state, js, hasStartedContext);

        var token = await service.GetValidAccessTokenAsync();
        Assert.Equal(fixture.NewAccessToken, token);
        Assert.Equal(token, await service.GetValidAccessTokenAsync());
        Assert.Equal(1, js.Calls);
        Assert.Equal(1, fixture.TokenEndpoint.Calls);
        Assert.Equal(fixture.OriginalRefreshToken, Assert.Single(fixture.TokenEndpoint.RefreshTokens));
        Assert.Equal(string.Empty, js.ResponseBody);

        // A separate HTTP request reads the real protected cookie created by
        // ASP.NET authentication. It cannot depend on the old circuit cache.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("Cookie", fixture.Cookies.GetCookieHeader(fixture.Client.BaseAddress!));
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observed = await response.Content.ReadFromJsonAsync<ObservedTicket>();
        Assert.Equal(fixture.NewAccessToken, observed!.AccessToken);
        Assert.Equal("rotated-refresh-operator", observed.RefreshToken);
        Assert.Equal(1, fixture.TokenEndpoint.Calls);
    }

    [Fact]
    public async Task Late_Renewal_Cannot_Revive_A_Signed_Out_Circuit()
    {
        await using var fixture = await Fixture.CreateAsync("operator");
        var state = new OperatorApiCredentialState();
        var principal = Principal("operator");
        state.BindPrincipal(principal);
        var js = new BrowserFetch(fixture.Client, fixture.Cookies) { BeforeCompletion = state.Clear };
        var service = fixture.CreateCircuitService(principal, state, js, hasStartedContext: true);

        await Assert.ThrowsAsync<ReauthRequiredException>(() => service.GetValidAccessTokenAsync());
        Assert.Null(state.GetCredential());
        await Assert.ThrowsAsync<ReauthRequiredException>(() => service.GetValidAccessTokenAsync());
        Assert.Equal(1, js.Calls);
    }

    [Fact]
    public async Task Started_Request_Principal_Cannot_Rebind_A_Signed_Out_Circuit_Or_Invoke_Js_During_Reconnect()
    {
        var principal = Principal("operator");
        var state = new OperatorApiCredentialState();
        var generation = state.BindPrincipal(principal);
        var context = new DefaultHttpContext { User = principal };
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var provider = new OperatorApiCredentialProvider(new HttpContextAccessor { HttpContext = context },
            new UnavailableTokenService(), state, new ConfigurationBuilder().Build());

        await provider.CaptureForCircuitAsync(principal, generation);
        Assert.Null(state.GetCredential());
        state.Clear();
        Assert.Null(await provider.GetCurrentCredentialAsync());
        Assert.Null(state.GetCredential());
    }

    [Fact]
    public async Task Local_Circuit_Still_Captures_Its_Cookie_Without_Browser_Renewal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "local-operator"), new Claim("auth_mode", "local")], "NetRatelLocal"));
        var state = new OperatorApiCredentialState();
        var generation = state.BindPrincipal(principal);
        var context = new DefaultHttpContext { User = principal };
        context.Request.Headers.Cookie = "NetRatel.Local=local-session";
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var provider = new OperatorApiCredentialProvider(new HttpContextAccessor { HttpContext = context },
            new UnavailableTokenService(), state, new ConfigurationBuilder().Build());

        await provider.CaptureForCircuitAsync(principal, generation);
        var credential = await provider.GetCurrentCredentialAsync();
        Assert.Equal(OperatorApiCredentialKind.LocalSessionCookie, credential?.Kind);
        Assert.Equal("local-session", credential?.Value);
    }

    [Fact]
    public async Task Another_Browser_Principal_Cannot_Supply_The_Circuits_Access_Token()
    {
        await using var fixture = await Fixture.CreateAsync("other-operator");
        var state = new OperatorApiCredentialState();
        var principal = Principal("operator");
        state.BindPrincipal(principal);
        var js = new BrowserFetch(fixture.Client, fixture.Cookies);
        var service = fixture.CreateCircuitService(principal, state, js, hasStartedContext: true);

        await Assert.ThrowsAsync<ReauthRequiredException>(() => service.GetValidAccessTokenAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, js.Status);
        Assert.Null(state.GetCredential());
    }

    [Fact]
    public async Task Rejected_Refresh_Returns_A_Bounded_Denial_Without_A_Circuit_Exception()
    {
        await using var fixture = await Fixture.CreateAsync("rejected-operator", rejectRefresh: true);
        var state = new OperatorApiCredentialState();
        var principal = Principal("rejected-operator");
        state.BindPrincipal(principal);
        var js = new BrowserFetch(fixture.Client, fixture.Cookies);
        var service = fixture.CreateCircuitService(principal, state, js, hasStartedContext: true);

        await Assert.ThrowsAsync<ReauthRequiredException>(() => service.GetValidAccessTokenAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, js.Status);
        Assert.Equal(1, fixture.TokenEndpoint.Calls);
        Assert.Null(state.GetCredential());
    }

    private static ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity(
        [new Claim("sub", subject), new Claim("iss", "https://issuer.example.invalid")],
        CookieAuthenticationDefaults.AuthenticationScheme));

    private static string AccessToken(string subject, DateTime expiresAt) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken(issuer: "https://issuer.example.invalid", claims: [new Claim("sub", subject)], expires: expiresAt));

    private sealed record ObservedTicket(string? AccessToken, string? RefreshToken);

    private sealed class Fixture(
        WebApplication app,
        HttpClient client,
        CookieContainer cookies,
        TokenEndpoint tokenEndpoint,
        IConfiguration configuration,
        BrowserSessionRenewalBroker broker,
        string newAccessToken,
        string originalRefreshToken) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public CookieContainer Cookies { get; } = cookies;
        public TokenEndpoint TokenEndpoint { get; } = tokenEndpoint;
        public string NewAccessToken { get; } = newAccessToken;
        public string OriginalRefreshToken { get; } = originalRefreshToken;

        public static async Task<Fixture> CreateAsync(string subject, bool rejectRefresh = false)
        {
            var oldExpiration = DateTime.UtcNow.AddMinutes(2);
            var originalRefreshToken = $"old-refresh-{Guid.NewGuid():N}";
            var newAccessToken = AccessToken(subject, DateTime.UtcNow.AddHours(1));
            var tokenEndpoint = new TokenEndpoint(newAccessToken, $"rotated-refresh-{subject}", rejectRefresh);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid",
                ["Authentication:Oidc:TokenEndpoint"] = "https://issuer.example.invalid/token",
                ["Authentication:Oidc:ClientId"] = "test-web"
            });
            var broker = new BrowserSessionRenewalBroker();
            builder.Services.AddSingleton(broker);
            builder.Services.AddSingleton<IHttpClientFactory>(new TokenClientFactory(tokenEndpoint));
            builder.Services.AddSingleton<ISystemTokenService, NoSystemTokenService>();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<OperatorApiCredentialState>();
            builder.Services.AddScoped<ITokenService, TokenService>();
            builder.Services.AddScoped<CookieOidcSessionEvents>();
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.EventsType = typeof(CookieOidcSessionEvents);
                options.SlidingExpiration = false;
            });
            builder.Services.AddAuthorization();
            builder.Services.AddControllers().AddApplicationPart(typeof(SessionRenewalController).Assembly);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapGet("/seed", async context =>
            {
                var properties = new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) };
                properties.StoreTokens([
                    new AuthenticationToken { Name = "access_token", Value = AccessToken(subject, oldExpiration) },
                    new AuthenticationToken { Name = "refresh_token", Value = originalRefreshToken },
                    new AuthenticationToken { Name = "expires_at", Value = oldExpiration.ToString("o") }
                ]);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(subject), properties);
            });
            app.MapGet("/probe", async context =>
            {
                var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                await context.Response.WriteAsJsonAsync(new ObservedTicket(authentication.Properties?.GetTokenValue("access_token"),
                    authentication.Properties?.GetTokenValue("refresh_token")));
            });
            await app.StartAsync();
            var client = app.GetTestClient();
            var cookies = new CookieContainer();
            using var seed = await client.GetAsync("/seed");
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
            foreach (var cookie in seed.Headers.GetValues("Set-Cookie"))
            {
                cookies.SetCookies(client.BaseAddress!, cookie);
            }

            return new Fixture(app, client, cookies, tokenEndpoint, builder.Configuration, broker, newAccessToken, originalRefreshToken);
        }

        public TokenService CreateCircuitService(ClaimsPrincipal principal, OperatorApiCredentialState state, IJSRuntime js, bool hasStartedContext)
        {
            var started = new DefaultHttpContext { User = principal };
            started.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
            var accessor = new HttpContextAccessor { HttpContext = hasStartedContext ? started : null };
            return new TokenService(accessor, new TokenClientFactory(TokenEndpoint), configuration, new NoSystemTokenService(), state,
                NullLogger<TokenService>.Instance,
                new BrowserSessionRenewal(js, broker, state, NullLogger<BrowserSessionRenewal>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class BrowserFetch(HttpClient client, CookieContainer cookies) : IJSRuntime
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; private set; }
        public string? ResponseBody { get; private set; }
        public Action? BeforeCompletion { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("netratelSessionRenewal.renew", identifier);
            Calls++;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/session/renew");
            request.Headers.Add("X-NetRatel-Session-Renewal", Assert.IsType<string>(Assert.Single(args!)));
            request.Headers.Add("Cookie", cookies.GetCookieHeader(client.BaseAddress!));
            using var response = await client.SendAsync(request, cancellationToken);
            Status = response.StatusCode;
            ResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var cookie in values)
                {
                    cookies.SetCookies(client.BaseAddress!, cookie);
                }
            }

            BeforeCompletion?.Invoke();
            return (TValue)(object)(response.StatusCode == HttpStatusCode.NoContent);
        }
    }

    private sealed class TokenEndpoint(string accessToken, string refreshToken, bool rejected) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> RefreshTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var values = body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            RefreshTokens.Add(values["refresh_token"]);
            return rejected
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = accessToken, refresh_token = refreshToken, expires_in = 3600 })
                };
        }
    }

    private sealed class TokenClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class NoSystemTokenService : ISystemTokenService
    {
        public Task<string> GetTokenAsync() => throw new InvalidOperationException("A human session must not use a system token.");
    }

    private sealed class UnavailableTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => throw new InvalidOperationException("No token acquisition is permitted here.");
        public Task<bool> TryRefreshSessionAsync(HttpContext context, AuthenticateResult authentication, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No token refresh is permitted here.");
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
