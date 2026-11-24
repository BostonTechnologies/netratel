using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Web.Services.Tenants;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class TenantSecretVaultBrowserTests(ClientsManagementBrowserFixture browser)
    : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _host;

    [Theory]
    [Trait("Category", "ManualBrowserAcceptance")]
    [InlineData(1440, 900)]
    [InlineData(1366, 768)]
    [InlineData(390, 844)]
    [InlineData(320, 568)]
    public async Task Vault_browser_and_dialog_actions_reflow_in_both_themes(int width, int height)
    {
        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light";
            await using var context = await browser.Browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = width, Height = height },
                ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light,
                HasTouch = width < 767
            });
            await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}')");
            var page = await context.NewPageAsync();
            await page.GotoAsync(_host!.BaseAddress + "/tenants");
            await Assertions.Expect(page.Locator($"html[data-netratel-theme={theme}]")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Manage Visual fixture", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).ClickAsync();
            var tenant = page.GetByRole(AriaRole.Dialog);
            var sections = tenant.GetByLabel("Tenant section", new() { Exact = true });
            await sections.WaitForAsync(new() { State = WaitForSelectorState.Attached });
            if (await sections.IsVisibleAsync())
                await sections.SelectOptionAsync(new SelectOptionValue { Label = "Secrets & credentials" });
            else
                await tenant.GetByRole(AriaRole.Tab, new() { Name = "Secrets & credentials", Exact = true }).ClickAsync();
            await Assertions.Expect(tenant.GetByText(VaultFixtureFactory.LongSecret, new() { Exact = true })).ToBeVisibleAsync();
            await AssertFitsAsync(page, tenant, width);

            var screenshots = Path.Combine(Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "netratel-playwright"), "tenant-secret-vault");
            Directory.CreateDirectory(screenshots);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"vault-{width}-{theme}.png") });

            var test = tenant.GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true });
            await test.PressAsync("Enter");
            await Assertions.Expect(tenant.GetByText("Authentication failed", new() { Exact = false })).ToBeVisibleAsync();
            var create = tenant.GetByRole(AriaRole.Button, new() { Name = "Create secret", Exact = true });
            await create.PressAsync("Enter");
            var editor = page.GetByRole(AriaRole.Dialog).Last;
            var submit = editor.GetByRole(AriaRole.Button, new() { Name = "Save secret", Exact = true });
            await submit.PressAsync("Enter");
            await Assertions.Expect(editor.GetByRole(AriaRole.Alert)).ToContainTextAsync("Enter a secret name.");
            var name = editor.GetByLabel("Secret name", new() { Exact = true });
            await name.FocusAsync();
            await Assertions.Expect(name).ToBeFocusedAsync();
            if (width < 767) await page.SetViewportSizeAsync(width, height - 210);
            await AssertFitsAsync(page, submit, width);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"vault-entry-{width}-{theme}.png") });
            await editor.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
            await page.SetViewportSizeAsync(width, height);

            await tenant.GetByRole(AriaRole.Button, new() { Name = "Credentials", Exact = true }).PressAsync("Enter");
            await tenant.GetByRole(AriaRole.Button, new() { Name = "Add credential profile", Exact = true }).PressAsync("Enter");
            var profile = page.GetByRole(AriaRole.Dialog).Last;
            await profile.GetByRole(AriaRole.Button, new() { Name = "Choose password reference", Exact = true }).PressAsync("Enter");
            var picker = page.GetByRole(AriaRole.Dialog).Last;
            await Assertions.Expect(picker.GetByText(VaultFixtureFactory.LongSecret, new() { Exact = true })).ToBeVisibleAsync();
            var select = picker.GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true });
            await AssertFitsAsync(page, select, width);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"vault-picker-{width}-{theme}.png") });
            await select.PressAsync("Enter");
            var referenceDialog = page.GetByRole(AriaRole.Dialog).Last;
            await referenceDialog.GetByRole(AriaRole.Button, new() { Name = "Use reference", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(2);
            await Assertions.Expect(profile.GetByText("22 · Latest at execution", new() { Exact = true })).ToBeVisibleAsync();
            await profile.GetByLabel("Display name", new() { Exact = true }).FillAsync("Test Windows credential");
            await profile.GetByLabel("Public username", new() { Exact = true }).FillAsync("test-operator");
            var nodes = profile.GetByTestId("job-node-picker");
            await nodes.GetByRole(AriaRole.Button, new() { Name = "View and edit nodes", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(nodes.GetByLabel("Select test-linux", new() { Exact = true })).ToBeVisibleAsync();
            await nodes.GetByLabel("Search nodes", new() { Exact = true }).FillAsync("test-windows-a");
            await Assertions.Expect(nodes.GetByLabel("Select test-linux", new() { Exact = true })).ToHaveCountAsync(0);
            await nodes.GetByLabel("Select test-windows-a", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(nodes.GetByText("Allowed clients (1)", new() { Exact = true })).ToBeVisibleAsync();
            await profile.GetByRole(AriaRole.Button, new() { Name = "Select all tenant clients", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(nodes.GetByText("Allowed clients (3)", new() { Exact = true })).ToBeVisibleAsync();
            await profile.GetByRole(AriaRole.Button, new() { Name = "Select all Windows clients", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(nodes.GetByText("Allowed clients (2)", new() { Exact = true })).ToBeVisibleAsync();
            var saveProfile = profile.GetByRole(AriaRole.Button, new() { Name = "Save credential profile", Exact = true });
            await Assertions.Expect(profile.GetByText("The client resolves the account when it starts a job or terminal.", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(profile.GetByLabel("Approved Windows account SID (optional)")).ToHaveCountAsync(0);
            await AssertFitsAsync(page, saveProfile, width);
            await page.ScreenshotAsync(new() { Path = Path.Combine(screenshots, $"credential-clients-{width}-{theme}.png") });
            await saveProfile.PressAsync("Enter");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
            await Assertions.Expect(tenant.GetByText("Test Windows credential", new() { Exact = true })).ToBeVisibleAsync();
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        }
    }

    private static async Task AssertFitsAsync(IPage page, ILocator control, int width)
    {
        await control.ScrollIntoViewIfNeededAsync();
        var bounds = await control.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.True(bounds.X >= -1 && bounds.X + bounds.Width <= width + 1);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"));
    }

    public async ValueTask InitializeAsync() => _host = await ClientsManagementFixtureHost.StartAsync(services =>
    {
        services.AddScoped<SecretVaultApi>();
        services.AddSingleton<IHttpClientFactory, VaultFixtureFactory>();
    });

    public async ValueTask DisposeAsync() { if (_host is not null) await _host.DisposeAsync(); }

    private sealed class VaultFixtureFactory(ClientDirectoryFixtureData directory) : IHttpClientFactory
    {
        public const string LongSecret = "Canary_ExtremelyLongSecretMetadataEntryName_ForResponsiveKeyboardAndWrappingVerification_Example_0123456789";
        private static readonly Guid ConnectionId = Guid.Parse("b59b9b79-2e0c-4d0b-950c-000000000007");
        private static readonly SecretEntryMetadata Entry = new("22", LongSecret, false, "1", "1", Enabled: true);
        private static readonly JobNodeInventoryDto[] Nodes =
        [
            Node("test-windows-a", "Windows", "10000000-0000-4000-8000-000000000071"),
            Node("test-windows-b", "Windows", "10000000-0000-4000-8000-000000000072"),
            Node("test-linux", "Linux", "10000000-0000-4000-8000-000000000073")
        ];
        private CredentialProfile? _profile;

        public HttpClient CreateClient(string name) => new(new FixtureHandler(directory, this)) { BaseAddress = new Uri("https://netratel.test") };

        private static JobNodeInventoryDto Node(string hostname, string os, string id) =>
            new(Guid.Parse(id), hostname, hostname, null, [], os, "x64", null, true, true, null);

        private sealed class FixtureHandler(ClientDirectoryFixtureData directory, VaultFixtureFactory fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                const string root = "/api/v1/tenants/7/secret-vault";
                if (!path.StartsWith(root + "/", StringComparison.Ordinal)) return directory.Respond(request);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                object data = path[root.Length..] switch
                {
                    "/permissions" => new SecretVaultPermissions(true, true, true, true, true, true, true),
                    "/connections" => new[] { new SecretProviderConnection(ConnectionId, SecretProviderKind.Local, "Local", 1, 1, true, true,
                        null, false, new(SecretWriteConcurrency.CompareAndSwap, SecretDeletionMode.RemoveEntry, false), null) },
                    "/profiles" when request.Method == HttpMethod.Post => await SaveProfileAsync(request, cancellationToken),
                    "/profiles" => fixture._profile is { } saved ? new[] { saved } : Array.Empty<CredentialProfile>(),
                    "/profile-targets" => SearchNodes(query["search"].ToString()),
                    "/profile-targets/all" => Nodes.Where(node => !bool.TryParse(query["windowsOnly"], out var windowsOnly) ||
                        !windowsOnly || node.OperatingSystem == "Windows").Select(node => node.AgentId).ToArray(),
                    var route when route.EndsWith("/entries", StringComparison.Ordinal) => new SecretBrowsePage([Entry], null, false, null),
                    var route when route.EndsWith("/entry", StringComparison.Ordinal) => new SecretEntryDetails(Entry, [], ["1"], false, []),
                    var route when route.EndsWith("/test", StringComparison.Ordinal) => new SecretProviderTestResult(DateTimeOffset.UtcNow, false, "Authentication"),
                    _ => throw new InvalidOperationException("Unexpected vault fixture route.")
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) };
            }

            private static JobNodePageDto SearchNodes(string search)
            {
                var nodes = Nodes.Where(node => node.Hostname.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new(nodes, 1, 25, nodes.Length, null);
            }

            private async Task<CredentialProfile> SaveProfileAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var saved = (await request.Content!.ReadFromJsonAsync<SaveCredentialProfileRequest>(ct))!;
                Assert.Null(saved.Scope.AccountId);
                Assert.Equal(Nodes.Where(node => node.OperatingSystem == "Windows").Select(node => node.AgentId).Order(),
                    saved.Scope.AgentIds.Order());
                return fixture._profile = new(Guid.NewGuid(), saved.DisplayName, 1, 1, saved.Mode, saved.PublicUsername,
                    saved.UsernameReference, saved.Domain, saved.PasswordReference, saved.Scope);
            }
        }
    }
}
