using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Layout;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Access;
using NetRatel.Web.Services.Branding;
using NetRatel.Web.Services.Notifications;
using NetRatel.Web.Services.Search;
using NetRatel.Web.Services.Tenants;
using NetRatel.Web.Components;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class ClientsManagementResponsiveTests : IAsyncLifetime
{
    private static readonly object EvidenceLock = new();
    private static readonly ConcurrentDictionary<string, object> EvidenceCases = new(StringComparer.Ordinal);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [InlineData(1600, 900, "wide", 100)]
    [InlineData(1280, 800, "desktop", 100)]
    [InlineData(1024, 600, "short", 100)]
    [InlineData(768, 1024, "tablet", 100)]
    [InlineData(390, 844, "phone", 100)]
    [InlineData(390, 480, "phone-short", 100)]
    [InlineData(390, 844, "phone-text-200", 200)]
    public async Task AuthenticatedApplicationShell_RendersAndOperatesManagementView(int width, int height, string viewportName, int textScalePercent)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ColorScheme = ColorScheme.Light
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        try
        {
            var response = await page.GotoAsync($"{fixture.BaseAddress}/clients/mgmt", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 10_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Client-management fixture returned HTTP {response.Status}.");

            await page.GetByTestId("app-main-content").WaitForAsync();
            await page.GetByTestId("client-management-tabs").WaitForAsync();
            await page.GetByTestId("automation-settings-button").WaitForAsync();
            Assert.Equal(5, await page.GetByRole(AriaRole.Tab).CountAsync());
            await page.GetByTestId("github-release-catalogue").WaitForAsync();
            await page.GetByTestId("release-automation-settings").WaitForAsync();
            await page.GetByText("Fixture GitHub client release", new() { Exact = false }).WaitForAsync();

            await VisitTabAsync(page, "Packages", "artifact-management-panel");
            await VisitTabAsync(page, "Auto-updates", "release-management-panel");
            await VisitTabAsync(page, "Activity", "attempt-management-panel");
            await VisitTabAsync(page, "Suspended", "suspended-management-panel");
            await VisitTabAsync(page, "GitHub releases", "github-release-catalogue");

            if (textScalePercent != 100)
            {
                await page.EvaluateAsync($"() => document.documentElement.style.fontSize = '{textScalePercent / 100d * 16d:0.##}px'");
            }

            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} management view has horizontal overflow.");
            Assert.Equal(0, await page.Locator("[data-testid='release-automation-settings'] button").CountAsync());

            await page.GetByTestId("automation-settings-button").ClickAsync();
            await page.GetByTestId("automation-drawer").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.GetByTestId("close-automation-settings").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.activeElement?.closest('[data-testid=automation-drawer]') !== null"),
                "Keyboard focus must remain inside the open automation drawer.");
            await page.GetByTestId("automation-deploy-prerelease").CheckAsync();
            await page.GetByTestId("automation-validation").WaitForAsync();

            await page.Keyboard.PressAsync("Escape");
            await page.GetByTestId("automation-unsaved").WaitForAsync();

            await page.GetByTestId("automation-download-prerelease").CheckAsync();
            await page.GetByTestId("automation-publish-automatically").CheckAsync();
            await page.GetByTestId("save-automation-policy").ClickAsync();
            await page.GetByTestId("automation-unsaved").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            Assert.Equal(1, fixture.Data.SaveCalls);

            await page.GetByTestId("close-automation-settings").ClickAsync();
            await page.Locator("aside.mud-drawer.mud-drawer-temporary.mud-drawer--closed").WaitForAsync();
            Assert.True(
                await page.GetByTestId("automation-settings-button").EvaluateAsync<bool>("element => element === document.activeElement"),
                "Closing the automation drawer must restore focus to its opener.");

            await page.GetByTestId("automation-settings-button").ClickAsync();
            await page.GetByTestId("automation-drawer").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.GetByTestId("automation-download-stable").CheckAsync();
            fixture.Data.PreparePendingSave();
            await page.GetByTestId("save-automation-policy").ClickAsync();
            await fixture.Data.SaveStarted.Task;
            await page.GetByTestId("automation-saving").WaitForAsync();
            Assert.True(await page.GetByTestId("save-automation-policy").IsDisabledAsync());
            Assert.True(await page.GetByTestId("automation-download-stable").IsDisabledAsync());
            fixture.Data.CompletePendingSave();
            await page.GetByTestId("automation-saving").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            await page.GetByTestId("automation-unsaved").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            Assert.Equal(2, fixture.Data.SaveCalls);

            await page.GetByTestId("automation-download-stable").UncheckAsync();
            fixture.Data.FailNextSave = true;
            await page.GetByTestId("save-automation-policy").ClickAsync();
            await page.GetByTestId("automation-drawer-error").WaitForAsync();
            Assert.Contains("Could not save the policy", await page.GetByTestId("automation-drawer-error").TextContentAsync());
            await page.GetByTestId("automation-unsaved").WaitForAsync();
            await page.GetByTestId("cancel-automation-policy").ClickAsync();
            await page.GetByTestId("close-automation-settings").ClickAsync();
            await page.Locator("aside.mud-drawer.mud-drawer-temporary.mud-drawer--closed").WaitForAsync();

            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            if (await page.GetByTestId("mobile-overflow").IsVisibleAsync())
            {
                await page.GetByTestId("mobile-overflow").ClickAsync();
                await page.GetByTestId("mobile-theme-option-dark").ClickAsync();
            }
            else
            {
                await page.GetByTestId("theme-preference-menu").ClickAsync();
                await page.GetByTestId("theme-option-dark").ClickAsync();
            }
            await page.Locator("html[data-netratel-theme='dark']").WaitForAsync();

            if (await page.GetByTestId("mobile-overflow").IsVisibleAsync())
            {
                await page.GetByTestId("mobile-overflow").ClickAsync();
                await page.GetByTestId("mobile-theme-option-system").ClickAsync();
            }
            else
            {
                await page.GetByTestId("theme-preference-menu").ClickAsync();
                await page.GetByTestId("theme-option-system").ClickAsync();
            }
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            await page.Locator("html[data-netratel-theme='dark']").WaitForAsync();
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
            await page.Locator("html[data-netratel-theme='light']").WaitForAsync();

            var evidenceDirectory = Path.GetFullPath(Path.Combine("TestResults", "playwright"));
            Directory.CreateDirectory(evidenceDirectory);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, $"clients-management-{viewportName}-{width}x{height}.png"),
                FullPage = true
            });
            RecordEvidence(viewportName, width, height, textScalePercent, evidenceDirectory);

        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        _fixture = await ClientsManagementFixtureHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private static async Task VisitTabAsync(IPage page, string tabName, string panelTestId)
    {
        var tab = page.GetByRole(AriaRole.Tab, new() { Name = tabName, Exact = true });
        var viewportWidth = await page.EvaluateAsync<int>("() => window.innerWidth");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var box = await tab.BoundingBoxAsync();
            if (box is not null && box.X >= 0 && box.X + box.Width <= viewportWidth)
            {
                break;
            }

            var scrollDirection = box is not null && box.X < 0 ? "left" : "right";
            var scrollButton = page.GetByRole(AriaRole.Button, new() { Name = $"Scroll tabs {scrollDirection}", Exact = true });
            if (!await scrollButton.IsVisibleAsync() || !await scrollButton.IsEnabledAsync())
            {
                break;
            }

            await scrollButton.ClickAsync();
            await page.WaitForTimeoutAsync(100);
        }

        await tab.ClickAsync();
        await page.GetByTestId(panelTestId).WaitForAsync();
    }

    private static void RecordEvidence(string viewportName, int width, int height, int textScalePercent, string directory)
    {
        EvidenceCases[viewportName] = new { viewportName, width, height, textScalePercent, zoomEquivalent = "root font-size text scaling; 200% case uses a 32px root size" };
        lock (EvidenceLock)
        {
            var screenshots = Directory.EnumerateFiles(directory, "clients-management-*.png")
                .Select(path => Path.GetFileName(path))
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            File.WriteAllText(
                Path.Combine(directory, "clients-management-evidence.json"),
                JsonSerializer.Serialize(new
                {
                    sourceRevision = Environment.GetEnvironmentVariable("NETRATEL_REVIEW_SOURCE_SHA")
                        ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
                        ?? "local-working-tree",
                    testMergeRevision = Environment.GetEnvironmentVariable("NETRATEL_REVIEW_TEST_MERGE_SHA"),
                    source = "NetRatel.Web.PlaywrightTests actual Routes component with production MainLayout, MudBlazor, NetRatel CSS/theme, and InteractiveServerRenderMode(prerender:false); fixture-backed application services",
                    applicationMode = "authenticated interactive server circuit",
                    themes = new[] { "light", "dark", "system-following-dark", "system-following-light" },
                    zoomMethod = "200% effective text-scale equivalent: 32px document root font size at a 390px CSS viewport",
                    acceptance = new[]
                    {
                        "interactive application shell and main navigation",
                        "light/dark/system theme including system preference changes",
                        "five text-led management tabs",
                        "automation grouped controls and prerelease validation",
                        "dirty navigation/Escape/close guard and focus containment/restoration",
                        "delayed save, visible saving state, disabled controls, and failed-save draft preservation",
                        "responsive overflow and 200% effective text scale"
                    },
                    screenshots,
                    cases = EvidenceCases.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

[Route("/clients/mgmt")]
public sealed class ClientsManagementFixtureApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.OpenElement(1, "head");
        builder.OpenElement(2, "base");
        builder.AddAttribute(3, "href", "/");
        builder.CloseElement();
        builder.OpenElement(4, "meta");
        builder.AddAttribute(5, "name", "viewport");
        builder.AddAttribute(6, "content", "width=device-width, initial-scale=1.0");
        builder.CloseElement();
        AddStylesheet(builder, "/app-site.css");
        AddStylesheet(builder, "/css/clients-presentation.css");
        AddStylesheet(builder, "/NetRatel.Web.styles.css");
        AddStylesheet(builder, "/_content/MudBlazor/MudBlazor.min.css");
        builder.OpenElement(17, "script");
        builder.AddAttribute(18, "src", "/js/theme-preference.js");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(20, "body");
        builder.OpenComponent<Routes>(21);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.OpenElement(26, "script");
        builder.AddAttribute(27, "src", "/js/global-search-hotkeys.js");
        builder.CloseElement();
        builder.OpenElement(28, "script");
        builder.AddAttribute(29, "src", "/_framework/blazor.web.js");
        builder.CloseElement();
        builder.OpenElement(30, "script");
        builder.AddAttribute(31, "src", "/_content/MudBlazor/MudBlazor.min.js");
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    }

    private static void AddStylesheet(RenderTreeBuilder builder, string href)
    {
        builder.OpenElement(0, "link");
        builder.AddAttribute(1, "rel", "stylesheet");
        builder.AddAttribute(2, "href", href);
        builder.CloseElement();
    }
}

internal sealed class ClientsManagementFixtureHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ClientsManagementFixtureHost(WebApplication application, string baseAddress, FixtureClientArtifactsService data)
    {
        _application = application;
        BaseAddress = baseAddress;
        Data = data;
    }

    public string BaseAddress { get; }
    public FixtureClientArtifactsService Data { get; }

    public static async Task<ClientsManagementFixtureHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddAuthentication("Fixture")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("Fixture", _ => { });
        builder.Services.AddAuthorization(_ => { });
        builder.Services.AddSingleton<FixtureClientArtifactsService>();
        builder.Services.AddSingleton<IClientArtifactsService>(services => services.GetRequiredService<FixtureClientArtifactsService>());
        builder.Services.AddSingleton<ITenantApiService, FixtureTenantApiService>();
        builder.Services.AddSingleton<IDeploymentBrandingApiService, FixtureBrandingApiService>();
        builder.Services.AddSingleton<IAccessAdministrationApiService, FixtureAccessAdministrationApiService>();
        builder.Services.AddSingleton<IAppBarVersionApiClient, FixtureAppBarVersionApiClient>();
        builder.Services.AddSingleton<INetRatelNotificationApiClient, FixtureNotificationApiClient>();
        builder.Services.AddSingleton<NetRatelNotificationEventBus>();
        builder.Services.AddSingleton<IGlobalSearchService, FixtureGlobalSearchService>();

        var application = builder.Build();
        application.MapGet("/_framework/blazor.web.js", () => Results.File(ResolveStaticAsset("_framework/blazor.web.js"), "text/javascript"));
        application.MapGet("/_content/MudBlazor/MudBlazor.min.css", () => Results.File(ResolveMudBlazorStylesheet(), "text/css"));
        application.MapGet("/_content/{assembly}/{**path}", (string path) =>
            Results.File(ResolvePackageAsset(path), ResolveContentType(path)));
        application.MapGet("/app-site.css", () => Results.File(ResolveWebAsset("wwwroot/app-site.css"), "text/css"));
        application.MapGet("/css/clients-presentation.css", () => Results.File(ResolveWebAsset("wwwroot/css/clients-presentation.css"), "text/css"));
        application.MapGet("/NetRatel.Web.styles.css", () => Results.File(ResolveWebAsset("obj/{0}/net10.0/scopedcss/bundle/NetRatel.Web.styles.css", "Debug"), "text/css"));
        application.MapGet("/js/theme-preference.js", () => Results.File(ResolveWebAsset("wwwroot/js/theme-preference.js"), "text/javascript"));
        application.MapGet("/js/global-search-hotkeys.js", () => Results.File(ResolveWebAsset("wwwroot/js/global-search-hotkeys.js"), "text/javascript"));
        application.UseStaticFiles(new StaticFileOptions { FileProvider = ResolveStaticAssetProvider() });
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseAntiforgery();
        application.MapRazorComponents<ClientsManagementFixtureApp>().AddInteractiveServerRenderMode();
        await application.StartAsync().ConfigureAwait(false);
        var address = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new ClientsManagementFixtureHost(application, address, application.Services.GetRequiredService<FixtureClientArtifactsService>());
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveMudBlazorStylesheet()
    {
        var stylesheet = Path.Combine(AppContext.BaseDirectory, "MudBlazor.min.css");
        return File.Exists(stylesheet)
            ? stylesheet
            : throw new FileNotFoundException("The copied MudBlazor stylesheet required by the client-management visual fixture was not found.", stylesheet);
    }

    private static string ResolveStaticAsset(string relativePath)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.staticwebassets.runtime.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var contentRoot in document.RootElement.GetProperty("ContentRoots").EnumerateArray())
        {
            var root = contentRoot.GetString();
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (relativePath.StartsWith("_framework/", StringComparison.Ordinal))
            {
                candidate = Path.Combine(root, relativePath["_framework/".Length..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("The static asset required by the authenticated application-shell visual fixture was not found.", relativePath);
    }

    private static IFileProvider ResolveStaticAssetProvider()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.staticwebassets.runtime.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var roots = new List<string>();
        foreach (var contentRoot in document.RootElement.GetProperty("ContentRoots").EnumerateArray())
        {
            var root = contentRoot.GetString();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            roots.Add(Path.GetFileName(trimmedRoot) == "_framework"
                ? Path.GetDirectoryName(trimmedRoot)!
                : root);
        }

        var providers = roots.Select(root => (IFileProvider)new PhysicalFileProvider(root)).ToArray();
        return new CompositeFileProvider(providers);
    }

    private static string ResolvePackageAsset(string relativePath)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.staticwebassets.runtime.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var contentRoot in document.RootElement.GetProperty("ContentRoots").EnumerateArray())
        {
            var root = contentRoot.GetString();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var rootPath = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (candidate.StartsWith(rootPath, StringComparison.Ordinal) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("The component-library static asset required by the authenticated application-shell visual fixture was not found.", relativePath);
    }

    private static string ResolveContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private static string ResolveWebAsset(string relativePath, params object[] formatArguments)
    {
        var relative = formatArguments.Length == 0 ? relativePath : string.Format(relativePath, formatArguments);
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web"));
        var candidates = new[]
        {
            Path.Combine(projectRoot, relative),
            Path.Combine(projectRoot, relative.Replace("obj/Debug/", "obj/Release/", StringComparison.Ordinal))
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The authenticated application-shell visual fixture asset was not found.",
                string.Join(Environment.NewLine, candidates));
    }
}

internal sealed class FixtureAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "fixture-operator"),
            new Claim(ClaimTypes.Name, "fixture-operator"),
            new Claim(ClaimTypes.Role, "Operator")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

internal sealed class FixtureBrandingApiService : IDeploymentBrandingApiService
{
    private static readonly EffectiveDeploymentBranding Branding = new(
        new BrandingField("NetRatel Fixture", BrandingValueSource.Deployment, false),
        new BrandingField("Visual Acceptance", BrandingValueSource.Deployment, false),
        new BrandingField("Installer and update management", BrandingValueSource.Deployment, false),
        new BrandingField("/brand/netratel-mark-64.png", BrandingValueSource.Deployment, false),
        new BrandingField("/brand/netratel-mark-64.png", BrandingValueSource.Deployment, false),
        new BrandingField("/brand/netratel-mark-64.png", BrandingValueSource.Deployment, false),
        new BrandingField("/favicon.ico", BrandingValueSource.Default, false),
        new BrandingField("https://example.invalid/support", BrandingValueSource.Default, false),
        new BrandingField("https://example.invalid", BrandingValueSource.Default, false),
        2);

    public Task<EffectiveDeploymentBranding> GetPublicAsync(CancellationToken cancellationToken = default) => Task.FromResult(Branding);
    public Task<EffectiveDeploymentBranding> UpdateAsync(IReadOnlyList<BrandingFieldUpdate> fields, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<BrandingAssetUploadResult> UploadAsync(string slot, IBrowserFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FixtureAccessAdministrationApiService : IAccessAdministrationApiService
{
    public Task<EffectiveAccessSummaryDto> GetSelfAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new EffectiveAccessSummaryDto("fixture-operator", true, ["*" ]));
    public Task<IReadOnlyList<AccessTenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AccessTenantDto>>([]);
    public Task<IReadOnlyList<AccessRoleDto>> GetRolesAsync(int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AccessRoleDto>>([]);
    public Task<IReadOnlyList<LocalUserAccessDto>> GetUsersAsync(int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LocalUserAccessDto>>([]);
    public Task<LocalAccountActivationDto> CreateLocalUserAsync(string displayName, string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<RoleAssignmentDto>> GetAssignmentsAsync(string principalId, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RoleAssignmentDto>>([]);
    public Task AssignAsync(string principalId, string roleId, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveAssignmentAsync(string principalId, string assignmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FixtureAppBarVersionApiClient : IAppBarVersionApiClient
{
    public Task<AppBarApiVersionInfo?> GetApiVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AppBarApiVersionInfo?>(new("NetRatel API", "v0.1.0-rc.10", "fixture", "fixture", "Development"));
}

internal sealed class FixtureNotificationApiClient : INetRatelNotificationApiClient
{
    public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(int page = 1, int pageSize = 20, string? eventType = null, string? correlationId = null, string? entityId = null, string? status = null, DateTimeOffset? from = null, DateTimeOffset? to = null, string? searchTerm = null, string? source = null, NetRatelNotificationSeverity? severity = null, CancellationToken ct = default) =>
        Task.FromResult(new PagedResult<NetRatelNotificationDto>([], page, pageSize, 0));
    public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<NetRatelNotificationDto?>(null);
    public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>([]);
    public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default) => Task.FromResult(new NetRatelNotificationSummaryDto());
    public Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => Task.FromResult(0);
    public Task RetryAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FixtureGlobalSearchService : IGlobalSearchService
{
    public Task<IReadOnlyList<GlobalSearchGroup>> SearchAsync(string? query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GlobalSearchGroup>>([]);

    public async IAsyncEnumerable<GlobalSearchGroup> SearchIncrementalAsync(string? query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

internal sealed class FixtureClientArtifactsService : IClientArtifactsService
{
    private ClientReleaseAutomationModel _automation = new();
    private TaskCompletionSource<bool>? _pendingSave;
    public int SaveCalls { get; private set; }
    public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
    public bool FailNextSave { get; set; }

    public void PreparePendingSave()
    {
        SaveStarted = NewSignal();
        _pendingSave = NewSignal();
    }

    public void CompletePendingSave() => _pendingSave?.TrySetResult(true);

    public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) =>
        Task.FromResult(_automation);

    public async Task<ClientReleaseAutomationModel> SaveReleaseAutomationAsync(ClientReleaseAutomationModel settings, CancellationToken ct = default)
    {
        SaveCalls++;
        SaveStarted.TrySetResult(true);
        if (_pendingSave is not null)
        {
            var pendingSave = _pendingSave;
            await pendingSave.Task.WaitAsync(ct);
            _pendingSave = null;
        }

        if (FailNextSave)
        {
            FailNextSave = false;
            throw new HttpRequestException("Fixture save failure.", null, System.Net.HttpStatusCode.ServiceUnavailable);
        }

        _automation = settings;
        _automation.Revision++;
        return _automation;
    }
    public Task<ClientReleaseAutomationModel> CheckReleasesNowAsync(CancellationToken ct = default)
    {
        _automation.NextCheckAtUtc = DateTimeOffset.UtcNow;
        return Task.FromResult(_automation);
    }

    public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default) =>
        Task.FromResult(new GitHubClientReleasePageModel
        {
            Page = page,
            RefreshedAtUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            Items =
            [
                new GitHubClientReleaseModel
                {
                    Id = 123,
                    Tag = "v0.4.102",
                    Version = "0.4.102",
                    Name = "Fixture GitHub client release",
                    PublishedAtUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
                    DetailsUrl = "https://github.com/BostonTechnologies/netratel/releases/tag/v0.4.102",
                    PublicationState = "verification required",
                    TotalClientBytes = 107374182,
                    ClientAssets = [new GitHubClientAssetModel { Id = 1, Name = "linux.tar.gz", RuntimeId = "linux-x64", SizeBytes = 40000000 }, new GitHubClientAssetModel { Id = 2, Name = "win.zip", RuntimeId = "win-x64", SizeBytes = 30000000 }, new GitHubClientAssetModel { Id = 3, Name = "osx.tar.gz", RuntimeId = "osx-arm64", SizeBytes = 37374182 }]
                },
                new GitHubClientReleaseModel
                {
                    Id = 124,
                    Tag = "v0.4.101",
                    Version = "0.4.101",
                    Name = "Security maintenance release with a deliberately long provenance label",
                    PublishedAtUtc = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
                    DetailsUrl = "https://github.com/BostonTechnologies/netratel/releases/tag/v0.4.101",
                    PublicationState = "published",
                    TotalClientBytes = 98234112,
                    ClientAssets = [new GitHubClientAssetModel { Id = 4, Name = "win.zip", RuntimeId = "win-x64", SizeBytes = 32000000 }]
                },
                new GitHubClientReleaseModel
                {
                    Id = 125,
                    Tag = "v0.4.100-preview.2",
                    Version = "0.4.100-preview.2",
                    Name = "Preview channel candidate for staged tenant rollout",
                    PublishedAtUtc = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
                    IsPrerelease = true,
                    DetailsUrl = "https://github.com/BostonTechnologies/netratel/releases/tag/v0.4.100-preview.2",
                    PublicationState = "verification required",
                    TotalClientBytes = 110000000,
                    ClientAssets = [new GitHubClientAssetModel { Id = 5, Name = "win.zip", RuntimeId = "win-x64", SizeBytes = 36000000 }]
                },
                new GitHubClientReleaseModel
                {
                    Id = 126,
                    Tag = "v0.3.99",
                    Version = "0.3.99",
                    Name = "Older release retained for rollback and provenance review",
                    PublishedAtUtc = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
                    DetailsUrl = "https://github.com/BostonTechnologies/netratel/releases/tag/v0.3.99",
                    PublicationState = "import failed: manifest signature verification requires operator retry",
                    TotalClientBytes = 87000000,
                    ClientAssets = [new GitHubClientAssetModel { Id = 6, Name = "win.zip", RuntimeId = "win-x64", SizeBytes = 29000000 }]
                }
            ]
        });

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly Guid AgentId = Guid.Parse("7b2f5d97-0d1b-4d25-b3ca-b5f58f069abf");
    private static readonly Guid ReleaseId = Guid.Parse("ee60f7aa-d994-455c-a7bd-30d6ebdbebf0");
    private readonly ClientUpdateReleaseModel _release = new()
    {
        ReleaseId = ReleaseId,
        RuntimeId = "win-x64",
        Version = "0.4.102",
        Channel = "stable",
        Sha256 = new string('a', 64),
        Enabled = true,
        PublishedAt = DateTimeOffset.UtcNow
    };

    public int ArtifactPageRequests { get; private set; }
    public int ReleasePageRequests { get; private set; }
    public int AttemptPageRequests { get; private set; }

    public Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default) => Task.FromResult(new List<ClientArtifactSummaryModel>());
    public Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default) => Task.FromResult(new List<ClientUpdateReleaseModel> { _release });
    public Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default) => Task.FromResult(new List<ClientUpdateAttemptModel>());
    public Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default) => Task.FromResult(new List<AgentClientUpdateStateModel>());

    public Task<ClientArtifactPageModel> GetArtifactsAsync(int page, int pageSize, string? rid = null, string? search = null, CancellationToken ct = default)
    {
        ArtifactPageRequests++;
        return Task.FromResult(new ClientArtifactPageModel { Total = 1, Page = page, PageSize = pageSize, Items = [new ClientArtifactSummaryModel { Rid = "win-x64", Version = "0.4.102", FileName = "fixture.zip", Sha256 = new string('a', 64), Size = 1024, UploadedAt = DateTimeOffset.UtcNow, Notes = "visual fixture" }] });
    }

    public Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(int page, int pageSize, string? search = null, string? runtimeId = null, string? version = null, string? channel = null, bool? enabled = null, CancellationToken ct = default)
    {
        ReleasePageRequests++;
        return Task.FromResult(new ClientUpdateReleasePageModel { Total = 1, Page = page, PageSize = pageSize, Items = [_release] });
    }

    public Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(int page, int pageSize, string? search = null, string? status = null, int? releaseId = null, string? runtimeId = null, int? tenantId = null, string? version = null, CancellationToken ct = default)
    {
        AttemptPageRequests++;
        return Task.FromResult(new ClientUpdateHistoryPageModel { Total = 1, Page = page, PageSize = pageSize, Items = [new ClientUpdateHistoryItemModel { AttemptId = Guid.NewGuid(), TenantId = 7, TenantName = "Visual fixture", AgentId = AgentId, ClientDisplayName = "Fixture agent", ClientHostName = "fixture-host", RuntimeId = "win-x64", TargetVersion = "0.4.102", Status = "RolledBack", FailureCode = "fixture_failure", Message = "A bounded visual-fixture failure summary", UpdatedAtUtc = DateTimeOffset.UtcNow }] });
    }

    public Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(int page, int pageSize, string? search = null, int? tenantId = null, CancellationToken ct = default) =>
        Task.FromResult(new AgentClientUpdateStatePageModel { Total = 1, Page = page, PageSize = pageSize, Items = [new AgentClientUpdateStateModel { TenantId = 7, TenantName = "Visual fixture", AgentId = AgentId, ClientDisplayName = "Fixture agent", ClientHostName = "fixture-host", SuspendedAtUtc = DateTimeOffset.UtcNow, SuspensionReason = "fixture-review" }] });

    public Task DisableReleaseAsync(Guid releaseId, CancellationToken ct = default) { _release.Enabled = false; return Task.CompletedTask; }
    public Task ResumeAgentAsync(int tenantId, Guid agentId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DownloadAsync(string rid, string version, CancellationToken ct = default) => Task.CompletedTask;
    public Task DownloadClientPackageAsync(ClientPackageDownloadRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task DownloadDeploymentScriptAsync(ClientScriptGenerateRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string rid, string version, CancellationToken ct = default) => Task.CompletedTask;
    public Task OpenUploadDialogAsync(string initialRid, Func<Task> onUploaded) => Task.CompletedTask;
    public Task UploadAsync(string rid, string version, string? notes, IBrowserFile file, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FixtureTenantApiService : ITenantApiService
{
    public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantDto>>([new TenantDto(7, "Visual fixture", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
    public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
