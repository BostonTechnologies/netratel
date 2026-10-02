using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.Access;
using NetRatel.Web.Services.Branding;
using NetRatel.Web.Services.Notifications;
using NetRatel.Web.Services.Search;
using NetRatel.Web.Services.Tenants;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Services.Services;
using NetRatel.Web.Components;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class ClientsManagementResponsiveTests : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private static readonly string[] SafeNetworkFailures =
    [
        "net::ERR_ABORTED",
        "net::ERR_CONNECTION_CLOSED",
        "net::ERR_CONNECTION_REFUSED",
        "net::ERR_CONNECTION_RESET",
        "net::ERR_NAME_NOT_RESOLVED",
        "net::ERR_NETWORK_CHANGED",
        "net::ERR_TIMED_OUT"
    ];
    private static readonly (string Name, bool ImportPackEnabled)[] ExpectedGitHubReleases =
    [
        ("Fixture GitHub client release", true),
        ("Security maintenance release with a deliberately long provenance label", false),
        ("Preview channel candidate for staged tenant rollout", true),
        ("Older release retained for rollback and provenance review", false)
    ];
    private static readonly object EvidenceLock = new();
    private static readonly ConcurrentDictionary<string, object> EvidenceCases = new(StringComparer.Ordinal);
    private const string FailureEvidenceDirectoryDataKey = "NetRatel.PlaywrightFailureEvidenceDirectory";
    private readonly ClientsManagementBrowserFixture _browserFixture;
    private readonly ITestOutputHelper _testOutputHelper;
    private ClientsManagementFixtureHost? _fixture;

    public ClientsManagementResponsiveTests(ClientsManagementBrowserFixture browserFixture, ITestOutputHelper testOutputHelper)
    {
        _browserFixture = browserFixture;
        _testOutputHelper = testOutputHelper;
    }

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
        var browser = _browserFixture.Browser;
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ColorScheme = ColorScheme.Light
        });
        IPage? page = null;
        BrowserStartupDiagnostics? startupDiagnostics = null;
        Exception? testFailureInFlight = null;
        try
        {
            page = await context.NewPageAsync();
            startupDiagnostics = new BrowserStartupDiagnostics(page);
            // The fixture starts a real Interactive Server circuit. Under the full
            // hosted test matrix the first circuit can take longer than the normal
            // interaction budget to attach, especially at the tablet case; keep
            // the visual assertions strict once the shell is available.
            page.SetDefaultTimeout(30_000);
            var evidenceDirectory = GetPlaywrightArtifactRoot();
            Directory.CreateDirectory(evidenceDirectory);

            await NavigateAndWaitForShellAsync(page, fixture, startupDiagnostics, viewportName, 90_000);
            var shellWait = new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 };
            await page.GetByTestId("client-management-tabs").WaitForAsync(shellWait);
            await page.GetByTestId("automation-settings-button").WaitForAsync(shellWait);
            Assert.Equal(5, await page.GetByRole(AriaRole.Tab).CountAsync());
            await page.GetByTestId("github-release-catalogue").WaitForAsync();
            await page.GetByTestId("release-automation-settings").WaitForAsync();
            var initialImportPack = page.GetByTestId("github-release-name")
                .GetByText(ExpectedGitHubReleases[0].Name, new() { Exact = true })
                .Locator("xpath=ancestor::tr[1]")
                .GetByTestId("github-import-pack");
            await Assertions.Expect(initialImportPack, "The interactive Client catalogue should enable its verification-required Import pack action.")
                .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });
            var initialRequest = fixture.Data.GitHubReleaseRequestCount;
            Assert.True(initialRequest > 0, "The interactive Client catalogue must have requested its release fixture.");
            await Assertions.Expect(page.GetByText(FixtureClientArtifactsService.GetGitHubCheckedText(initialRequest), new() { Exact = true }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

            await VisitTabAsync(page, "Packages", "artifact-management-panel");
            await VisitTabAsync(page, "Auto-updates", "release-management-panel");
            await VisitTabAsync(page, "Activity", "attempt-management-panel");
            await VisitTabAsync(page, "Suspended", "suspended-management-panel");
            var previousGitHubRequest = fixture.Data.GitHubReleaseRequestCount;
            await VisitTabAsync(page, "GitHub releases", "github-release-catalogue");
            var refreshedGitHubRequest = await fixture.Data.WaitForGitHubReleaseRequestAfterAsync(previousGitHubRequest, TimeSpan.FromSeconds(30));
            await Assertions.Expect(page.GetByText(FixtureClientArtifactsService.GetGitHubCheckedText(refreshedGitHubRequest), new() { Exact = true }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

            if (textScalePercent != 100)
            {
                await page.EvaluateAsync($"() => document.documentElement.style.fontSize = '{textScalePercent / 100d * 16d:0.##}px'");
            }

            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} management view has horizontal overflow.");
            await AssertReleaseRowContentReachableAsync(page, viewportName);
            Assert.Equal(0, await page.Locator("[data-testid='release-automation-settings'] button").CountAsync());

            await page.GetByTestId("automation-settings-button").ClickAsync();
            await page.GetByTestId("automation-drawer").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.WaitForFunctionAsync("""
                () => {
                    const drawer = document.querySelector("[data-testid='automation-drawer']");
                    if (!drawer) return false;
                    const bounds = drawer.getBoundingClientRect();
                    return bounds.width > 0 && bounds.left < window.innerWidth - 1 && bounds.right <= window.innerWidth + 1;
                }
                """);
            await AssertAutomationActionsReachableAsync(page, viewportName);
            await CaptureAutomationStateAsync(page, evidenceDirectory, viewportName, "open", width, height);
            await page.GetByTestId("close-automation-settings").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.activeElement?.closest('[data-testid=automation-drawer]') !== null"),
                "Keyboard focus must remain inside the open automation drawer.");
            await page.GetByTestId("automation-deploy-prerelease").CheckAsync();
            await page.GetByTestId("automation-validation").WaitForAsync();
            await CaptureAutomationStateAsync(page, evidenceDirectory, viewportName, "dirty", width, height);

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
            await CaptureAutomationStateAsync(page, evidenceDirectory, viewportName, "saving", width, height);
            fixture.Data.CompletePendingSave();
            await page.GetByTestId("automation-saving").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            await page.GetByTestId("automation-unsaved").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            Assert.Equal(2, fixture.Data.SaveCalls);

            await page.GetByTestId("automation-download-stable").UncheckAsync();
            fixture.Data.FailNextSave = true;
            await page.GetByTestId("save-automation-policy").ClickAsync();
            await page.GetByTestId("automation-drawer-error").WaitForAsync();
            Assert.Contains("Could not save the policy", await page.GetByTestId("automation-drawer-error").TextContentAsync());
            await page.GetByTestId("automation-drawer-error").EvaluateAsync(
                "element => element.scrollIntoView({ block: 'center', inline: 'nearest' })");
            await CaptureAutomationStateAsync(page, evidenceDirectory, viewportName, "error", width, height);
            await page.GetByTestId("automation-unsaved").WaitForAsync();
            await page.GetByTestId("cancel-automation-policy").ClickAsync();
            await page.GetByTestId("close-automation-settings").ClickAsync();
            await page.Locator("aside.mud-drawer.mud-drawer-temporary.mud-drawer--closed").WaitForAsync();

            await page.EvaluateAsync("() => window.scrollTo(0, 0)");
            if (await page.GetByTestId("mobile-overflow").IsVisibleAsync())
            {
                await SelectThemeOptionAsync(page, "mobile-overflow", "mobile-theme-option-dark");
            }
            else
            {
                await SelectThemeOptionAsync(page, "theme-preference-menu", "theme-option-dark");
            }
            await page.Locator("html[data-netratel-theme='dark']").WaitForAsync();

            if (await page.GetByTestId("mobile-overflow").IsVisibleAsync())
            {
                await SelectThemeOptionAsync(page, "mobile-overflow", "mobile-theme-option-system");
            }
            else
            {
                await SelectThemeOptionAsync(page, "theme-preference-menu", "theme-option-system");
            }
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            await page.Locator("html[data-netratel-theme='dark']").WaitForAsync();
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
            await page.Locator("html[data-netratel-theme='light']").WaitForAsync();

            // Wait for the closed drawer to finish sliding out before capturing the full page.
            await page.WaitForFunctionAsync(
                """
                () => {
                    const drawer = document.querySelector("[data-testid='automation-drawer']")?.closest("aside");
                    if (!drawer) return false;
                    const animations = drawer.getAnimations();
                    return drawer.getBoundingClientRect().left >= window.innerWidth &&
                        animations.every(animation => animation.playState !== "running" && !animation.pending);
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, $"clients-management-{viewportName}-{width}x{height}.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });
            RecordEvidence(viewportName, width, height, textScalePercent, evidenceDirectory);

        }
        catch (Exception exception)
        {
            testFailureInFlight = exception;
            await CaptureFailureDiagnosticsBestEffortAsync(
                browser,
                page,
                startupDiagnostics,
                fixture,
                $"responsive-{viewportName}-{width}x{height}-text-{textScalePercent}",
                width,
                height,
                textScalePercent,
                exception);
            throw;
        }
        finally
        {
            await CloseContextPreservingFailureAsync(context, testFailureInFlight);
        }
    }

    [Fact]
    public async Task AbortedCriticalStartupScript_FailsAndWritesDiagnosticsBeforeContextDisposal()
    {
        var browser = _browserFixture.Browser;
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        const int width = 1280;
        const int height = 800;
        const int textScalePercent = 100;
        const string viewportName = "diagnostic-regression";
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ColorScheme = ColorScheme.Light
        });
        var page = await context.NewPageAsync();
        var startupDiagnostics = new BrowserStartupDiagnostics(page);
        Exception? testFailureInFlight = null;

        try
        {
            await page.RouteAsync("**/_framework/blazor.web.js", route => route.AbortAsync("aborted"));
            var failure = await Record.ExceptionAsync(() => NavigateAndWaitForShellAsync(
                page,
                fixture,
                startupDiagnostics,
                viewportName,
                shellTimeoutMilliseconds: 5_000));
            Assert.NotNull(failure);
            Assert.IsType<TimeoutException>(failure);
            Assert.IsType<TimeoutException>(failure.InnerException);
            Assert.Contains("/_framework/blazor.web.js", failure.Message, StringComparison.Ordinal);
            Assert.Contains("net::ERR_ABORTED", failure.Message, StringComparison.Ordinal);

            await CaptureFailureDiagnosticsBestEffortAsync(
                browser,
                page,
                startupDiagnostics,
                fixture,
                $"responsive-{viewportName}-{width}x{height}-text-{textScalePercent}",
                width,
                height,
                textScalePercent,
                failure);

            Assert.True(failure.Data.Contains(FailureEvidenceDirectoryDataKey));
            var evidenceDirectory = Assert.IsType<string>(failure.Data[FailureEvidenceDirectoryDataKey]);
            var diagnosticPath = Path.Combine(evidenceDirectory, "startup-diagnostics.json");
            var screenshotPath = Path.Combine(evidenceDirectory, "startup-failure.png");
            Assert.True(File.Exists(diagnosticPath), $"Startup diagnostics were not written before context disposal: {diagnosticPath}");
            Assert.True(File.Exists(screenshotPath), $"Startup screenshot was not written before context disposal: {screenshotPath}");

            var diagnosticsJson = await File.ReadAllTextAsync(diagnosticPath);
            Assert.Contains("/_framework/blazor.web.js", diagnosticsJson, StringComparison.Ordinal);
            Assert.Contains("ERR_ABORTED", diagnosticsJson, StringComparison.Ordinal);
            Assert.Contains("failedCriticalResourcePaths", diagnosticsJson, StringComparison.Ordinal);
            using var diagnostics = JsonDocument.Parse(diagnosticsJson);
            var circuitState = diagnostics.RootElement
                .GetProperty("webSocketAndCircuitState")
                .GetProperty("circuitState")
                .GetString();
            Assert.True(
                circuitState is "circuit not observed; no WebSocket observed" or "circuit readiness unknown; WebSocket observed",
                $"Unexpected circuit observation state: {circuitState}");
        }
        catch (Exception exception)
        {
            testFailureInFlight = exception;
            throw;
        }
        finally
        {
            await CloseContextPreservingFailureAsync(context, testFailureInFlight);
        }
    }

    [Fact]
    public async Task DelayedGitHubReloadRendersItsFixtureResponse_AndReachabilityRejectsMissingOrClippedTargets()
    {
        var browser = _browserFixture.Browser;
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1024, Height = 600 },
            ColorScheme = ColorScheme.Light
        });
        try
        {
            var page = await context.NewPageAsync();
            var startupEvents = new ConcurrentQueue<string>();
            var startupWebSockets = new ConcurrentQueue<IWebSocket>();
            var startupClock = Stopwatch.StartNew();
            var startupEventCount = 0;
            var startupWebSocketCount = 0;
            var startupDiagnosticsActive = 1;
            void RecordStartupEvent(string message)
            {
                if (Volatile.Read(ref startupDiagnosticsActive) == 0 ||
                    Interlocked.Increment(ref startupEventCount) > 32 ||
                    Volatile.Read(ref startupDiagnosticsActive) == 0)
                {
                    return;
                }

                var boundedMessage = message.Length <= 240 ? message : message[..240];
                startupEvents.Enqueue($"{startupClock.ElapsedMilliseconds,5} ms {boundedMessage}");
            }

            page.Console += (_, message) =>
            {
                if (message.Type is "error" or "warning")
                {
                    RecordStartupEvent($"console {message.Type}");
                }
            };
            page.PageError += (_, _) => RecordStartupEvent("page error event");
            page.RequestFailed += (_, request) =>
            {
                var method = request.Method is "GET" or "POST" ? request.Method : "other";
                RecordStartupEvent(
                    $"request failed {method} {GetSafeStartupRequestPath(request.Url)}: {GetSafeNetworkFailure(request.Failure)}");
            };
            page.Response += (_, response) =>
            {
                var path = GetSafeStartupRequestPath(response.Url);
                if (response.Request.IsNavigationRequest || response.Status >= 400 ||
                    path is "/_framework/blazor.web.js" or "/_content/MudBlazor/MudBlazor.min.js" ||
                    path.StartsWith("/_blazor", StringComparison.Ordinal))
                {
                    RecordStartupEvent($"response {response.Status} {path}");
                }
            };
            page.WebSocket += (_, webSocket) =>
            {
                if (Volatile.Read(ref startupDiagnosticsActive) == 0)
                {
                    return;
                }

                var path = GetSafeStartupRequestPath(webSocket.Url);
                if (Interlocked.Increment(ref startupWebSocketCount) <= 12 &&
                    Volatile.Read(ref startupDiagnosticsActive) != 0)
                {
                    startupWebSockets.Enqueue(webSocket);
                }

                RecordStartupEvent($"websocket request {path}");
                webSocket.SocketError += (_, _) => RecordStartupEvent($"websocket error {path}");
                webSocket.Close += (_, _) => RecordStartupEvent($"websocket closed {path}");
            };
            page.SetDefaultTimeout(30_000);
            var response = await page.GotoAsync($"{fixture.BaseAddress}/clients/mgmt", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Client-management fixture returned HTTP {response.Status}.");

            var shellWait = new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 };
            try
            {
                await page.GetByTestId("app-main-content").WaitForAsync(shellWait);
                Interlocked.Exchange(ref startupDiagnosticsActive, 0);
            }
            catch (TimeoutException exception)
            {
                Interlocked.Exchange(ref startupDiagnosticsActive, 0);
                var domSummary = "unavailable";
                try
                {
                    domSummary = await page.EvaluateAsync<string>("""
                        () => {
                            const mainContent = document.querySelector('[data-testid="app-main-content"]');
                            const errorUi = document.querySelector('#blazor-error-ui');
                            const errorStyle = errorUi ? getComputedStyle(errorUi) : null;
                            return [
                                `ready=${document.readyState}`,
                                `shell=${Boolean(mainContent)}`,
                                `blazorErrorVisible=${Boolean(errorUi && errorStyle && errorStyle.display !== 'none' && errorStyle.visibility !== 'hidden')}`,
                                `bodyChildren=${document.body?.children.length ?? 0}`,
                                `frameworkScript=${Boolean(document.querySelector('script[src="/_framework/blazor.web.js"]'))}`
                            ].join(',');
                        }
                        """).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception diagnosticException) when (diagnosticException is PlaywrightException or TimeoutException)
                {
                    domSummary = $"unavailable ({diagnosticException.GetType().Name})";
                }

                throw new TimeoutException(
                    $"The delayed GitHub fixture shell did not start at {GetSafeStartupRequestPath(page.Url)}. DOM summary: {domSummary}. Browser startup events: {FormatStartupEvents(startupEvents)}. WebSocket states: {FormatSafeWebSocketStates(startupWebSockets)}. Fixture server diagnostics: {FormatSafeServerDiagnostics(fixture)}",
                    exception);
            }
            await page.GetByTestId("client-management-tabs").WaitForAsync(shellWait);
            var initialImportPack = page.GetByTestId("github-release-name")
                .GetByText(ExpectedGitHubReleases[0].Name, new() { Exact = true })
                .Locator("xpath=ancestor::tr[1]")
                .GetByTestId("github-import-pack");
            await Assertions.Expect(initialImportPack, "The interactive Client catalogue should enable its verification-required Import pack action.")
                .ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });
            var initialRequest = fixture.Data.GitHubReleaseRequestCount;
            Assert.True(initialRequest > 0, "The interactive Client catalogue must have requested its release fixture.");
            await Assertions.Expect(page.GetByText(FixtureClientArtifactsService.GetGitHubCheckedText(initialRequest), new() { Exact = true }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

            await VisitTabAsync(page, "Packages", "artifact-management-panel");
            var previousRequest = fixture.Data.GitHubReleaseRequestCount;
            var reload = fixture.Data.PrepareDelayedGitHubReload();
            try
            {
                await VisitTabAsync(page, "GitHub releases", "github-release-catalogue");
                var request = await reload.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(previousRequest + 1, request);
                Assert.NotEqual(FixtureClientArtifactsService.GetGitHubCheckedText(previousRequest), reload.CheckedText);
                await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = "GitHub releases", Exact = true, Selected = true }))
                    .ToBeVisibleAsync();
                await Assertions.Expect(page.GetByTestId("github-release-catalogue")).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByText(FixtureClientArtifactsService.GetGitHubCheckedText(previousRequest), new() { Exact = true })).ToBeVisibleAsync();
                Assert.Equal(0, await page.GetByText(reload.CheckedText, new() { Exact = true }).CountAsync());

                reload.Release();
                await Assertions.Expect(page.GetByText(reload.CheckedText, new() { Exact = true }))
                    .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
                await Assertions.Expect(page.GetByRole(AriaRole.Status, new() { Name = "Loading GitHub releases", Exact = true }))
                    .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30_000 });
                await AssertReleaseRowContentReachableAsync(page, "delayed-reload");
            }
            finally
            {
                reload.Release();
            }

            var missingTarget = page.GetByTestId("reachability-missing-target");
            Assert.Equal(0, await missingTarget.CountAsync());
            var missingTargetFailure = await Assert.ThrowsAsync<TimeoutException>(() => MeasureHorizontalReachabilityAsync(
                missingTarget,
                "missing fixture target",
                "delayed-reload",
                timeoutMs: 500));
            Assert.Contains("missing fixture target at delayed-reload", missingTargetFailure.Message);

            await page.EvaluateAsync("""
                () => {
                    const container = document.createElement("div");
                    container.dataset.testid = "reachability-clipping-container";
                    Object.assign(container.style, {
                        position: "fixed",
                        left: "0px",
                        top: "0px",
                        width: "32px",
                        height: "28px",
                        overflow: "hidden",
                        pointerEvents: "none",
                        zIndex: "-1"
                    });
                    const target = document.createElement("span");
                    target.dataset.testid = "reachability-clipped-target";
                    Object.assign(target.style, { display: "block", width: "64px", height: "24px" });
                    container.append(target);
                    document.body.append(container);
                }
                """);
            var clippedTargetIssue = await MeasureHorizontalReachabilityAsync(
                page.GetByTestId("reachability-clipped-target"),
                "clipped fixture target",
                "delayed-reload");
            Assert.Contains("outside its hidden scroll region", clippedTargetIssue);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public void StartupDiagnosticsKeepOnlySafePathsAndFailureCategories()
    {
        const string url = "http://127.0.0.1/clients/mgmt?code=fixture-secret#access_token=fixture-secret";
        const string serverMessage = "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost [Warning]: access_token=fixture-secret | System.InvalidOperationException: fixture-secret";

        Assert.Equal("/clients/mgmt", GetSafeStartupRequestPath(url));
        Assert.Equal("(other)", GetSafeStartupRequestPath("https://fixture.test/clients/secret-capability"));
        Assert.Equal("/_content/(asset)", GetSafeStartupRequestPath("https://fixture.test/_content/secret-capability/asset.js?code=fixture-secret"));
        Assert.Equal("/_blazor/(endpoint)", GetSafeStartupRequestPath("https://fixture.test/_blazor/secret-capability?token=fixture-secret"));
        Assert.Equal("net::ERR_CONNECTION_RESET", GetSafeNetworkFailure("net::ERR_CONNECTION_RESET; token=fixture-secret"));
        Assert.Equal("net::ERR_NETWORK_CHANGED", GetSafeNetworkFailure("net::ERR_NETWORK_CHANGED"));
        Assert.Equal("network failure", GetSafeNetworkFailure("net::ERR_secret-capability-token"));
        Assert.Equal("network failure", GetSafeNetworkFailure("Authorization: Bearer fixture-secret"));
        Assert.Equal("Blazor circuit [Warning]", FormatSafeServerDiagnostic(serverMessage));
        Assert.DoesNotContain("fixture-secret", FormatSafeServerDiagnostic(serverMessage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedClientsDirectory_RendersOnlineOfflineEmptyAndErrorStates_AtDesktopAndNarrowWidths()
    {
        var browser = _browserFixture.Browser;
        var fixture = _fixture ?? throw new InvalidOperationException("Client management fixture was not initialized.");
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1600, Height = 900 },
            ColorScheme = ColorScheme.Light
        });

        try
        {
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(30_000);
            var response = await page.GotoAsync($"{fixture.BaseAddress}/clients", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Client directory fixture returned HTTP {response.Status}.");
            await Assertions.Expect(page.Locator(".client-title")).ToHaveTextAsync(["gateway-agent-01", "registered-offline-agent"]);
            Assert.Equal(2, await page.Locator(".client-card").CountAsync());
            var evidenceDirectory = GetPlaywrightArtifactRoot();
            Directory.CreateDirectory(evidenceDirectory);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, "clients-directory-desktop-light-populated-1600x900.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });

            var requestCount = fixture.ClientDirectory.RequestCount;
            var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" });
            await refresh.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await fixture.ClientDirectory.WaitForRequestAfterAsync(requestCount, TimeSpan.FromSeconds(10));
            Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement?.getAttribute('aria-label') === 'Refresh clients'"),
                "The directory refresh action must remain operable from keyboard focus.");

            var search = page.GetByRole(AriaRole.Textbox, new() { Name = "Search" });
            await search.FillAsync("registered-offline-agent");
            await Assertions.Expect(page.Locator(".client-title")).ToHaveTextAsync(["registered-offline-agent"]);
            await page.GetByRole(AriaRole.Button, new() { Name = "Show all clients" }).ClickAsync();
            await search.FillAsync(string.Empty);

            await page.SetViewportSizeAsync(390, 844);
            await page.EvaluateAsync("() => document.documentElement.style.fontSize = '32px'");
            if (await page.GetByTestId("mobile-overflow").IsVisibleAsync())
            {
                await SelectThemeOptionAsync(page, "mobile-overflow", "mobile-theme-option-dark", _testOutputHelper);
            }
            else
            {
                await SelectThemeOptionAsync(page, "theme-preference-menu", "theme-option-dark");
            }

            await page.Locator("html[data-netratel-theme='dark']").WaitForAsync();
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"),
                "The directory must reflow without horizontal overflow at narrow width and 200% effective text size.");
            var narrowLayoutProblems = await page.EvaluateAsync<string[]>(
                """
                () => {
                const problems = [];
                const viewportRight = window.innerWidth;
                for (const label of ['Refresh clients', 'Switch client view']) {
                    const button = document.querySelector(`[aria-label="${label}"]`);
                    if (!button) {
                        problems.push(`${label} control is missing`);
                        continue;
                    }

                    const rect = button.getBoundingClientRect();
                    const paper = button.closest('.mud-paper');
                    const paperRect = paper?.getBoundingClientRect();
                    if (rect.left < 0 || rect.right > viewportRight || (paperRect && (rect.left < paperRect.left || rect.right > paperRect.right))) {
                        problems.push(`${label} control is outside the viewport or filter panel (${rect.left.toFixed(1)}..${rect.right.toFixed(1)})`);
                    }
                }

                for (const selector of ['.client-title', '.client-host']) {
                    for (const element of document.querySelectorAll(selector)) {
                        if (element.scrollWidth > element.clientWidth + 1) {
                            problems.push(`${selector} text is clipped (${element.scrollWidth}px in ${element.clientWidth}px)`);
                        }
                    }
                }

                return problems;
                }
                """);
            Assert.Empty(narrowLayoutProblems);
            await Assertions.Expect(page.Locator(".client-title")).ToHaveTextAsync(["gateway-agent-01", "registered-offline-agent"]);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, "clients-directory-phone-dark-populated-390x844-text-200.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });

            fixture.ClientDirectory.SetMode(ClientDirectoryFixtureMode.Empty);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
            await page.GetByText("No registered clients yet.").WaitForAsync();
            await Assertions.Expect(page.GetByText("No clients match the active filters.")).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, "clients-directory-phone-dark-empty-390x844-text-200.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });

            fixture.ClientDirectory.SetMode(ClientDirectoryFixtureMode.Unavailable);
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
            await page.GetByRole(AriaRole.Alert).GetByText("could not load the client directory").WaitForAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(evidenceDirectory, "clients-directory-phone-dark-error-390x844-text-200.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });

            fixture.ClientDirectory.SetMode(ClientDirectoryFixtureMode.Populated);
            await page.GetByTestId("retry-client-directory").ClickAsync();
            await Assertions.Expect(page.Locator(".client-title")).ToHaveTextAsync(["gateway-agent-01", "registered-offline-agent"]);
            await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        _fixture = await ClientsManagementFixtureHost.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private static string GetPlaywrightArtifactRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (!Path.IsPathFullyQualified(configuredRoot))
            {
                throw new InvalidOperationException("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT must be an absolute path.");
            }

            return Path.GetFullPath(configuredRoot);
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright"));
    }

    private async Task CaptureFailureDiagnosticsBestEffortAsync(
        IBrowser browser,
        IPage? page,
        BrowserStartupDiagnostics? startupDiagnostics,
        ClientsManagementFixtureHost fixture,
        string caseName,
        int width,
        int height,
        int textScalePercent,
        Exception originalException)
    {
        var evidenceDirectory = "(unavailable)";
        try
        {
            evidenceDirectory = Path.Combine(
                GetPlaywrightArtifactRoot(), "failures", SanitizePathSegment(caseName),
                $"attempt-{SafeMetadata(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), "^[0-9]{1,6}$", "local")}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(evidenceDirectory);
            var captureFailures = new List<string>();
            var screenshotPath = Path.Combine(evidenceDirectory, "startup-failure.png");
            if (page is not null)
            {
                try
                {
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = screenshotPath,
                        FullPage = true,
                        Animations = ScreenshotAnimations.Disabled,
                        Timeout = 3_000
                    });
                }
                catch (Exception captureException)
                {
                    captureFailures.Add($"screenshot capture failed ({captureException.GetType().Name})");
                }
            }
            else
            {
                captureFailures.Add("screenshot capture unavailable (page was not created)");
            }

            var payload = new
            {
                schema = "netratel-playwright-startup-diagnostics-v1",
                capturedAtUtc = DateTimeOffset.UtcNow,
                sourceSha = SafeMetadata(Environment.GetEnvironmentVariable("NETRATEL_REVIEW_SOURCE_SHA") ?? Environment.GetEnvironmentVariable("GITHUB_SHA"), "^[0-9a-fA-F]{7,64}$", "local"),
                testedSha = SafeMetadata(Environment.GetEnvironmentVariable("NETRATEL_REVIEW_TEST_MERGE_SHA") ?? Environment.GetEnvironmentVariable("GITHUB_SHA"), "^[0-9a-fA-F]{7,64}$", "local"),
                runId = SafeMetadata(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), "^[0-9]{1,20}$", "local"),
                runAttempt = SafeMetadata(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), "^[0-9]{1,6}$", "local"),
                dotnetSdkVersion = SafeMetadata(Environment.GetEnvironmentVariable("NETRATEL_DOTNET_SDK_VERSION"), "^[0-9A-Za-z.+-]{1,40}$", "unknown"),
                browser = new { name = "Chromium", version = SafeMetadata(browser.Version, "^[0-9A-Za-z.+-]{1,40}$", "unknown") },
                viewport = new { name = caseName, width, height, textScalePercent },
                currentPath = page is null ? "(unavailable)" : GetSafeStartupRequestPath(page.Url),
                failedCriticalResourcePaths = startupDiagnostics?.FailedCriticalResourcePaths ?? [],
                webSocketAndCircuitState = new
                {
                    webSockets = startupDiagnostics is null ? "(none)" : FormatSafeWebSocketStates(startupDiagnostics.WebSockets),
                    circuitState = startupDiagnostics is null || startupDiagnostics.WebSockets.IsEmpty
                        ? "circuit not observed; no WebSocket observed"
                        : "circuit readiness unknown; WebSocket observed"
                },
                fixtureServerDiagnostics = FormatSafeServerDiagnostics(fixture),
                failure = new { exceptionType = originalException.GetType().FullName },
                timeline = startupDiagnostics?.Events.ToArray() ?? [],
                omittedTimelineEvents = startupDiagnostics?.OmittedTimelineEvents ?? 0,
                screenshot = File.Exists(screenshotPath) ? Path.GetFileName(screenshotPath) : null,
                diagnosticCaptureFailures = captureFailures
            };

            var diagnosticPath = Path.Combine(evidenceDirectory, "startup-diagnostics.json");
            await File.WriteAllTextAsync(diagnosticPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            originalException.Data[FailureEvidenceDirectoryDataKey] = evidenceDirectory;
            _testOutputHelper.WriteLine($"Browser failure diagnostics were written before context disposal: {evidenceDirectory}");
        }
        catch (Exception captureException)
        {
            _testOutputHelper.WriteLine($"Best-effort browser failure diagnostics could not be completed ({captureException.GetType().Name}).");
        }
    }

    private async Task CloseContextPreservingFailureAsync(IBrowserContext context, Exception? testFailureInFlight)
    {
        try
        {
            await context.CloseAsync();
        }
        catch (Exception cleanupException) when (testFailureInFlight is not null)
        {
            _testOutputHelper.WriteLine($"Browser context cleanup failed while preserving the test failure ({cleanupException.GetType().Name}).");
        }
    }

    private static async Task NavigateAndWaitForShellAsync(
        IPage page,
        ClientsManagementFixtureHost fixture,
        BrowserStartupDiagnostics startupDiagnostics,
        string viewportName,
        int shellTimeoutMilliseconds)
    {
        var response = await page.GotoAsync(
            $"{fixture.BaseAddress}/clients/mgmt",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Client-management fixture returned HTTP {response.Status}.");

        try
        {
            await page.GetByTestId("app-main-content").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = shellTimeoutMilliseconds
            });
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"The {viewportName} shell did not start at {GetSafeStartupRequestPath(page.Url)}. Browser startup events: {startupDiagnostics.FormatTimeline()}. WebSocket/circuit state: {FormatSafeWebSocketStates(startupDiagnostics.WebSockets)}. Fixture server startup warnings/errors: {FormatSafeServerDiagnostics(fixture)}",
                exception);
        }
    }

    private static string SafeMetadata(string? value, string allowedPattern, string fallback) =>
        value is not null && Regex.IsMatch(value, allowedPattern, RegexOptions.CultureInvariant)
            ? value
            : fallback;

    private static string SanitizePathSegment(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9.-]", "-");
        return sanitized.Length <= 100 ? sanitized : sanitized[..100];
    }

    private sealed class BrowserStartupDiagnostics
    {
        private const int MaximumTimelineEvents = 250;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public ConcurrentQueue<BrowserTimelineEvent> Events { get; } = new();
        public ConcurrentQueue<IWebSocket> WebSockets { get; } = new();
        private readonly ConcurrentDictionary<string, byte> _failedCriticalResourcePaths = new(StringComparer.Ordinal);
        private int _eventCount;
        private int _omittedTimelineEvents;

        public BrowserStartupDiagnostics(IPage page)
        {
            page.Request += (_, request) => Record("request", GetSafeStartupRequestPath(request.Url));
            page.Console += (_, message) =>
            {
                if (message.Type is "error" or "warning")
                {
                    Record("console", GetSafeConsoleResourcePath(message.Location), severity: message.Type);
                }
            };
            page.PageError += (_, _) => Record("pageerror");
            page.RequestFailed += (_, request) =>
            {
                var path = GetSafeStartupRequestPath(request.Url);
                if (IsCriticalStartupPath(path))
                {
                    _failedCriticalResourcePaths.TryAdd(path, 0);
                }

                Record("requestfailed", path, failure: GetSafeNetworkFailure(request.Failure));
            };
            page.Response += (_, response) =>
            {
                var path = GetSafeStartupRequestPath(response.Url);
                if (response.Request.IsNavigationRequest || response.Status >= 400 ||
                    IsCriticalStartupPath(path) || path.StartsWith("/_blazor/", StringComparison.Ordinal))
                {
                    Record("response", path, status: response.Status);
                }
            };
            page.FrameNavigated += (_, frame) => Record("navigation", GetSafeStartupRequestPath(frame.Url));
            page.WebSocket += (_, webSocket) =>
            {
                var path = GetSafeStartupRequestPath(webSocket.Url);
                WebSockets.Enqueue(webSocket);
                Record("websocket", path);
                webSocket.SocketError += (_, _) => Record("websocket-error", path);
                webSocket.Close += (_, _) => Record("websocket-closed", path);
            };
        }

        public IReadOnlyList<string> FailedCriticalResourcePaths => _failedCriticalResourcePaths.Keys
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        public int OmittedTimelineEvents => Volatile.Read(ref _omittedTimelineEvents);

        public string FormatTimeline()
        {
            var timeline = string.Join(" | ", Events.Select(item => item.ToString()));
            return OmittedTimelineEvents == 0 ? timeline : $"{timeline} | {OmittedTimelineEvents} additional event(s) omitted";
        }

        private void Record(string kind, string? path = null, int? status = null, string? failure = null, string? severity = null)
        {
            if (Interlocked.Increment(ref _eventCount) > MaximumTimelineEvents)
            {
                Interlocked.Increment(ref _omittedTimelineEvents);
                return;
            }

            Events.Enqueue(new BrowserTimelineEvent(_clock.ElapsedMilliseconds, kind, path, status, failure, severity));
        }

        private static bool IsCriticalStartupPath(string path) =>
            path is "/_framework/blazor.web.js" or "/_content/MudBlazor/MudBlazor.min.js" or "/js/theme-preference.js" ||
            path.StartsWith("/_blazor/", StringComparison.Ordinal);
    }

    private sealed record BrowserTimelineEvent(long ElapsedMilliseconds, string Kind, string? Path, int? Status, string? Failure, string? Severity)
    {
        public override string ToString() =>
            $"{ElapsedMilliseconds,5} ms {Kind}{(Severity is null ? "" : $" severity={Severity}")}{(Status is null ? "" : $" status={Status}")}{(Failure is null ? "" : $" failure={Failure}")}{(Path is null ? "" : $" path={Path}")}";
    }

    private static string GetSafeConsoleResourcePath(string location)
    {
        var columnSeparator = location.LastIndexOf(':');
        var lineSeparator = columnSeparator > 0 ? location.LastIndexOf(':', columnSeparator - 1) : -1;
        return lineSeparator > 0 ? GetSafeStartupRequestPath(location[..lineSeparator]) : "(unknown)";
    }

    private static string FormatStartupEvents(ConcurrentQueue<string> events) =>
        events.IsEmpty ? "(none)" : string.Join(" | ", events);

    private static string FormatSafeWebSocketStates(ConcurrentQueue<IWebSocket> webSockets) =>
        webSockets.IsEmpty
            ? "(none)"
            : string.Join(", ", webSockets.Take(12).Select(webSocket => $"{GetSafeStartupRequestPath(webSocket.Url)} closed={webSocket.IsClosed}"));

    private static string FormatSafeServerDiagnostics(ClientsManagementFixtureHost fixture)
    {
        var messages = fixture.StartupServerDiagnostics;
        if (messages.Count == 0)
        {
            return "(none)";
        }

        var categories = messages.Take(12).Select(FormatSafeServerDiagnostic);
        var omittedCount = messages.Count - Math.Min(messages.Count, 12);
        return omittedCount == 0
            ? string.Join(" | ", categories)
            : $"{string.Join(" | ", categories)} | {omittedCount} additional event(s) omitted";
    }

    private static string FormatSafeServerDiagnostic(string message)
    {
        var levelStart = message.IndexOf(" [", StringComparison.Ordinal);
        if (levelStart <= 0)
        {
            return "unknown server warning/error";
        }

        var levelEnd = message.IndexOf(']', levelStart + 2);
        if (levelEnd < 0)
        {
            return "unknown server warning/error";
        }

        var category = GetSafeServerDiagnosticCategory(message[..levelStart]);
        var rawLevel = message[(levelStart + 2)..levelEnd];
        var level = rawLevel is "Warning" or "Error" ? rawLevel : "other";
        return $"{category} [{level}]";
    }

    private static string GetSafeServerDiagnosticCategory(string category) => category switch
    {
        "Microsoft.AspNetCore.Server.Kestrel" => "Kestrel",
        "Microsoft.AspNetCore.Hosting.Diagnostics" => "Hosting",
        "Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware" => "Static files",
        "Microsoft.Hosting.Lifetime" => "Host lifetime",
        _ when category.StartsWith("Microsoft.AspNetCore.Components.Server.Circuits.", StringComparison.Ordinal) => "Blazor circuit",
        _ when category.StartsWith("Microsoft.AspNetCore.Http.Connections.", StringComparison.Ordinal) => "SignalR connection",
        _ when category.StartsWith("Microsoft.AspNetCore.SignalR.", StringComparison.Ordinal) => "SignalR",
        _ => "other server"
    };

    private static string GetSafeStartupRequestPath(string requestUrl)
    {
        var path = GetSafeRequestPath(requestUrl);
        return path switch
        {
            "/" or "/clients/mgmt" or "/_framework/blazor.web.js" or "/_content/MudBlazor/MudBlazor.min.js" or "/js/theme-preference.js" or "/app-site.css" or "/NetRatel.Web.styles.css" => path,
            _ when path.StartsWith("/_framework/", StringComparison.Ordinal) => "/_framework/(asset)",
            _ when path.StartsWith("/_content/", StringComparison.Ordinal) => "/_content/(asset)",
            _ when path.StartsWith("/_blazor", StringComparison.Ordinal) => "/_blazor/(endpoint)",
            _ => "(other)"
        };
    }

    private static string GetSafeNetworkFailure(string? failure)
    {
        if (failure is not null)
        {
            foreach (var knownFailure in SafeNetworkFailures)
            {
                if (failure.Equals(knownFailure, StringComparison.Ordinal) ||
                    failure.StartsWith($"{knownFailure};", StringComparison.Ordinal) ||
                    failure.StartsWith($"{knownFailure} ", StringComparison.Ordinal))
                {
                    return knownFailure;
                }
            }
        }

        return "network failure";
    }

    private static string GetSafeRequestPath(string requestUrl) =>
        Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : requestUrl.Split('?', 2)[0];

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
        await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = tabName, Exact = true, Selected = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId(panelTestId))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }

    private static async Task AssertReleaseRowContentReachableAsync(IPage page, string viewportName)
    {
        var releaseNames = page.GetByTestId("github-release-name");
        Assert.Equal(ExpectedGitHubReleases.Length, await releaseNames.CountAsync());
        Assert.Equal(ExpectedGitHubReleases.Length, await page.GetByTestId("github-release-actions").CountAsync());
        foreach (var expected in ExpectedGitHubReleases)
        {
            var releaseName = releaseNames.GetByText(expected.Name, new() { Exact = true });
            Assert.Equal(1, await releaseName.CountAsync());
            var row = releaseName.Locator("xpath=ancestor::tr[1]");
            Assert.Equal(1, await row.CountAsync());
            await AssertHorizontallyReachableAsync(releaseName, $"release '{expected.Name}' name", viewportName);

            var actionCell = row.GetByTestId("github-release-actions");
            Assert.Equal(1, await actionCell.CountAsync());
            var actionButtons = actionCell.GetByRole(AriaRole.Button);
            Assert.Equal(1, await actionButtons.CountAsync());
            var action = actionButtons.First;
            Assert.Equal("Import pack", (await action.InnerTextAsync()).Trim());
            Assert.Equal(expected.ImportPackEnabled, await action.IsEnabledAsync());
            Assert.Equal(1, await row.GetByRole(AriaRole.Link, new() { Name = "Release details", Exact = true }).CountAsync());
            await AssertHorizontallyReachableAsync(action, $"release '{expected.Name}' action", viewportName);
        }

        var firstRelease = releaseNames.GetByText(ExpectedGitHubReleases[0].Name, new() { Exact = true }).Locator("xpath=ancestor::tr[1]");
        var importPack = firstRelease.GetByTestId("github-import-pack");
        Assert.Equal(1, await importPack.CountAsync());
        Assert.True(await importPack.IsEnabledAsync(), "The fixture's verification-required Import pack action must remain enabled.");
        await importPack.FocusAsync();
        Assert.True(
            await importPack.EvaluateAsync<bool>("element => document.activeElement === element"),
            $"The Import pack action must remain keyboard reachable at {viewportName}.");
        await AssertHorizontallyReachableAsync(importPack, "Import pack action", viewportName);
    }

    private static async Task AssertHorizontallyReachableAsync(ILocator element, string description, string viewportName)
    {
        var issue = await MeasureHorizontalReachabilityAsync(element, description, viewportName);
        Assert.True(
            issue is null,
            $"{description} is clipped or not reachable at {viewportName}: {issue}");
    }

    private static async Task<string?> MeasureHorizontalReachabilityAsync(
        ILocator element,
        string description,
        string viewportName,
        float timeoutMs = 30_000)
    {
        var budget = TimeSpan.FromMilliseconds(timeoutMs);
        var elapsed = Stopwatch.StartNew();
        var lastTransient = "the target did not produce a stable geometry sample";
        TimeoutException? lastTimeout = null;
        while (elapsed.Elapsed < budget)
        {
            var remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                var evaluation = element.EvaluateAsync<string>("""
                async element => {
                    const transient = reason => JSON.stringify({ status: "transient", reason });
                    const failure = (issue, rect) => JSON.stringify({
                        status: "failure",
                        issue,
                        bounds: `${rect.left.toFixed(1)}..${rect.right.toFixed(1)} of ${window.innerWidth}px`
                    });
                    if (!element.isConnected) return transient("target detached before geometry sampling");
                    element.scrollIntoView({ behavior: "instant", block: "nearest", inline: "nearest" });
                    if (!element.isConnected) return transient("target detached while scrolling into view");

                    const firstRect = element.getBoundingClientRect();
                    const firstStyle = getComputedStyle(element);
                    if (firstStyle.display === "none" || firstStyle.visibility === "hidden" || firstStyle.visibility === "collapse" || firstRect.width <= 0 || firstRect.height <= 0) {
                        return failure("is not visible or has no visible box", firstRect);
                    }

                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    if (!element.isConnected) return transient("target detached during the two-frame stability check");

                    const failures = [];
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse" || rect.width <= 0 || rect.height <= 0) {
                        return failure("is not visible or has no visible box", rect);
                    }
                    if (Math.abs(rect.left - firstRect.left) > 0.25 || Math.abs(rect.top - firstRect.top) > 0.25 || Math.abs(rect.width - firstRect.width) > 0.25 || Math.abs(rect.height - firstRect.height) > 0.25) {
                        return transient("target geometry changed during the two-frame stability check");
                    }
                    if (rect.left < -1 || rect.right > window.innerWidth + 1) {
                        failures.push(`extends beyond the viewport (${rect.left.toFixed(1)}..${rect.right.toFixed(1)} of ${window.innerWidth}px)`);
                    }

                    for (let ancestor = element.parentElement; ancestor; ancestor = ancestor.parentElement) {
                        const overflowX = getComputedStyle(ancestor).overflowX;
                        if (!["hidden", "clip", "auto", "scroll"].includes(overflowX)) {
                            continue;
                        }

                        const ancestorRect = ancestor.getBoundingClientRect();
                        const visibleLeft = ancestorRect.left + ancestor.clientLeft;
                        const visibleRight = visibleLeft + ancestor.clientWidth;
                        if (rect.left < visibleLeft - 1 || rect.right > visibleRight + 1) {
                            failures.push(`is outside its ${overflowX} scroll region (${rect.left.toFixed(1)}..${rect.right.toFixed(1)} vs ${visibleLeft.toFixed(1)}..${visibleRight.toFixed(1)})`);
                        }
                    }

                    const ownOverflowX = getComputedStyle(element).overflowX;
                    if (!["auto", "scroll"].includes(ownOverflowX) && element.scrollWidth > element.clientWidth + 1) {
                        failures.push(`has horizontally overflowing content (${element.scrollWidth}px in ${element.clientWidth}px)`);
                    }
                    return JSON.stringify({
                        status: "complete",
                        issue: failures.length === 0 ? null : failures.join("; "),
                        bounds: `${rect.left.toFixed(1)}..${rect.right.toFixed(1)} of ${window.innerWidth}px`
                    });
                }
                """, null, new LocatorEvaluateOptions { Timeout = (float)remaining.TotalMilliseconds });
                var snapshot = await evaluation.WaitAsync(remaining);
                using var document = JsonDocument.Parse(snapshot);
                var status = document.RootElement.GetProperty("status").GetString();
                if (string.Equals(status, "transient", StringComparison.Ordinal))
                {
                    lastTransient = document.RootElement.GetProperty("reason").GetString() ?? lastTransient;
                    continue;
                }

                var issue = document.RootElement.GetProperty("issue").GetString();
                var bounds = document.RootElement.GetProperty("bounds").GetString();
                return issue is null ? null : $"{issue}; sampled bounds {bounds} ({description}, {viewportName})";
            }
            catch (PlaywrightException exception) when (exception.Message.Contains("Element is not attached to the DOM", StringComparison.Ordinal))
            {
                lastTransient = exception.Message;
            }
            catch (TimeoutException exception)
            {
                lastTransient = exception.Message;
                lastTimeout = exception;
                break;
            }
        }

        throw new TimeoutException(
            $"Could not resolve and stably measure {description} at {viewportName} within {timeoutMs:0} ms. Last transient state: {lastTransient}",
            lastTimeout);
    }

    private static async Task AssertAutomationActionsReachableAsync(IPage page, string viewportName)
    {
        var footer = page.GetByTestId("automation-action-footer");
        await footer.ScrollIntoViewIfNeededAsync();
        var issue = await footer.EvaluateAsync<string?>("""
            footer => {
                const failures = [];
                const selectors = [
                    "save-automation-policy",
                    "cancel-automation-policy",
                    "run-automation-policy",
                    "reload-automation-policy"
                ];
                const buttons = selectors.map(id => document.querySelector(`button[data-testid='${id}']`));
                if (buttons.some(button => !button)) {
                    return "one or more automation actions are missing from the footer";
                }

                const content = document.querySelector("[data-testid='automation-drawer-content']");
                const footerRect = footer.getBoundingClientRect();
                if (footerRect.width <= 0 || footerRect.height <= 0 || footerRect.top < -1 || footerRect.bottom > window.innerHeight + 1) {
                    failures.push("footer is clipped or outside the viewport");
                }
                if (!content) {
                    failures.push("settings content region is missing");
                } else if (content.getBoundingClientRect().bottom > footerRect.top + 1) {
                    failures.push("settings content region extends underneath the footer");
                }

                const rectangles = buttons.map((button, index) => {
                    const rect = button.getBoundingClientRect();
                    if (button.closest("[data-testid='automation-action-footer']") !== footer) {
                        failures.push(`${selectors[index]} is outside the shared footer`);
                    }
                    if (rect.width < 24 || rect.height < 24) {
                        failures.push(`${selectors[index]} is smaller than the 24px minimum target`);
                    }
                    if (rect.left < -1 || rect.right > window.innerWidth + 1 || rect.top < -1 || rect.bottom > window.innerHeight + 1) {
                        failures.push(`${selectors[index]} is clipped by the viewport`);
                    }
                    if (!button.disabled) {
                        const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
                        if (hit !== button && !button.contains(hit)) {
                            failures.push(`${selectors[index]} center is obstructed`);
                        }
                    }
                    return rect;
                });

                for (let left = 0; left < rectangles.length; left++) {
                    for (let right = left + 1; right < rectangles.length; right++) {
                        const overlapWidth = Math.min(rectangles[left].right, rectangles[right].right) - Math.max(rectangles[left].left, rectangles[right].left);
                        const overlapHeight = Math.min(rectangles[left].bottom, rectangles[right].bottom) - Math.max(rectangles[left].top, rectangles[right].top);
                        if (overlapWidth > 1 && overlapHeight > 1) {
                            failures.push(`${selectors[left]} overlaps ${selectors[right]}`);
                        }
                    }
                }

                return failures.length === 0 ? null : failures.join("; ");
            }
            """);

        Assert.True(
            issue is null,
            $"Automation actions are clipped, obstructed, or overlapping at {viewportName}: {issue}");
    }

    private static async Task CaptureAutomationStateAsync(
        IPage page,
        string evidenceDirectory,
        string viewportName,
        string state,
        int width,
        int height)
    {
        if (!string.Equals(viewportName, "desktop", StringComparison.Ordinal))
        {
            return;
        }

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(evidenceDirectory, $"clients-management-automation-{state}-{viewportName}-{width}x{height}.png"),
            FullPage = false
        });
    }

    private static async Task SelectThemeOptionAsync(IPage page, string menuTestId, string optionTestId, ITestOutputHelper? testOutputHelper = null)
    {
        await page.GetByTestId(menuTestId).ClickAsync();
        var option = page.GetByTestId(optionTestId);
        await option.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        if (testOutputHelper is not null && Environment.GetEnvironmentVariable("NETRATEL_MENU_VIEWPORT_DIAGNOSTICS") == "1")
        {
            testOutputHelper.WriteLine($"NETRATEL_MENU_VIEWPORT_DIAGNOSTIC before-scroll {await CaptureMenuViewportGeometryAsync(option).WaitAsync(TimeSpan.FromSeconds(3))}");
        }

        // Wait for the menu item itself to intersect after scrolling its list.
        await option.EvaluateAsync("element => element.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' })");
        if (testOutputHelper is not null && Environment.GetEnvironmentVariable("NETRATEL_MENU_VIEWPORT_DIAGNOSTICS") == "1")
        {
            testOutputHelper.WriteLine($"NETRATEL_MENU_VIEWPORT_DIAGNOSTIC after-scroll {await CaptureMenuViewportGeometryAsync(option).WaitAsync(TimeSpan.FromSeconds(3))}");
        }

        try
        {
            await Assertions.Expect(option)
                .ToBeInViewportAsync(new LocatorAssertionsToBeInViewportOptions { Ratio = 1, Timeout = 30_000 });
        }
        catch
        {
            if (testOutputHelper is not null)
            {
                try
                {
                    testOutputHelper.WriteLine($"NETRATEL_MENU_VIEWPORT_DIAGNOSTIC assertion-failed {await CaptureMenuViewportGeometryAsync(option).WaitAsync(TimeSpan.FromSeconds(3))}");
                }
                catch (Exception diagnosticFailure)
                {
                    testOutputHelper.WriteLine($"NETRATEL_MENU_VIEWPORT_DIAGNOSTIC capture-unavailable exception={diagnosticFailure.GetType().Name}");
                }
            }

            throw;
        }

        await option.ClickAsync();
    }

    private static Task<string> CaptureMenuViewportGeometryAsync(ILocator option) => option.EvaluateAsync<string>(
        """
        element => {
            const rect = node => {
                const bounds = node.getBoundingClientRect();
                return {
                    x: Math.round(bounds.x * 10) / 10,
                    y: Math.round(bounds.y * 10) / 10,
                    width: Math.round(bounds.width * 10) / 10,
                    height: Math.round(bounds.height * 10) / 10,
                    top: Math.round(bounds.top * 10) / 10,
                    bottom: Math.round(bounds.bottom * 10) / 10
                };
            };
            const ancestors = [];
            for (let node = element, depth = 0; node && depth < 10; node = node.parentElement, depth++) {
                const style = getComputedStyle(node);
                const bounds = node.getBoundingClientRect();
                const kind = node.matches('.mud-menu-item') ? 'menu-item'
                    : node.matches('.mud-list') ? 'menu-list'
                    : node.matches('.mud-menu-list-wrapper') ? 'menu-wrapper'
                    : node.matches('.mud-popover') ? 'popover'
                    : node === document.body ? 'body'
                    : 'ancestor';
                ancestors.push({
                    kind,
                    overflowY: style.overflowY,
                    overflowX: style.overflowX,
                    maxHeight: style.maxHeight,
                    height: style.height,
                    rect: rect(node),
                    scrollTop: Math.round(node.scrollTop * 10) / 10,
                    scrollHeight: node.scrollHeight,
                    clientHeight: node.clientHeight,
                    childElementCount: node.childElementCount
                });
                if (node === document.body) break;
            }

            const optionBounds = element.getBoundingClientRect();
            const visibleWidth = Math.max(0, Math.min(optionBounds.right, window.innerWidth) - Math.max(optionBounds.left, 0));
            const visibleHeight = Math.max(0, Math.min(optionBounds.bottom, window.innerHeight) - Math.max(optionBounds.top, 0));
            const optionArea = optionBounds.width * optionBounds.height;
            const visibleRatio = optionArea === 0 ? 0 : (visibleWidth * visibleHeight) / optionArea;
            const assetPaths = new Set(performance.getEntriesByType('resource').map(entry => {
                try { return new URL(entry.name, location.href).pathname; }
                catch { return ''; }
            }));

            return JSON.stringify({
                viewport: { width: window.innerWidth, height: window.innerHeight },
                rootFontSize: getComputedStyle(document.documentElement).fontSize,
                option: { visible: element.getClientRects().length > 0, rect: rect(element), viewportRatio: Math.round(visibleRatio * 1000) / 1000 },
                ancestors,
                mudBlazorAssets: {
                    cssLoaded: assetPaths.has('/_content/MudBlazor/MudBlazor.min.css'),
                    jsLoaded: assetPaths.has('/_content/MudBlazor/MudBlazor.min.js')
                }
            });
        }
        """,
        options: new LocatorEvaluateOptions { Timeout = 3_000 });

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
                        "all four automation action targets are reachable and non-overlapping",
                        "dirty navigation/Escape/close guard and focus containment/restoration",
                        "delayed save, visible saving state, disabled controls, and failed-save draft preservation",
                        "responsive overflow and 200% effective text scale"
                    },
                    screenshots,
                    automationStateScreenshots = screenshots
                        .Where(name => name.Contains("-automation-", StringComparison.Ordinal))
                        .ToArray(),
                    cases = EvidenceCases.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

public sealed class ClientsManagementBrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("Playwright browser was not initialized.");

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync();
            }
        }
        finally
        {
            _playwright?.Dispose();
        }
    }
}

[Route("/clients/mgmt")]
[Route("/clients")]
[Route("/flows")]
[Route("/monitoring")]
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
        AddStylesheet(builder, "/_content/VeloxDev.Razor/veloxdev.workflow.css");
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
    private readonly FixtureServerDiagnosticLoggerProvider _serverDiagnostics;

    private ClientsManagementFixtureHost(
        WebApplication application,
        string baseAddress,
        FixtureClientArtifactsService data,
        ClientDirectoryFixtureData clientDirectory,
        FixtureServerDiagnosticLoggerProvider serverDiagnostics)
    {
        _application = application;
        BaseAddress = baseAddress;
        Data = data;
        ClientDirectory = clientDirectory;
        _serverDiagnostics = serverDiagnostics;
    }

    public string BaseAddress { get; }
    public FixtureClientArtifactsService Data { get; }
    public ClientDirectoryFixtureData ClientDirectory { get; }
    public FixtureClientServicesService ServicesData => _application.Services.GetRequiredService<FixtureClientServicesService>();
    public FixtureFlowApiService FlowsData => _application.Services.GetRequiredService<FixtureFlowApiService>();
    public FixtureMonitoringApi MonitoringData => _application.Services.GetRequiredService<FixtureMonitoringApi>();
    public IReadOnlyList<string> StartupServerDiagnostics => _serverDiagnostics.Snapshot();

    public static async Task<ClientsManagementFixtureHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        var serverDiagnostics = new FixtureServerDiagnosticLoggerProvider();
        builder.Logging.AddProvider(serverDiagnostics);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddAuthentication("Fixture")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("Fixture", _ => { });
        builder.Services.AddAuthorization(_ => { });
        builder.Services.AddSingleton<FixtureClientArtifactsService>();
        builder.Services.AddSingleton<ClientDirectoryFixtureData>();
        builder.Services.AddSingleton<IHttpClientFactory, ClientDirectoryFixtureHttpClientFactory>();
        builder.Services.AddScoped<ClientPresenceApiService>();
        builder.Services.AddScoped<ClientPresentationService>();
        builder.Services.AddScoped<GatewayTelemetryApiService>();
        builder.Services.AddScoped<GatewayClientActionApiService>();
        builder.Services.AddSingleton<FixtureClientServicesService>();
        builder.Services.AddSingleton<IClientServicesApiService>(services => services.GetRequiredService<FixtureClientServicesService>());
        builder.Services.AddSingleton<IClientServicesLiveStreamService>(services => services.GetRequiredService<FixtureClientServicesService>());
        builder.Services.AddSingleton<FixtureFlowApiService>();
        builder.Services.AddSingleton<NetRatel.Web.Services.Flows.IFlowApiService>(services => services.GetRequiredService<FixtureFlowApiService>());
        builder.Services.AddSingleton<FixtureMonitoringApi>();
        builder.Services.AddSingleton<NetRatel.Web.Services.Monitoring.IMonitoringApiService>(services => services.GetRequiredService<FixtureMonitoringApi>());
        builder.Services.AddSingleton<IClientArtifactsService>(services => services.GetRequiredService<FixtureClientArtifactsService>());
        builder.Services.AddSingleton<ITenantApiService, FixtureTenantApiService>();
        builder.Services.AddSingleton<IDeploymentBrandingApiService, FixtureBrandingApiService>();
        builder.Services.AddSingleton<IAccessAdministrationApiService, FixtureAccessAdministrationApiService>();
        builder.Services.AddSingleton<IAppBarVersionApiClient, FixtureAppBarVersionApiClient>();
        builder.Services.AddSingleton<INetRatelNotificationApiClient, FixtureNotificationApiClient>();
        builder.Services.AddSingleton<NetRatelNotificationEventBus>();
        builder.Services.AddSingleton<IGlobalSearchService, FixtureGlobalSearchService>();

        configureServices?.Invoke(builder.Services);
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
        application.MapGet("/js/flows-editor.js", () => Results.File(ResolveWebAsset("wwwroot/js/flows-editor.js"), "text/javascript"));
        application.MapGet("/js/client-services-focus.js", () => Results.File(ResolveWebAsset("wwwroot/js/client-services-focus.js"), "text/javascript"));
        application.UseStaticFiles(new StaticFileOptions { FileProvider = ResolveStaticAssetProvider() });
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseAntiforgery();
        application.MapRazorComponents<ClientsManagementFixtureApp>().AddInteractiveServerRenderMode();
        await application.StartAsync().ConfigureAwait(false);
        var address = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new ClientsManagementFixtureHost(
            application,
            address,
            application.Services.GetRequiredService<FixtureClientArtifactsService>(),
            application.Services.GetRequiredService<ClientDirectoryFixtureData>(),
            serverDiagnostics);
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

internal sealed class FixtureServerDiagnosticLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Snapshot() => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new FixtureLogger(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class FixtureLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                var detail = formatter(state, exception);
                messages.Enqueue(exception is null
                    ? $"{categoryName} [{logLevel}]: {detail}"
                    : $"{categoryName} [{logLevel}]: {detail} | {exception}");
            }
        }
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

internal sealed class FixtureGitHubReloadGate
{
    private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string CheckedText => Started.Task.IsCompletedSuccessfully
        ? FixtureClientArtifactsService.GetGitHubCheckedText(Started.Task.Result)
        : throw new InvalidOperationException("The delayed GitHub reload has not started.");

    public void Release() => _released.TrySetResult(true);

    internal void MarkStarted(int requestNumber) => Started.TrySetResult(requestNumber);

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken) => _released.Task.WaitAsync(cancellationToken);
}

internal sealed class FixtureClientArtifactsService : IClientArtifactsService
{
    private readonly object _githubRequestLock = new();
    private TaskCompletionSource<int> _nextGitHubRequestStarted = NewSignal<int>();
    private FixtureGitHubReloadGate? _nextDelayedGitHubReload;
    private int _githubReleaseRequestCount;
    private ClientReleaseAutomationModel _automation = new();
    private TaskCompletionSource<bool>? _pendingSave;
    public int SaveCalls { get; private set; }
    public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
    public bool FailNextSave { get; set; }

    public int GitHubReleaseRequestCount
    {
        get
        {
            lock (_githubRequestLock)
            {
                return _githubReleaseRequestCount;
            }
        }
    }

    public async Task<int> WaitForGitHubReleaseRequestAfterAsync(int previousRequest, TimeSpan timeout)
    {
        Task<int> started;
        lock (_githubRequestLock)
        {
            if (_githubReleaseRequestCount > previousRequest)
            {
                return _githubReleaseRequestCount;
            }

            started = _nextGitHubRequestStarted.Task;
        }

        return await started.WaitAsync(timeout);
    }

    public FixtureGitHubReloadGate PrepareDelayedGitHubReload()
    {
        lock (_githubRequestLock)
        {
            if (_nextDelayedGitHubReload is not null)
            {
                throw new InvalidOperationException("A delayed GitHub fixture reload is already prepared.");
            }

            return _nextDelayedGitHubReload = new FixtureGitHubReloadGate();
        }
    }

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

    public async Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default)
    {
        int requestNumber;
        TaskCompletionSource<int> requestStarted;
        FixtureGitHubReloadGate? delayedReload;
        lock (_githubRequestLock)
        {
            requestNumber = ++_githubReleaseRequestCount;
            requestStarted = _nextGitHubRequestStarted;
            _nextGitHubRequestStarted = NewSignal<int>();
            delayedReload = _nextDelayedGitHubReload;
            _nextDelayedGitHubReload = null;
        }

        requestStarted.TrySetResult(requestNumber);
        if (delayedReload is not null)
        {
            delayedReload.MarkStarted(requestNumber);
            await delayedReload.WaitForReleaseAsync(ct);
        }

        var refreshedAtUtc = GetGitHubRefreshedAtUtc(requestNumber);
        return new GitHubClientReleasePageModel
        {
            Page = page,
            RefreshedAtUtc = refreshedAtUtc,
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
        };
    }

    public static string GetGitHubCheckedText(int requestNumber) =>
        $"Checked {GetGitHubRefreshedAtUtc(requestNumber).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

    private static DateTimeOffset GetGitHubRefreshedAtUtc(int requestNumber) =>
        new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(requestNumber);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
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

internal enum ClientDirectoryFixtureMode
{
    Populated,
    Empty,
    Unavailable
}

internal sealed class ClientDirectoryFixtureData
{
    private static readonly Guid OnlineAgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
    private static readonly Guid OfflineAgentId = Guid.Parse("99f5a0b0-5e61-4039-8d09-6c9d44c7c100");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private TaskCompletionSource<int> _requestChanged = NewSignal<int>();
    private ClientDirectoryFixtureMode _mode = ClientDirectoryFixtureMode.Populated;
    private int _requestCount;

    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return _requestCount;
            }
        }
    }

    public void SetMode(ClientDirectoryFixtureMode mode)
    {
        lock (_gate)
        {
            _mode = mode;
        }
    }

    public async Task<int> WaitForRequestAfterAsync(int previousRequest, TimeSpan timeout)
    {
        Task<int> requestStarted;
        lock (_gate)
        {
            if (_requestCount > previousRequest)
            {
                return _requestCount;
            }

            requestStarted = _requestChanged.Task;
        }

        return await requestStarted.WaitAsync(timeout);
    }

    public HttpResponseMessage Respond(HttpRequestMessage request)
    {
        if (request.RequestUri?.AbsolutePath == "/api/v2/client-presence")
        {
            ClientDirectoryFixtureMode mode;
            lock (_gate)
            {
                _requestCount++;
                mode = _mode;
                _requestChanged.TrySetResult(_requestCount);
                _requestChanged = NewSignal<int>();
            }

            if (mode == ClientDirectoryFixtureMode.Unavailable)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var entries = mode == ClientDirectoryFixtureMode.Empty
                ? []
                : new ClientPresenceDto[]
                {
                    new ClientPresenceDto(
                        $"gateway:3:{OnlineAgentId:D}", 3, OnlineAgentId, "gateway-agent-01", "gateway-agent-01", "Linux", "x64", true, true,
                        DateTimeOffset.UtcNow, "0.4.101", ["terminal-gateway", "file-gateway", "remote-support-gateway"], "gateway", "akka", true, 12,
                        new GatewayTerminalCapabilityDto(true, ["bash", "sh"], true, null, DateTimeOffset.UtcNow), "NetRatel"),
                    new ClientPresenceDto(
                        "gateway:3:offline", 3, OfflineAgentId, "registered-offline-agent", null, "Linux", "x64", false, true,
                        null, null, [], "gateway", "unobserved", false, 12, null, "NetRatel")
                };

            return Json(HttpStatusCode.OK, new ClientPresenceListDto("Akka", 12, entries));
        }

        if (request.RequestUri?.AbsolutePath == "/api/v2/agent-telemetry")
        {
            return Json(HttpStatusCode.OK, new[]
            {
                new GatewayTelemetrySummary(3, OnlineAgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new GatewayTelemetryCpu(37.5, 0.25, 100), new GatewayTelemetryMemory(1024, 624, 400, 61),
                    [new GatewayTelemetryDisk("/", 80, 20, 60, 25)],
                    [new GatewayTelemetryNetwork("eth0", 1000, 500)],
                    new GatewayTelemetryTransportHealth(3600, "0.4.101", "Linux", DateTimeOffset.UtcNow), "gateway", true)
            });
        }

        return Json(HttpStatusCode.OK, Array.Empty<object>());
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode statusCode, T payload) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions))
    };

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ClientDirectoryFixtureHttpClientFactory(ClientDirectoryFixtureData directory) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Handler(directory))
    {
        BaseAddress = new Uri("https://netratel.test")
    };

    private sealed class Handler(ClientDirectoryFixtureData directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(directory.Respond(request));
    }
}

internal sealed class FixtureTenantApiService : ITenantApiService
{
    public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantDto>>([new TenantDto(7, "Visual fixture", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
    public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
