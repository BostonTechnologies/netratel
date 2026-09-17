using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Tenants;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class ClientsManagementResponsiveTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [InlineData(1440, 900, "desktop")]
    [InlineData(390, 844, "phone")]
    public async Task AuthenticatedFixture_RendersFourManagementTabsWithinViewport(int width, int height, string viewportName)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        try
        {
            var response = await page.GotoAsync($"{fixture.BaseAddress}/clients-mgmt", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 10_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Client-management fixture returned HTTP {response.Status}.");

            await page.GetByTestId("client-management-tabs").WaitForAsync();
            Assert.Equal(4, await page.GetByRole(AriaRole.Tab).CountAsync());
            await page.GetByTestId("artifact-table").WaitForAsync();
            await page.GetByRole(AriaRole.Tab, new() { Name = "Auto-update Releases" }).WaitForAsync();
            await page.GetByRole(AriaRole.Tab, new() { Name = "Update Attempts" }).WaitForAsync();
            await page.GetByRole(AriaRole.Tab, new() { Name = "Suspended Agents" }).WaitForAsync();
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} management view has horizontal overflow.");

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine("TestResults", "playwright", $"clients-management-{viewportName}-{width}x{height}.png"),
                FullPage = true
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async Task InitializeAsync()
    {
        _fixture = await ClientsManagementFixtureHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_fixture is not null) await _fixture.DisposeAsync();
    }
}

[Route("/clients-mgmt")]
public sealed class ClientsManagementFixtureApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.OpenElement(1, "head");
        builder.OpenElement(2, "meta");
        builder.AddAttribute(3, "name", "viewport");
        builder.AddAttribute(4, "content", "width=device-width, initial-scale=1.0");
        builder.CloseElement();
        builder.OpenElement(5, "link");
        builder.AddAttribute(6, "rel", "stylesheet");
        builder.AddAttribute(7, "href", "_content/MudBlazor/MudBlazor.min.css");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(8, "body");
        builder.OpenComponent<MudThemeProvider>(9);
        builder.CloseComponent();
        builder.OpenComponent<MudPopoverProvider>(10);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(11);
        builder.CloseComponent();
        builder.OpenComponent<MudSnackbarProvider>(12);
        builder.CloseComponent();
        builder.OpenElement(13, "main");
        builder.AddAttribute(14, "class", "pa-4");
        builder.OpenComponent<ClientsMgmt>(15);
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseElement();
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
        builder.Services.AddRazorComponents();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddSingleton<FixtureClientArtifactsService>();
        builder.Services.AddSingleton<IClientArtifactsService>(services => services.GetRequiredService<FixtureClientArtifactsService>());
        builder.Services.AddSingleton<ITenantApiService, FixtureTenantApiService>();

        var application = builder.Build();
        application.MapGet("/_content/MudBlazor/MudBlazor.min.css", () => Results.File(ResolveMudBlazorStylesheet(), "text/css"));
        application.UseStaticFiles();
        application.UseAntiforgery();
        application.MapRazorComponents<ClientsManagementFixtureApp>();
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
}

internal sealed class FixtureClientArtifactsService : IClientArtifactsService
{
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
    public void OpenUploadDialog(string initialRid, Func<Task> onUploaded) { }
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
