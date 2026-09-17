using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

namespace NetRatel.Web.PlaywrightTests;

internal sealed class TelemetryFixtureHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private TelemetryFixtureHost(WebApplication application, string baseAddress)
    {
        _application = application;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public static async Task<TelemetryFixtureHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddAuthentication(FixtureAuthenticationHandler.FixtureScheme)
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>(FixtureAuthenticationHandler.FixtureScheme, _ => { });
        builder.Services.AddAuthorization();

        var application = builder.Build();
        application.MapGet("/_content/MudBlazor/MudBlazor.min.css", () => Results.File(ResolveMudBlazorStylesheet(), "text/css"));
        application.UseStaticFiles();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseAntiforgery();
        application.MapRazorComponents<TelemetryFixtureApp>().RequireAuthorization();
        await application.StartAsync().ConfigureAwait(false);
        var address = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new TelemetryFixtureHost(application, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class FixtureAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string FixtureScheme = "TelemetryFixture";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "visual-fixture-admin")], FixtureScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), FixtureScheme)));
        }
    }

    private static string ResolveMudBlazorStylesheet()
    {
        var stylesheet = Path.Combine(AppContext.BaseDirectory, "MudBlazor.min.css");

        return File.Exists(stylesheet)
            ? stylesheet
            : throw new FileNotFoundException("The copied MudBlazor stylesheet required by the telemetry visual fixture was not found.", stylesheet);
    }
}
