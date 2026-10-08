using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Services.ServiceLinks;
using NetRatel.Web.Themes;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class HelpdeskM2MBrowserTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768, false, 100)]
    [InlineData(1366, 768, true, 100)]
    [InlineData(390, 844, false, 100)]
    [InlineData(390, 844, true, 100)]
    [InlineData(720, 900, false, 200)]
    [InlineData(720, 900, true, 200)]
    public async Task IncidentOnlyWizardNeedsNoBusinessResourcesAndKeepsActionsReachable(int width, int height, bool dark, int zoom)
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.Address + "/account/integration-credentials?purpose=helpdesk-m2m&theme=" + (dark ? "dark" : "light"));
        await page.GetByTestId("helpdesk-m2m-setup").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("helpdesk-connect")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("helpdesk-grant-selector").GetByRole(AriaRole.Combobox, new() { Name = "NetRatel tenant", Exact = true })).ToContainTextAsync("Fixture tenant");
        await Assertions.Expect(page.GetByTestId("helpdesk-resources")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("helpdesk-definitions")).ToHaveCountAsync(0);
        var start = page.GetByTestId("helpdesk-link-start");
        Assert.Equal(new[] { ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope },
            await start.Locator("input[name=inboundScopes]").EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.value)"));
        Assert.Equal(new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read" },
            await start.Locator("input[name=outboundScopes]").EvaluateAllAsync<string[]>("inputs => inputs.map(input => input.value)"));
        await Assertions.Expect(start.Locator("input[name=resourceIds], input[name=requestDefinitionIds]")).ToHaveCountAsync(0);
        await page.GetByTestId("helpdesk-peer-url").FillAsync("https://helpdesk.example.test");
        await Assertions.Expect(page.GetByTestId("helpdesk-connect")).ToBeEnabledAsync();
        await page.EvaluateAsync("scale => document.documentElement.style.zoom = scale", zoom / 100d);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"));
        foreach (var input in await page.GetByRole(AriaRole.Combobox).AllAsync())
        {
            if (!await input.IsVisibleAsync()) continue;
            var inputBounds = await input.BoundingBoxAsync();
            Assert.NotNull(inputBounds);
            Assert.True(inputBounds.X >= -1 && inputBounds.X + inputBounds.Width <= width + 1, "A visible grant selector must fit the rendered viewport.");
        }
        var close = page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
        await close.ScrollIntoViewIfNeededAsync();
        var bounds = await close.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.InRange(bounds.X, 0, width);
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"helpdesk-m2m-{width}-{(dark ? "dark" : "light")}-{zoom}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        Assert.InRange(bounds.Y + bounds.Height, 0, height + 1);
        Assert.Equal(0, fixture.Data.CreateCount);
        Assert.Equal(0, fixture.Data.StartCount);
        await close.ClickAsync();
        await page.GetByTestId("open-create-integration").ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Connection type", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option, new() { Name = "API / CLI / stdio MCP", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option, new() { Name = "HTTP MCP gateway", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Option, new() { Name = "Connect RatelDesk", Exact = true })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parent_native_submission_preserves_optional_peer_organization_and_flow_return(bool preset)
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        fixture.Data.AcceptStart = true;
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        const string peer = "https://helpdesk.example.test";
        const string organization = "fixture-organization-exact";
        const string returnUrl = "/flows?flowId=fixture";
        var query = "?purpose=helpdesk-m2m" + (preset
            ? "&peer_url=" + Uri.EscapeDataString(peer) + "&tenant_id=1&peer_tenant_id=" + organization + "&returnUrl=" + Uri.EscapeDataString(returnUrl)
            : "");
        await page.GotoAsync(fixture.Address + "/account/integration-credentials" + query);
        var form = page.GetByTestId("helpdesk-link-start");
        await form.WaitForAsync();
        await Assertions.Expect(form.Locator("input[name=requestedResponderTenantId]")).ToHaveValueAsync(preset ? organization : "");
        await Assertions.Expect(form.Locator("input[name=returnUrl]")).ToHaveValueAsync(preset ? returnUrl : "");
        if (preset) await Assertions.Expect(page.GetByTestId("helpdesk-peer-url")).ToHaveValueAsync(peer);
        else await page.GetByTestId("helpdesk-peer-url").FillAsync(peer);
        await Assertions.Expect(page.GetByTestId("helpdesk-connect")).ToBeEnabledAsync();

        await page.GetByTestId("helpdesk-connect").ClickAsync();
        await page.WaitForURLAsync(fixture.Address + "/fixture-peer-approval?attempt_id=fixture-submission");

        Assert.Equal(1, fixture.Data.StartCount);
        var submitted = Assert.IsType<ServiceLinkStartRequest>(fixture.Data.LastStart);
        Assert.Equal(peer, submitted.PeerWebBaseUrl);
        Assert.Equal("1", submitted.LocalTenantId);
        Assert.Equal(preset ? organization : null, submitted.RequestedResponderTenantId);
        Assert.Equal(new[] { ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope }, submitted.InboundScopes);
        Assert.Equal(new[] { "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.incidents.create" }, submitted.OutboundScopes);
        Assert.Empty(submitted.InboundResourceIds);
        Assert.Empty(submitted.InboundRequestDefinitionIds);
        var returned = await context.APIRequest.GetAsync(fixture.Address + "/account/integration-credentials/link/return", new() { MaxRedirects = 0 });
        Assert.Equal(preset ? returnUrl : "/account/integration-credentials", returned.Headers["location"]);
    }

    [Fact]
    public async Task Failed_start_offers_input_correction_with_safe_diagnostics_and_no_resume()
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.Address + "/account/integration-credentials?purpose=helpdesk-m2m");
        await page.GetByTestId("helpdesk-peer-url").FillAsync("https://helpdesk.example.test");
        await Assertions.Expect(page.GetByTestId("helpdesk-connect")).ToBeEnabledAsync();
        await page.GetByTestId("helpdesk-connect").ClickAsync();
        await page.GetByTestId("service-link-consent-page").WaitForAsync();

        Assert.Equal(1, fixture.Data.StartCount);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Correct setup and retry", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "View notifications", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Stage: start", new() { Exact = false })).ToContainTextAsync("0123456789abcdef0123456789abcdef");
        Assert.DoesNotContain("Resume", await page.Locator("body").InnerTextAsync());
    }

    [Fact]
    public async Task ManualClientRevealIsClearedAndOnlyManualRowsCanRotateOrRevoke()
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        fixture.Data.WithOrchestrationResources = true;
        // No trace or screenshot is captured by this secret-bearing journey.
        try
        {
            await page.GotoAsync(fixture.Address + "/account/integration-credentials?purpose=helpdesk-m2m");
            await page.GetByText("Manual service credentials (advanced)", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("manual-helpdesk-grant-selector").GetByRole(AriaRole.Combobox, new() { Name = "NetRatel tenant", Exact = true })).ToContainTextAsync("Fixture tenant");
            await page.GetByTestId("manual-helpdesk-resources").ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = "Fixture resource (e0497370-a6ab-45eb-a197-4bc7e290158f)", Exact = true }).ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            await page.GetByTestId("helpdesk-service-name").FillAsync("Fixture inbound service");
            await page.GetByTestId("helpdesk-peer-instance").FillAsync("rateldesk-fixture-instance");
            await page.GetByTestId("helpdesk-peer-tenant").FillAsync("2a2947d5-5792-4e8b-81aa-ecb244d8bc8b");
            await page.GetByRole(AriaRole.Checkbox, new() { Name = "I approve this exact peer, tenant and resource grant", Exact = true }).CheckAsync();
            await page.GetByTestId("helpdesk-create-service").ClickAsync();
            await page.GetByTestId("helpdesk-service-secret-reveal").WaitForAsync();
            var secret = await page.GetByLabel("New service client secret", new() { Exact = true }).InputValueAsync();
            Assert.False(string.IsNullOrWhiteSpace(secret));
            Assert.Equal(1, fixture.Data.CreateCount);
            Assert.NotNull(fixture.Data.LastCreate);
            Assert.Equal(1, fixture.Data.LastCreate.TenantId);
            Assert.Equal(new[] { "netratel.orchestration.read" }, fixture.Data.LastCreate.Scopes);
            var constraints = JsonSerializer.Deserialize<ServiceLinkResourceConstraints>(fixture.Data.LastCreate.ResourceConstraintsJson);
            Assert.NotNull(constraints);
            Assert.Equal("1", constraints.TenantId);
            Assert.Equal(new[] { "e0497370-a6ab-45eb-a197-4bc7e290158f" }, constraints.ResourceIds);
            Assert.Empty(constraints.RequestDefinitionIds);
            await page.GetByRole(AriaRole.Button, new() { Name = "I stored the service secret", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("New service client secret", new() { Exact = true })).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await page.GetByText("Service clients and rotation", new() { Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Refresh service clients", Exact = true }).ClickAsync();
            var deployment = page.GetByTestId("helpdesk-service-client-row").Filter(new() { HasText = "Deployment client · deployment-client" });
            await Assertions.Expect(deployment).ToHaveCountAsync(1);
            await Assertions.Expect(deployment).ToBeVisibleAsync();
            Assert.Equal(0, await deployment.GetByRole(AriaRole.Button).CountAsync());
            var lockedDeployment = page.GetByTestId("helpdesk-service-client-row").Filter(new() { HasText = "Fixture deployment client · active" });
            await Assertions.Expect(lockedDeployment).ToHaveCountAsync(1);
            await Assertions.Expect(lockedDeployment).ToBeVisibleAsync();
            Assert.Equal(0, await lockedDeployment.GetByRole(AriaRole.Button).CountAsync());
            await page.GetByRole(AriaRole.Button, new() { Name = "Rotate manual client", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm rotation", Exact = true }).ClickAsync();
            await page.GetByTestId("helpdesk-service-secret-reveal").WaitForAsync();
            Assert.NotEqual(secret, await page.GetByLabel("New service client secret", new() { Exact = true }).InputValueAsync());
            Assert.Equal(1, fixture.Data.RotateCount);
            await page.GetByRole(AriaRole.Button, new() { Name = "I stored the service secret", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Revoke manual client", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm revocation", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByText("Fixture inbound service · revoked", new() { Exact = true })).ToBeVisibleAsync();
            Assert.Equal(1, fixture.Data.RevokeCount);
            Assert.DoesNotContain(secret, await page.Locator("body").InnerTextAsync());
        }
        finally { await page.GotoAsync("about:blank"); }
    }

    [Fact]
    public async Task CallbackProofIsCleanedAndMissingAntiforgeryCreatesNoAttempt()
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var proof = "/account/integration-credentials/link/callback?attempt_id=fixture&pairing_code=not-a-valid-code&browser_state=not-a-valid-state&responder_instance_id=peer&oauth_issuer=https%3A%2F%2Fhelpdesk.example.test";
        var response = await page.GotoAsync(fixture.Address + proof);
        Assert.NotNull(response);
        Assert.DoesNotContain("pairing_code", page.Url);
        Assert.DoesNotContain("browser_state", page.Url);
        Assert.True(System.Net.Http.Headers.CacheControlHeaderValue.Parse(response.Headers["cache-control"]).NoStore);
        Assert.Equal("no-referrer", response.Headers["referrer-policy"]);
        var post = await context.APIRequest.PostAsync(fixture.Address + "/account/integration-credentials/link/start", new() { Data = "peerWebBaseUrl=https%3A%2F%2Fhelpdesk.example.test&localTenantId=1", Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" } });
        Assert.True(post.Ok);
        Assert.Contains("form-expired", post.Url);
        Assert.Equal(0, fixture.Data.StartCount);
        Assert.Equal(0, await page.EvaluateAsync<int>("() => Object.keys(localStorage).concat(Object.keys(sessionStorage)).filter(key => /pairing|verifier|browser.?state|client.?secret/i.test(key)).length"));
        await using var anonymous = await browserFixture.Browser.NewContextAsync(new() { ExtraHTTPHeaders = new Dictionary<string, string> { ["X-Helpdesk-Fixture-Anonymous"] = "1" } });
        var signedOut = await anonymous.NewPageAsync();
        var approval = await signedOut.GotoAsync(fixture.Address + "/account/integration-credentials/link/approve?initiator_web_base_url=https%3A%2F%2Fhelpdesk.example.test&attempt_id=fixture&browser_state=not-a-valid-state");
        Assert.NotNull(approval);
        Assert.DoesNotContain("browser_state", signedOut.Url);
        Assert.DoesNotContain("not-a-valid-state", signedOut.Url);
        Assert.True(System.Net.Http.Headers.CacheControlHeaderValue.Parse(approval.Headers["cache-control"]).NoStore);
    }

    [Theory]
    [InlineData(1366, 768, false)]
    [InlineData(1366, 768, true)]
    [InlineData(390, 844, false)]
    [InlineData(390, 844, true)]
    public async Task ActiveButUnavailableGrantIsNotConnectedAndTestUsesOnlyReadOnlyProbe(int width, int height, bool dark)
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        var descriptor = new ServiceLinkRequestDescriptor
        {
            InitiatorEndpointSnapshot = new() { Product = "netratel", InstanceId = "fixture-instance", WebBaseUrl = "https://web.example.test" },
            ResponderEndpointSnapshot = new() { Product = "rateldesk", InstanceId = "peer-instance", WebBaseUrl = "https://helpdesk.example.test" }
        };
        fixture.Data.Link = new("fixture-attempt", "fixture-link", 1, "active", "1", "peer-instance", "peer-organization", "commit", "fixture-commit", "fixture-grant-hash", descriptor, null, true, true, false, false, true, "grant-unavailable", false, []) { LocalTenantName = "Fixture tenant" };
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.Address + "/account/integration-credentials?theme=" + (dark ? "dark" : "light"));
        await Assertions.Expect(page.GetByText("Needs attention · grant unavailable", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.GetByTestId("helpdesk-test-connection").CountAsync());
        fixture.Data.Link = fixture.Data.Link with { LocalInboundActive = true, LocalBusinessSenderEnabled = true, LastErrorCode = null };
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Approved · verify connection", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Connected", new() { Exact = true })).ToHaveCountAsync(0);
        await CaptureConnectionAsync(page, $"netratel-pairing-approved-unverified-{width}-{(dark ? "dark" : "light")}.png");
        Assert.Equal(1, fixture.Data.CompleteCount);
        Assert.Equal("fixture-link", fixture.Data.LastCompletion?.LinkId);
        fixture.Data.ConnectionReady = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Connected", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Fixture organization / Fixture customer", new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Return to Flow", Exact = true })).ToBeVisibleAsync();
        await CaptureConnectionAsync(page, $"netratel-pairing-connected-{width}-{(dark ? "dark" : "light")}.png");
        await page.GetByTestId("helpdesk-test-connection").ClickAsync();
        await Assertions.Expect(page.GetByTestId("helpdesk-connection-test-result")).ToContainTextAsync("requires separate receiver capability and target validation");
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"));
        Assert.Equal(1, fixture.Data.TestCount);
        Assert.Equal(0, fixture.Data.CreateCount);
        Assert.Equal(0, fixture.Data.StartCount);
        Assert.Equal(3, fixture.Data.CompleteCount);
    }

    [Theory]
    [InlineData(1366, 768, false)]
    [InlineData(1366, 768, true)]
    [InlineData(390, 844, false)]
    [InlineData(390, 844, true)]
    public async Task Installation_enable_and_pending_failed_states_fit_the_viewport(int width, int height, bool dark)
    {
        await using var fixture = await HelpdeskM2MFixture.StartAsync();
        fixture.Data.ConnectionsDisabled = true;
        fixture.Data.Link = new("fixture-pending", null, 0, "awaiting_approval", "1", "fixture-peer", "fixture-organization",
            "undecided", null, null, new ServiceLinkRequestDescriptor
            {
                InitiatorEndpointSnapshot = new() { Product = "rateldesk", WebBaseUrl = "https://helpdesk.example.test" },
                ResponderEndpointSnapshot = new() { Product = "netratel", WebBaseUrl = "https://web.example.test" }
            }, null, false, false, false, false, false, null, false, [])
        { LocalRole = "responder", AvailableAction = "respond", CanCancel = true, LocalTenantName = "Fixture tenant" };
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.Address + "/account/integration-credentials?purpose=helpdesk-m2m&theme=" + (dark ? "dark" : "light"));
        var enable = page.GetByTestId("helpdesk-enable-connections");
        await enable.WaitForAsync();
        var button = await enable.BoundingBoxAsync();
        var parent = await enable.Locator("..").BoundingBoxAsync();
        Assert.NotNull(button); Assert.NotNull(parent);
        Assert.True(button.Width < parent.Width - 12, "The enable button should fit its text instead of stretching across the column.");
        Assert.InRange(button.Height, 20, 48);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"));
        await CaptureConnectionAsync(page, $"netratel-installation-enable-{width}-{(dark ? "dark" : "light")}.png");
        await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Resume local approval", Exact = true })).ToHaveAttributeAsync("href", "/account/integration-credentials/link/respond/fixture-pending");
        await CaptureConnectionAsync(page, $"netratel-pairing-pending-{width}-{(dark ? "dark" : "light")}.png");
        fixture.Data.Link = fixture.Data.Link with { LifecycleState = "failed", AvailableAction = "none", CanCancel = false, CanStartFresh = true, LastErrorCode = "invalid-organization" };
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Reconnect with new approval", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Resume local approval", Exact = true })).ToHaveCountAsync(0);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"));
        await CaptureConnectionAsync(page, $"netratel-pairing-failed-{width}-{(dark ? "dark" : "light")}.png");
    }

    private static async Task CaptureConnectionAsync(IPage page, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, filename), FullPage = true, Animations = ScreenshotAnimations.Disabled });
    }
}

internal sealed class HelpdeskM2MFixture(WebApplication app, string address, HelpdeskFixtureData data) : IAsyncDisposable
{
    public string Address { get; } = address;
    public HelpdeskFixtureData Data { get; } = data;
    public static async Task<HelpdeskM2MFixture> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Authentication:Local:AllowInsecureLocalhost"] = "true";
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddMudServices();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication("HelpdeskFixture").AddScheme<AuthenticationSchemeOptions, HelpdeskFixtureAuthentication>("HelpdeskFixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<HelpdeskFixtureData>();
        builder.Services.AddSingleton<IHttpClientFactory, HelpdeskFixtureClients>();
        builder.Services.AddScoped<HelpdeskM2MApiClient>();
        var app = builder.Build();
        app.MapGet("/_framework/blazor.web.js", () => Results.File(ResolveAsset("_framework/blazor.web.js"), "text/javascript"));
        app.MapGet("/_content/MudBlazor/{file}", (string file) => Results.File(ResolveAsset(file), file.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/javascript"));
        app.MapGet("/app-site.css", () => Results.File(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/app-site.css")), "text/css"));
        app.Use(async (context, next) => { if (context.Request.Path.StartsWithSegments("/account/integration-credentials")) ServiceLinkBrowserEndpoints.ProtectResponse(context); await next(); });
        app.MapGet("/fixture-peer-approval", () => Results.Content("Fixture peer sign-in", "text/html"));
        app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
        app.MapServiceLinkBrowserEndpoints();
        app.MapRazorComponents<HelpdeskFixtureApp>().AddAdditionalAssemblies(typeof(IntegrationCredentials).Assembly).AddInteractiveServerRenderMode();
        await app.StartAsync();
        var data = app.Services.GetRequiredService<HelpdeskFixtureData>();
        data.BrowserAddress = app.Urls.Single();
        return new HelpdeskM2MFixture(app, data.BrowserAddress, data);
    }
    private static string ResolveAsset(string name)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.staticwebassets.runtime.json")));
        foreach (var root in manifest.RootElement.GetProperty("ContentRoots").EnumerateArray())
        {
            var path = root.GetString();
            if (path is null) continue;
            foreach (var relative in new[] { name, name.Replace("_framework/", "", StringComparison.Ordinal) })
            { var candidate = Path.Combine(path, relative); if (File.Exists(candidate)) return candidate; }
        }
        throw new FileNotFoundException("The browser fixture's static asset was not produced.", name);
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

public sealed class HelpdeskFixtureApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "html"); b.OpenElement(1, "head");
        b.AddMarkupContent(2, "<base href='/'/><meta name='viewport' content='width=device-width,initial-scale=1'/><link rel='stylesheet' href='/_content/MudBlazor/MudBlazor.min.css'/><link rel='stylesheet' href='/app-site.css'/><style>body{margin:0;min-width:0;background:var(--mud-palette-background);color:var(--mud-palette-text-primary);font-family:system-ui,sans-serif}</style>");
        b.CloseElement(); b.OpenElement(3, "body"); b.OpenComponent<HelpdeskFixtureSurface>(4); b.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false)); b.CloseComponent();
        b.AddMarkupContent(5, "<script src='/_framework/blazor.web.js'></script><script src='/_content/MudBlazor/MudBlazor.min.js'></script>");
        b.CloseElement(); b.CloseElement();
    }
}
public sealed class HelpdeskFixtureSurface : ComponentBase
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenComponent<MudThemeProvider>(0); b.AddComponentParameter(1, nameof(MudThemeProvider.Theme), new NetRatelTheme()); b.AddComponentParameter(2, nameof(MudThemeProvider.IsDarkMode), Navigation.Uri.Contains("theme=dark", StringComparison.Ordinal)); b.CloseComponent();
        b.OpenComponent<MudPopoverProvider>(3); b.CloseComponent(); b.OpenComponent<MudDialogProvider>(4); b.CloseComponent(); b.OpenComponent<MudSnackbarProvider>(5); b.CloseComponent();
        b.OpenComponent<Router>(6); b.AddComponentParameter(7, nameof(Router.AppAssembly), typeof(IntegrationCredentials).Assembly);
        b.AddComponentParameter(8, nameof(Router.Found), (RenderFragment<RouteData>)(route => child => { child.OpenComponent<RouteView>(0); child.AddComponentParameter(1, nameof(RouteView.RouteData), route); child.CloseComponent(); })); b.CloseComponent();
    }
}
internal sealed class HelpdeskFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers["X-Helpdesk-Fixture-Anonymous"] == "1"
        ? AuthenticateResult.NoResult()
        : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-actor"), new Claim(ClaimTypes.Name, "fixture-actor")], "HelpdeskFixture")), "HelpdeskFixture")));
}
internal sealed class HelpdeskFixtureData
{
    public int CreateCount, RotateCount, RevokeCount, StartCount, TestCount, CompleteCount;
    public bool WithOrchestrationResources, ConnectionReady, AcceptStart, ConnectionsDisabled;
    public ServiceLinkStartRequest? LastStart;
    public string BrowserAddress = "";
    public CompleteRatelDeskConnectionRequest? LastCompletion;
    public ServiceLinkAdminStatus? Link;
    public ServiceClientCreateRequest? LastCreate;
    public ServiceClientMetadata? Manual;
    public ServiceClientMetadata Deployment { get; } = new(Guid.Parse("9a3dbde9-cc02-46e2-bb21-0b19224cbdaa"), "Fixture deployment client", "deployment-client", 1, "rateldesk-fixture-instance", "fixture-organization", ["netratel.orchestration.read"], "{}", "active", "deployment", true, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
}
internal sealed class HelpdeskFixtureClients(HelpdeskFixtureData data) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new Handler(data)) { BaseAddress = new Uri("http://fixture.example.test") };
    private sealed class Handler(HelpdeskFixtureData data) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            if (path == "/api/v2/account/integration-credentials") return Json(Array.Empty<object>());
            if (path == "/api/v2/account/integration-credentials/authority") return Json(new { tenants = new[] { new { tenantId = 1, name = "Fixture tenant", permissions = new[] { new { id = "telemetry.read", label = "Read telemetry", description = "Read this tenant" } } } }, instancePermissions = Array.Empty<object>(), configuredHttpMcpServerUrl = "https://mcp.example.test/mcp" });
            if (path == "/api/v2/account/service-clients/deployment") return Json(new[] { new ServiceClientDeploymentMetadata("deployment-client", "fixture-audience", ["netratel.orchestration.read"]) });
            if (path == "/api/v2/account/service-clients/settings") return Json(new ServicePublicSettingsResponse(!data.ConnectionsDisabled, "https://web.example.test", "https://api.example.test", "https://issuer.example.test", "fixture-audience", "fixture-instance", "https://gateway.example.test", 1, ["issuer"], !data.ConnectionsDisabled));
            if (path == "/api/v2/account/service-clients/authority") return Json(new ServiceClientManagementAuthority(true, [new(1, "Fixture tenant", ["netratel.orchestration.read", "netratel.orchestration.invoke"], data.WithOrchestrationResources ? [new("e0497370-a6ab-45eb-a197-4bc7e290158f", "Fixture resource")] : [], data.WithOrchestrationResources ? [new("9", "Fixture request definition", "e0497370-a6ab-45eb-a197-4bc7e290158f")] : [])], true));
            if (path == "/api/v2/tenants/1/connectors/rateldesk/setup/complete")
            {
                data.CompleteCount++;
                data.LastCompletion = await request.Content!.ReadFromJsonAsync<CompleteRatelDeskConnectionRequest>(ct);
                return Json(new RatelDeskConnectionCompletionDto(Guid.Parse("2e9f48c4-e2b5-41cb-81ce-e8d1a6d030e7"), "Fixture helpdesk", data.ConnectionReady, data.ConnectionReady ? "ready" : "receiver-unavailable")
                { OrganizationName = "Fixture organization", CustomerName = "Fixture customer" });
            }
            if (path == "/api/v1/admin/service-links") return Json(data.Link is null ? Array.Empty<ServiceLinkAdminStatus>() : new[] { data.Link });
            if (path == "/api/v1/admin/service-links/links/fixture-link/test") { data.TestCount++; return Json(new ServiceLinkTestResult(true, true, false, null)); }
            if (path == "/api/v1/admin/service-links/identity") return Json(new ServiceLinkIdentityDto("fixture-instance", "71376348-1f0a-4887-9cbe-031d5f879fb1", 1));
            if (path == "/api/v1/admin/service-links/start")
            {
                data.StartCount++;
                data.LastStart = await request.Content!.ReadFromJsonAsync<ServiceLinkStartRequest>(ct);
                return data.AcceptStart
                    ? Json(new ServiceLinkNavigation("fixture-submission", data.BrowserAddress + "/fixture-peer-approval?attempt_id=fixture-submission", "awaiting_approval"))
                    : Json(new { code = "upgrade-required", stage = "start", correlationId = "0123456789abcdef0123456789abcdef" }, HttpStatusCode.BadRequest);
            }
            if (path == "/api/v1/admin/service-links/attempts/fixture-submission")
                return Json(new ServiceLinkAdminStatus("fixture-submission", null, 0, "awaiting_approval", "1", "fixture-peer", data.LastStart?.RequestedResponderTenantId,
                    "undecided", null, null, new ServiceLinkRequestDescriptor
                    {
                        InitiatorEndpointSnapshot = new() { Product = "netratel", WebBaseUrl = "https://web.example.test" },
                        ResponderEndpointSnapshot = new() { Product = "rateldesk", WebBaseUrl = "https://helpdesk.example.test", ApprovalEndpoint = data.BrowserAddress + "/fixture-peer-approval" }
                    }, null, false, false, false, false, false, null, false, []) { LocalRole = "initiator", AvailableAction = "continue", CanCancel = true });
            if (path == "/api/v2/account/service-clients")
            {
                if (request.Method == HttpMethod.Get) return Json(data.Manual is { } manual ? new[] { data.Deployment, manual } : new[] { data.Deployment });
                data.CreateCount++;
                data.LastCreate = await request.Content!.ReadFromJsonAsync<ServiceClientCreateRequest>(ct);
                var input = data.LastCreate!;
                data.Manual = new(Guid.NewGuid(), input.Name, "fixture-managed-client", input.TenantId, input.PeerInstanceId, input.PeerTenantId, input.Scopes, input.ResourceConstraintsJson, "active", "database", false, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
                return Reveal(data.Manual, "fixture-only-create-secret");
            }
            if (data.Manual is { } client && path == $"/api/v2/account/service-clients/{client.Id:D}/rotate")
            { data.RotateCount++; data.Manual = client with { CredentialRevision = 2, Revision = 2 }; return Reveal(data.Manual, "fixture-only-successor-secret"); }
            if (data.Manual is { } revoking && path == $"/api/v2/account/service-clients/{revoking.Id:D}/revoke")
            { data.RevokeCount++; data.Manual = revoking with { Status = "revoked", Revision = 3 }; return new(HttpStatusCode.NoContent); }
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Reveal(ServiceClientMetadata client, string secret) => Json(new ServiceClientReveal(client, secret, "https://issuer.example.test", "https://api.example.test/connect/token", "fixture-audience", client.Scopes), HttpStatusCode.Created);
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
    }
}
