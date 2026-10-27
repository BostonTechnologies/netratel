using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using NetRatel.Web.Controllers;
using NetRatel.Web.Services.Authentication;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class SessionRenewalBrowserTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Fact]
    public async Task Live_Circuit_Renews_Through_Browser_Http_And_Preserves_Its_Draft_And_Rotated_Cookie()
    {
        await using var host = await RenewalBrowserHost.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync();
        Assert.Equal(204, (await context.APIRequest.PostAsync(host.BaseAddress + "/seed-valid")).Status);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/session-renewal-check");
        await Assertions.Expect(page.GetByTestId("renewal-status")).ToHaveTextAsync("Ready");
        var identity = await page.GetByTestId("renewal-circuit").TextContentAsync();
        var draft = page.GetByRole(AriaRole.Textbox, new() { Name = "Draft", Exact = true });
        await draft.FillAsync("Keep the current reviewed draft");
        await page.GetByRole(AriaRole.Button, new() { Name = "Read current session", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("renewal-status")).ToHaveTextAsync("Read 1");
        Assert.Equal(0, host.RefreshCalls);

        // Replace only this fixture's cookie with an expiring ticket. The
        // component's explicit generation change invalidates its access cache,
        // avoiding a real-time sleep while retaining the real SignalR circuit.
        Assert.Equal(204, (await context.APIRequest.PostAsync(host.BaseAddress + "/seed-expiring")).Status);
        var renewal = page.WaitForResponseAsync(response => response.Url.EndsWith("/auth/session/renew", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Read current session", Exact = true }).ClickAsync();
        var response = await renewal;
        Assert.Equal(204, response.Status);
        Assert.Empty(await response.BodyAsync());
        await Assertions.Expect(page.GetByTestId("renewal-status")).ToHaveTextAsync("Read 2");
        await Assertions.Expect(draft).ToHaveValueAsync("Keep the current reviewed draft");
        await Assertions.Expect(page.GetByTestId("renewal-circuit")).ToHaveTextAsync(identity!);
        Assert.Equal(1, host.RefreshCalls);

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("renewal-status")).ToHaveTextAsync("Ready");
        var probe = await context.APIRequest.GetAsync(host.BaseAddress + "/probe-cookie");
        Assert.Equal(200, probe.Status);
        Assert.Equal("persisted", await probe.TextAsync());
        Assert.Equal(1, host.RefreshCalls);
        Assert.Empty(errors);
    }
}

[Route("/session-renewal-check")]
public sealed class RenewalBrowserApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.OpenElement(1, "head");
        builder.OpenElement(2, "base");
        builder.AddAttribute(3, "href", "/");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(4, "body");
        builder.OpenComponent<RenewalBrowserPanel>(5);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.OpenElement(6, "script");
        builder.AddAttribute(7, "src", "/js/session-renewal.js");
        builder.CloseElement();
        builder.OpenElement(8, "script");
        builder.AddAttribute(9, "src", "/_framework/blazor.web.js");
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    }
}

public sealed class RenewalBrowserPanel : ComponentBase
{
    [Inject] public AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] public OperatorApiCredentialState Credentials { get; set; } = null!;
    [Inject] public ITokenService Tokens { get; set; } = null!;
    private ClaimsPrincipal _principal = new();
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private string _draft = string.Empty;
    private int _reads;

    protected override async Task OnInitializedAsync()
    {
        _principal = (await Authentication.GetAuthenticationStateAsync()).User;
        Credentials.BindPrincipal(_principal);
    }

    private async Task ReadAsync()
    {
        Credentials.ClearCredential(_principal);
        _ = await Tokens.GetValidAccessTokenAsync();
        _reads++;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "p");
        builder.AddAttribute(1, "data-testid", "renewal-circuit");
        builder.AddContent(2, _instance);
        builder.CloseElement();
        builder.OpenElement(3, "input");
        builder.AddAttribute(4, "aria-label", "Draft");
        builder.AddAttribute(5, "value", _draft);
        builder.AddAttribute(6, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, args => _draft = args.Value?.ToString() ?? string.Empty));
        builder.CloseElement();
        builder.OpenElement(7, "button");
        builder.AddAttribute(8, "onclick", EventCallback.Factory.Create(this, ReadAsync));
        builder.AddContent(9, "Read current session");
        builder.CloseElement();
        builder.OpenElement(10, "p");
        builder.AddAttribute(11, "data-testid", "renewal-status");
        builder.AddContent(12, _reads == 0 ? "Ready" : $"Read {_reads}");
        builder.CloseElement();
    }
}

internal sealed class RenewalBrowserHost(WebApplication app, string baseAddress, RenewalTokenEndpoint tokenEndpoint) : IAsyncDisposable
{
    public string BaseAddress { get; } = baseAddress;
    public int RefreshCalls => tokenEndpoint.Calls;

    public static async Task<RenewalBrowserHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid",
            ["Authentication:Oidc:TokenEndpoint"] = "https://issuer.example.invalid/token",
            ["Authentication:Oidc:ClientId"] = "browser-fixture"
        });
        var endpoint = new RenewalTokenEndpoint();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IHttpClientFactory>(new RenewalTokenClientFactory(endpoint));
        builder.Services.AddSingleton<ISystemTokenService>(new RenewalNoSystemTokenService());
        builder.Services.AddSingleton<BrowserSessionRenewalBroker>();
        builder.Services.AddScoped<BrowserSessionRenewal>();
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
        app.UseAntiforgery();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/js/session-renewal.js", () => Results.File(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/js/session-renewal.js")), "text/javascript"));
        app.MapGet("/_framework/blazor.web.js", () => Results.File(ResolveBlazorScript(), "text/javascript"));
        app.MapPost("/seed-valid", context => SeedAsync(context, DateTime.UtcNow.AddHours(1)));
        app.MapPost("/seed-expiring", context => SeedAsync(context, DateTime.UtcNow.AddMinutes(2)));
        app.MapGet("/probe-cookie", async context =>
        {
            var ticket = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (ticket.Properties?.GetTokenValue("refresh_token") != "fixture-rotated-refresh")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await context.Response.WriteAsync("persisted");
        });
        app.MapRazorComponents<RenewalBrowserApp>().AddInteractiveServerRenderMode();
        await app.StartAsync();
        return new RenewalBrowserHost(app, app.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)), endpoint);
    }

    private static async Task SeedAsync(HttpContext context, DateTime expiration)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "browser-operator"), new Claim("iss", "https://issuer.example.invalid")],
            CookieAuthenticationDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) };
        properties.StoreTokens([
            new AuthenticationToken { Name = "access_token", Value = AccessToken(expiration) },
            new AuthenticationToken { Name = "refresh_token", Value = $"fixture-refresh-{Guid.NewGuid():N}" },
            new AuthenticationToken { Name = "expires_at", Value = expiration.ToString("o") }
        ]);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    internal static string AccessToken(DateTime expiration) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("https://issuer.example.invalid", claims: [new Claim("sub", "browser-operator")], expires: expiration));

    private static string ResolveBlazorScript()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.staticwebassets.runtime.json")));
        foreach (var root in manifest.RootElement.GetProperty("ContentRoots").EnumerateArray())
        {
            foreach (var relative in new[] { "_framework/blazor.web.js", "blazor.web.js" })
            {
                var path = Path.Combine(root.GetString()!, relative);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("The Blazor runtime script is required for session-renewal acceptance.");
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private sealed class RenewalNoSystemTokenService : ISystemTokenService
    {
        public Task<string> GetTokenAsync() => throw new InvalidOperationException("This fixture must use the human session.");
    }

    private sealed class RenewalTokenClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }
}

internal sealed class RenewalTokenEndpoint : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                access_token = RenewalBrowserHost.AccessToken(DateTime.UtcNow.AddHours(1)),
                refresh_token = "fixture-rotated-refresh",
                expires_in = 3600
            })
        });
    }
}
