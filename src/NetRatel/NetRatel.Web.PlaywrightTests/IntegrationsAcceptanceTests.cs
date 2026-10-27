using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Web.Services.Pairing;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class IntegrationsAcceptanceTests(ClientsManagementBrowserFixture browser) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768, "light", 1d)]
    [InlineData(1366, 768, "dark", 1d)]
    [InlineData(1600, 900, "light", 1d)]
    [InlineData(390, 844, "dark", 1d)]
    [InlineData(683, 384, "light", 2d)]
    public async Task Approvals_grouped_access_credentials_and_system_connections_are_operable(int width, int height, string theme, double scale)
    {
        using var api = new IntegrationBrowserApi();
        await using var host = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            services.AddSingleton<IHttpClientFactory>(api);
            services.AddSingleton<IPairingApiClient>(new SystemConnections());
        });
        await using var context = await browser.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)scale });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/account/integration-credentials?tab=credentials&host_code=ABCD-EFGH", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("integration-credentials-client-ready").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync();
        var drawer = page.GetByTestId("app-navigation-drawer");
        var closed = await drawer.EvaluateAsync<bool>("e=>e.classList.contains('mud-drawer--closed')");
        if (width >= 1280 && closed) await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width < 1280)
        {
            var scrim = page.Locator(".mud-drawer-overlay--open .mud-overlay-scrim");
            if (await scrim.CountAsync() > 0) await scrim.ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2f } });
            await Assertions.Expect(drawer).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-drawer--closed"));
        }
        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(24);
        if (width >= 1280)
        {
            var first = (await page.GetByTestId("host-setup-panel").BoundingBoxAsync())!;
            var second = (await page.GetByTestId("integration-pairing-panel").BoundingBoxAsync())!;
            Assert.InRange(Math.Abs(first.Y - second.Y), 0, 3);
            Assert.True(second.X >= first.X + first.Width - 1);
            Assert.True((await page.GetByTestId("credential-search").BoundingBoxAsync())!.Y < height);
        }
        await Capture("credentials");
        await page.GetByTestId("host-setup-lookup").ClickAsync();
        await page.GetByTestId("host-review-dialog").WaitForAsync();
        await Reachable(page.GetByTestId("host-setup-approve"));
        await Capture("host-review");
        await page.GetByTestId("host-review-dialog").GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true }).ClickAsync();
        await page.GetByTestId("pairing-code").FillAsync("JKLM-NPQR");
        await page.GetByTestId("pairing-lookup").ClickAsync();
        await page.GetByTestId("client-review-dialog").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("permission-scope")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".permission-row")).ToHaveCountAsync(IntegrationBrowserApi.TenantPermissions.Length);
        await Reachable(page.GetByTestId("pairing-approve"));
        await Capture("grouped-tenant-access");
        var lastPermission = page.Locator(".permission-row").Last.GetByRole(AriaRole.Checkbox);
        await lastPermission.CheckAsync();
        await Assertions.Expect(lastPermission).ToBeCheckedAsync();
        await Assertions.Expect(page.Locator(".permission-current")).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(page.GetByTestId("pairing-approve")).ToBeInViewportAsync(new() { Ratio = 1 });
        await lastPermission.UncheckAsync();
        await Capture("grouped-tenant-access-scrolled");
        await page.GetByTestId("permission-scope").SelectOptionAsync("server");
        await page.GetByTestId("permission-select-scope").ClickAsync();
        await Assertions.Expect(page.GetByTestId("pairing-approve")).ToBeDisabledAsync();
        await Reachable(page.GetByTestId("pairing-approve"));
        await Capture("grouped-access");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("client-review-dialog")).ToHaveCountAsync(0);
        await page.GetByTestId("open-create-integration").ClickAsync();
        await page.GetByTestId("credential-name").FillAsync("Synthetic workflow client");
        await page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await page.GetByTestId("permission-scope").WaitForAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Add tenant", Exact = true }).ClickAsync();
        await page.Locator(".mud-popover-open").GetByText("Synthetic operations", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add tenant", Exact = true }).ClickAsync();
        await page.GetByTestId("permission-scope").SelectOptionAsync("7");
        await Assertions.Expect(page.Locator(".permission-row")).ToHaveCountAsync(IntegrationBrowserApi.TenantPermissions.Length);
        await Capture("create-access");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await page.GetByTestId("credential-filter-revoked").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(8);
        await page.GetByTestId("select-inactive-credential-03").CheckAsync();
        await page.GetByTestId("delete-selected-inactive").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(7);
        Assert.Equal(new[] { "credential-03" }, api.Deleted);
        await page.GotoAsync(host.BaseAddress + "/account/integration-credentials?tenantId=7", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("connection-card")).ToHaveCountAsync(4);
        await Assertions.Expect(page.GetByTestId("pairing-final-form")).ToHaveCountAsync(0);
        await Capture("system-connections");
        await page.GetByTestId("connection-view").First.ClickAsync();
        await page.GetByTestId("pairing-final-form").WaitForAsync();
        await Reachable(page.GetByTestId("pairing-save"));
        await Capture("system-configuration");
        Assert.Empty(errors);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());

        async Task Reachable(ILocator control)
        {
            await control.ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(control).ToBeVisibleAsync();
            Assert.True(await control.EvaluateAsync<bool>("e=>{const r=e.getBoundingClientRect();return r.left>=-1&&r.right<=innerWidth+1&&r.top>=-1&&r.bottom<=innerHeight+1}"));
        }
        async Task Capture(string name)
        {
            Assert.False(await page.EvaluateAsync<bool>("()=>document.documentElement.scrollWidth>innerWidth+1"));
            var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
            Directory.CreateDirectory(root);
            await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"integrations-{name}-{width}x{height}-{theme}-dpr{scale}.png"), FullPage = false, Animations = ScreenshotAnimations.Disabled });
        }
    }

    [Theory]
    [InlineData(1366, 768, "light")]
    [InlineData(390, 844, "dark")]
    public async Task Credential_details_dialog_keeps_metadata_and_admission_reachable_and_preserves_search(int width, int height, string theme)
    {
        using var api = new IntegrationBrowserApi();
        await using var host = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            services.AddSingleton<IHttpClientFactory>(api);
            services.AddSingleton<IPairingApiClient>(new SystemConnections());
        });
        await using var context = await browser.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height } });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/account/integration-credentials?tab=credentials", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("integration-credentials-client-ready").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var drawer = page.GetByTestId("app-navigation-drawer");
        if (width < 1280 && !await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')"))
            await page.GetByTestId("navigation-toggle").ClickAsync();
        var search = page.GetByTestId("credential-search");
        await search.FillAsync("Synthetic client 01");
        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(1);
        await page.GetByTestId("credential-row").GetByRole(AriaRole.Button, new() { Name = "Details", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Credential details", Exact = true });
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByTestId("credential-admission")).ToContainTextAsync("Authorization admitted");
        await Assertions.Expect(dialog).ToContainTextAsync("HTTP MCP server");
        await Assertions.Expect(dialog).ToContainTextAsync("Exact expiry (UTC)");
        Assert.True(await dialog.EvaluateAsync<bool>("e => e.contains(document.activeElement)"));
        var bounds = (await dialog.BoundingBoxAsync())!;
        Assert.InRange(bounds.X, 0, width); Assert.True(bounds.X + bounds.Width <= width + 1);
        Assert.InRange(bounds.Y, 0, height); Assert.True(bounds.Y + bounds.Height <= height + 1);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"credential-details-{width}x{height}-{theme}.png"), Animations = ScreenshotAnimations.Disabled });
        await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("netratel_flows") }).ClickAsync();
        await Assertions.Expect(dialog.GetByText("Required delegation", new() { Exact = true })).ToBeVisibleAsync();
        await dialog.GetByText("Required delegation", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"credential-admission-{width}x{height}-{theme}.png"), Animations = ScreenshotAnimations.Disabled });
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(search).ToHaveValueAsync("Synthetic client 01");
        await Assertions.Expect(page.Locator(".credential-toolbar")).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(1);
        Assert.Empty(api.Deleted); Assert.Empty(errors);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private sealed class IntegrationBrowserApi : HttpMessageHandler, IHttpClientFactory
    {
        private static readonly DateTimeOffset CredentialExpiry = DateTimeOffset.Parse("2030-04-01T10:11:12Z");
        private static readonly Guid PolicyId = Guid.Parse("be1c136e-1cd2-44d8-8043-d047d16983ad");
        public List<string> Deleted { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://fixture.example.test") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
            object response;
            if (request.Method == HttpMethod.Delete)
            { Deleted.Add(path.Split('/').Last()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            if (path.EndsWith("/admission")) response = new CredentialMcpAdmission("credential-01", DateTimeOffset.UtcNow, "active",
                "current_credential_valid", "https://fixture.example.test/mcp", "fixture", "Production", CredentialExpiry,
                [new(7, "flow.read")], ["mcp.discovery.read"],
                [new(new(7, "netratel_flows", "list"), "netratel.mcp.observe", ["Operator"], true, true, true, null, [PolicyId])
                    { OwnerPermissionGranted = true, AuthorizationAdmitted = true }], [], "Review the intended operation before dispatch.")
                { MatchingPolicies = [new(PolicyId, "Production", 7, null, "netratel_flows/list", "Allow", CredentialExpiry, CredentialExpiry, 1)] };
            else if (path == "/api/v2/access/self") response = new { isInstanceAdministrator = true };
            else if (path.EndsWith("/authority")) response = new
            {
                tenants = new[] { new { tenantId = 7, name = "Synthetic operations", permissions = TenantPermissions } },
                instancePermissions = ServerPermissions,
                configuredHttpMcpServerUrl = "https://fixture.example.test/mcp"
            };
            else if (path.EndsWith("/http-host-setup/lookup")) response = new HttpHostSetupReview("host-fixture", "ABCD-EFGH", "https://fixture.example.test", "https://fixture.example.test/mcp", "netratel-mcp-http", new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(10), "pending");
            else if (path.EndsWith("/pairing/lookup")) response = new IntegrationPairingReview("client-fixture", "JKLM-NPQR", "Synthetic CLI client", PairingCredentialPurpose.Api, "https://fixture.example.test", null, [], DateTimeOffset.UtcNow.AddMinutes(10), "pending");
            else if (path.EndsWith("/integration-credentials")) response = Enumerable.Range(1, 24).Where(i => !Deleted.Contains($"credential-{i:00}")).Select(i => new
            {
                id = $"credential-{i:00}", publicId = $"public-{i:00}", tokenPrefix = "synthetic", name = $"Synthetic client {i:00}", purpose = i % 2,
                resource = i % 2 == 1 ? "https://fixture.example.test/mcp" : null,
                createdAtUtc = DateTimeOffset.UtcNow.AddDays(-1), expiresAtUtc = i == 1 ? CredentialExpiry : DateTimeOffset.UtcNow.AddDays(i % 3 == 2 ? -1 : 30),
                revokedAtUtc = i % 3 == 0 ? DateTimeOffset.UtcNow.AddHours(-1) : (DateTimeOffset?)null,
                lastUsedAtUtc = (DateTimeOffset?)null, grants = i == 1 ? new[] { new IntegrationPairingGrant(7, "flow.read") } : Array.Empty<IntegrationPairingGrant>(), instancePermissions = new[] { "mcp.discovery.read" }
            }).ToArray();
            else response = new { };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, response.GetType()) });
        }
        internal static readonly IntegrationPermissionDescriptor[] TenantPermissions = IntegrationPermissionCatalog.Delegable
            .Where(permission => permission.Id is not (NetRatelPermissions.ArtifactPublication or NetRatelPermissions.UserRoleAdministration or NetRatelPermissions.McpPolicyAdministration)).ToArray();
        private static readonly IntegrationPermissionDescriptor[] ServerPermissions = IntegrationPermissionCatalog.Delegable
            .Where(permission => permission.Id is NetRatelPermissions.McpDiscoveryRead or NetRatelPermissions.McpPolicyAdministration
                or NetRatelPermissions.AuditRead or NetRatelPermissions.ArtifactPublication or NetRatelPermissions.UserRoleAdministration).ToArray();
    }
    private sealed class SystemConnections : IPairingApiClient
    {
        private static readonly PairingMetadata Peer = new(PairingProtocol.Contract, "rateldesk", "fixture-peer", "Synthetic support", "https://desk.example.test", "https://api.desk.example.test", null);
        public Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default) => Task.FromResult(Enumerable.Range(1, 4).Select(i => new PairingConnectionDto($"pair-{i}", $"pair-{i}", null, Peer with { Name = $"Synthetic support {i}" }, "paired")).ToArray());
        public Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default) => Task.FromResult(new PairingDirectories([new("7", "Synthetic operations")], [new("org", "Synthetic support")], [new("customer", "Example customer", "org")]));
        public Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default) => Task.FromResult(new PairingCodeResponse("ABCD-EFGH", DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default) => Task.CompletedTask;
    }
}
