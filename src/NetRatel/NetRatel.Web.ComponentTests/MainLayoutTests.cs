using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using MudBlazor.Services;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Layout;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Notifications;
using NetRatel.Web.Services.Search;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class MainLayoutTests : AsyncBunitContext
{
    public MainLayoutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        Services.AddScoped<NetRatelNotificationEventBus>();
        Services.AddSingleton<INetRatelNotificationApiClient, StubNotificationApiClient>();
        Services.AddSingleton<IGlobalSearchService, StubGlobalSearchService>();
        Services.AddSingleton<IAppBarVersionApiClient, StubAppBarVersionApiClient>();

        AddAuthorization();
    }

    [Fact]
    public void MainLayout_RendersApiLinkAndAuthoritativeAssemblyVersion()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["AppBar:BuildVersion"] = "v2.2.0"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-api-chip[href='/api/docs/']").TextContent.Trim().Should().Be("API");
        cut.Find(".netratel-appbar-version-chip").TextContent.Trim().Should().Be("v0.1.0-rc.2");
    }

    [Fact]
    public void MainLayout_RejectsDeploymentAndTelemetryVersionsAsProductIdentity()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_CONTROL_PLANE_BUILD_VERSION"] = "2.3.4",
            ["OTEL_SERVICE_VERSION"] = "9.9.9"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-version-chip").TextContent.Trim().Should().Be("v0.1.0-rc.2");
    }

    [Fact]
    public void AppBarVersionResolver_PreservesPrereleaseAndRemovesOnlyBuildMetadata()
    {
        AppBarVersionResolver.FormatProductVersion("0.1.0-rc.2+462a556b738891d440d8b72e61ce361b1656e033")
            .Should().Be("v0.1.0-rc.2");
        AppBarVersionResolver.FormatProductVersion("0.1.0")
            .Should().Be("v0.1.0");
    }

    [Fact]
    public void MainLayout_RejectsGenericBuildVersionAlias()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["BUILD_VERSION"] = "0.0.125"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-version-chip").TextContent.Trim().Should().Be("v0.1.0-rc.2");
    }

    [Fact]
    public void MainLayout_RendersFallbackVersionWhenNoConfigIsSupplied()
    {
        AddConfiguration(new Dictionary<string, string?>());

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-version-chip").TextContent.Trim().Should().StartWith("v");
    }

    [Fact]
    public void MainLayout_RendersSeparateApiAndVersionChips()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["AppBar:BuildVersion"] = "0.0.126+abcdef"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-api-chip").TextContent.Trim().Should().Be("API");
        cut.Find(".netratel-appbar-version-chip").TextContent.Trim().Should().Be("v0.1.0-rc.2");
        cut.FindAll(".netratel-appbar-desktop-actions .netratel-appbar-chip").Should().HaveCount(2);
    }

    [Fact]
    public void MainLayout_RendersMobileOverflowActionsForCompactAppBar()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["AppBar:BuildVersion"] = "0.0.126+abcdef"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-left").Should().NotBeNull();
        cut.Find(".netratel-appbar-search-wrap").Should().NotBeNull();
        cut.Find(".netratel-appbar-mobile-actions").Should().NotBeNull();

        cut.Find(".netratel-appbar-mobile-actions .mud-menu-icon-button-activator").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("API Docs");
            cut.Markup.Should().Contain("v0.1.0-rc.2");
            cut.Markup.Should().Contain("Notifications");
            cut.Markup.Should().Contain("Theme: System");
            cut.Markup.Should().Contain("System");
            cut.Markup.Should().Contain("Light");
            cut.Markup.Should().Contain("Dark");
            cut.Markup.Should().Contain("Connectivity");

            var mobileActions = cut.Find(".netratel-appbar-mobile-actions");
            mobileActions.QuerySelector(".netratel-appbar-version-chip").Should().BeNull();
            mobileActions.QuerySelector(".notification-bell-anchor").Should().BeNull();
        });
    }

    [Fact]
    public void MainLayout_UsesCanonicalThemeMenuInsteadOfThemeManagerInDevelopment()
    {
        Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment
        {
            EnvironmentName = Environments.Development
        });
        AddConfiguration(new Dictionary<string, string?>
        {
            ["AppBar:BuildVersion"] = "0.0.126+abcdef"
        });

        var cut = RenderMainLayout();

        cut.Markup.Should().Contain("data-testid=\"theme-preference-menu\"");
        cut.Markup.Should().NotContain("Theme Manager");
    }

    [Fact]
    public void MainLayout_RendersAppShellOffsetHooks()
    {
        AddConfiguration(new Dictionary<string, string?>());

        var cut = RenderMainLayout();

        cut.Find(".netratel-app-bar").Should().NotBeNull();
        cut.Find("[data-testid='navigation-toggle']").Should().NotBeNull();
        cut.Find("[data-testid='app-navigation-drawer']").GetAttribute("class").Should().Contain("mud-drawer-responsive");
        cut.Find("[data-testid='app-main-content']").TextContent.Should().Contain("Layout body");
        cut.Find("[data-testid='netratel-appbar-grid']").Should().NotBeNull();
        cut.Find("[data-testid='global-search']").Should().NotBeNull();
        cut.Find("[data-testid='mobile-overflow']").Should().NotBeNull();
    }

    [Fact]
    public void AppSiteCss_DefinesProfessionalResponsiveShellContract()
    {
        var css = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/wwwroot/app-site.css"));

        css.Should().Contain("--netratel-appbar-height: 56px;");
        css.Should().Contain(".netratel-main-content.mud-main-content");
        css.Should().Contain("min-height: calc(100dvh - var(--netratel-appbar-height));");
        css.Should().Contain("@media (max-width: 1279px)");
        css.Should().Contain("grid-template-columns: 44px minmax(0, 1fr) 44px;");
        css.Should().Contain("grid-template-columns: minmax(0, 1fr) minmax(220px, 420px) minmax(0, 1fr);");
        css.Should().Contain(".netratel-appbar-grid");
        css.Should().Contain(".netratel-app-bar.mud-appbar .mud-toolbar");
        css.Should().NotContain("grid-template-columns: 44px minmax(280px, 480px) minmax(0, 1fr);");
        css.Should().Contain(".netratel-public-theme-control");
    }

    [Fact]
    public void AppSiteCss_DefinesMobileAppBarOverflowContract()
    {
        var css = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/wwwroot/app-site.css"));

        css.Should().Contain(".netratel-appbar-mobile-actions");
        css.Should().Contain(".netratel-appbar-desktop-actions");
        css.Should().Contain("@media (max-width: 800px)");
        css.Should().Contain("grid-template-columns: 44px minmax(0, 1fr) 44px;");
        css.Should().Contain("padding: 0 8px !important;");
        css.Should().Contain("grid-template-columns: auto minmax(0, 1fr);");
        css.Should().Contain("text-overflow: ellipsis;");
        css.Should().NotContain(".netratel-appbar-overflow-row");
        css.Should().Contain(".netratel-appbar-desktop-actions");
        css.Should().Contain("display: none;");
        css.Should().Contain(".netratel-appbar-mobile-actions");
        css.Should().Contain("display: flex;");
    }

    [Fact]
    public void MainLayout_VersionChipOpensReleaseInfoPopover()
    {
        AddConfiguration(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_CONTROL_PLANE_BUILD_VERSION"] = "0.0.126+abcdef"
        });

        var cut = RenderMainLayout();

        cut.Find(".netratel-appbar-version-chip").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Release Info");
            cut.Markup.Should().Contain("NetRatel.Web");
            cut.Markup.Should().Contain("v0.0.200");
        });
    }

    [Fact]
    public void AppBarVersionResolver_FallsBackToDevWhenNoVersionSourceExists()
    {
        var configuration = new ConfigurationBuilder().Build();

        AppBarVersionResolver.Resolve(configuration, assembly: null).Should().Be("vdev");
    }

    private void AddConfiguration(Dictionary<string, string?> values)
        => Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build());

    private IRenderedComponent<MainLayout> RenderMainLayout()
        => Render<MainLayout>(parameters => parameters
            .Add(x => x.Body, builder => builder.AddContent(0, "Layout body")));

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from the test working directory.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "NetRatel.Web.ComponentTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubNotificationApiClient : INetRatelNotificationApiClient
    {
        public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
            int page = 1,
            int pageSize = 20,
            string? eventType = null,
            string? correlationId = null,
            string? entityId = null,
            string? status = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? searchTerm = null,
            string? source = null,
            NetRatelNotificationSeverity? severity = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>(Array.Empty<NetRatelNotificationDto>());

        public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new NetRatelNotificationSummaryDto());

        public Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RetryAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubGlobalSearchService : IGlobalSearchService
    {
        public Task<IReadOnlyList<GlobalSearchGroup>> SearchAsync(string? query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GlobalSearchGroup>>(Array.Empty<GlobalSearchGroup>());

        public async IAsyncEnumerable<GlobalSearchGroup> SearchIncrementalAsync(
            string? query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubAppBarVersionApiClient : IAppBarVersionApiClient
    {
        public Task<AppBarApiVersionInfo?> GetApiVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<AppBarApiVersionInfo?>(new(
                "NetRatel.API",
                "v0.0.200",
                "0.0.200+api",
                "0.0.200.0",
                "Test"));
    }
}
