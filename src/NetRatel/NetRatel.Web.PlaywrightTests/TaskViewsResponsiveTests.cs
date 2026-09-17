using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MudBlazor.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Components.Pages.Tasks;
using NetRatel.Web.Services.ClientTasks;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class TaskViewsResponsiveTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private TaskViewsFixtureHost? _fixture;

    [Theory]
    [InlineData(1440, 900, "desktop")]
    [InlineData(390, 844, "phone")]
    public async Task Fixture_RendersPagedHistoryAndBoundedTaskOutput(int width, int height, string viewportName)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Task fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        try
        {
            // This static SSR fixture has no Blazor boot script to await its
            // stylesheet. Measure layout only after linked CSS has loaded.
            var response = await page.GotoAsync(fixture.BaseAddress, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 10_000 });
            Assert.NotNull(response);
            var responseBody = await page.Locator("body").InnerTextAsync();
            Assert.True(response.Ok, $"Task visual fixture returned HTTP {response.Status}: {responseBody}.");

            await page.GetByTestId("task-history-table").WaitForAsync();
            await page.GetByTestId("task-output-panel").WaitForAsync();
            Assert.Contains("first stdout line", await page.GetByTestId("task-output-panel").InnerTextAsync());
            Assert.Equal(6, await page.GetByRole(AriaRole.Tab).CountAsync());
            var overflowingElements = await page.EvaluateAsync<string>("""
                () => JSON.stringify([...document.querySelectorAll('body *')]
                    .filter(element => element.getBoundingClientRect().right > window.innerWidth)
                    .slice(0, 10).map(element => ({ tag: element.tagName, className: element.className,
                        right: element.getBoundingClientRect().right })))
                """);
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"),
                $"{viewportName} task view has horizontal overflow: {overflowingElements}");

            var output = page.Locator(".task-output-scroll").First;
            Assert.Equal("auto", await output.EvaluateAsync<string>("element => getComputedStyle(element).overflowY"));
            Assert.True(await output.EvaluateAsync<float>("element => element.getBoundingClientRect().width") <= width, "Task output exceeded the viewport.");

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine("TestResults", "playwright", $"task-views-{viewportName}-{width}x{height}.png"),
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
        _fixture = await TaskViewsFixtureHost.StartAsync();
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

public sealed class TaskViewsFixtureApp : ComponentBase
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
        builder.OpenComponent<MudBlazor.MudThemeProvider>(9);
        builder.CloseComponent();
        builder.OpenComponent<MudBlazor.MudPopoverProvider>(10);
        builder.CloseComponent();
        builder.OpenComponent<MudBlazor.MudDialogProvider>(11);
        builder.CloseComponent();
        builder.OpenComponent<MudBlazor.MudSnackbarProvider>(12);
        builder.CloseComponent();
        builder.OpenElement(13, "main");
        builder.AddAttribute(14, "class", "pa-4");
        builder.OpenComponent<TaskHistory>(15);
        builder.CloseComponent();
        builder.OpenElement(16, "div");
        builder.AddAttribute(17, "style", "height: 1rem");
        builder.CloseElement();
        builder.OpenComponent<TaskDetail>(18);
        builder.AddAttribute(19, nameof(TaskDetail.TaskId), 101);
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    }
}

internal sealed class TaskViewsFixtureHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private TaskViewsFixtureHost(WebApplication application, string baseAddress, TaskFixtureHttpClientFactory clientFactory)
    {
        _application = application;
        BaseAddress = baseAddress;
        Requests = clientFactory.Requests;
    }

    public string BaseAddress { get; }
    public IReadOnlyList<string> Requests { get; }

    public static async Task<TaskViewsFixtureHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddSingleton<TaskFixtureHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(services => services.GetRequiredService<TaskFixtureHttpClientFactory>());
        builder.Services.AddSingleton<TaskApiService>();

        var application = builder.Build();
        application.MapGet("/_content/MudBlazor/MudBlazor.min.css", () => Results.File(ResolveMudBlazorStylesheet(), "text/css"));
        application.UseAntiforgery();
        application.MapRazorComponents<TaskViewsFixtureApp>();
        await application.StartAsync().ConfigureAwait(false);
        var address = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new TaskViewsFixtureHost(application, address, application.Services.GetRequiredService<TaskFixtureHttpClientFactory>());
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
            : throw new FileNotFoundException("The copied MudBlazor stylesheet required by the task visual fixture was not found.", stylesheet);
    }
}

internal sealed class TaskFixtureHttpClientFactory : IHttpClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TaskDto FixtureTask = new(
        101, "request-101", "agent-routing-identity", 7, ClientEnvironment.Dev, "Update", "Failed",
        "The update command failed.",
        """{"stdout":["first stdout line","second stdout line"],"stderr":["Access denied to fixture path"],"diagnostics":{"attempt":"3"},"exitCode":17}""",
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 17,
        "Fixture agent", "fixture-host", "Fixture agent", Guid.Parse("fe4c9a9d-dbae-4c03-9fed-4f2e4f10d5b1"));
    private static readonly TaskHistoryPageDto History = new(
        [new TaskHistoryItemDto(101, "request-101", 7, "Update", "Failed", "The update command failed.", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Fixture agent", "fixture-host", "Fixture agent", Guid.Parse("fe4c9a9d-dbae-4c03-9fed-4f2e4f10d5b1"))],
        27,
        0,
        10);

    public List<string> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("https://netratel.test") };

    private sealed class Handler(TaskFixtureHttpClientFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.PathAndQuery ?? string.Empty;
            owner.Requests.Add(uri);
            object response = uri.StartsWith("/api/v2/tasks/history", StringComparison.Ordinal)
                ? History
                : uri == "/api/v2/tasks/101"
                    ? FixtureTask
                    : Array.Empty<TaskLogDto>();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, JsonOptions))
            });
        }
    }
}
