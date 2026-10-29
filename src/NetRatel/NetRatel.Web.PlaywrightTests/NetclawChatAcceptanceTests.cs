using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Services;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "ManualAcceptance")]
public sealed class NetclawChatAcceptanceTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 900, "light")]
    [InlineData(390, 844, "dark")]
    public async Task Starter_keyboard_send_and_tenant_switch_work_in_the_rendered_chat_workspace(int width, int height, string theme)
    {
        var api = new ChatFixture();
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<INetclawApiService>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height }, TimezoneId = "Africa/Johannesburg"
        });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/ai/chat?tenantId=17");
        var composer = page.GetByTestId("chat-message");
        await Assertions.Expect(composer).ToBeEnabledAsync();
        await page.Locator("html[data-netratel-theme]").WaitForAsync();
        if (width < 1280)
        {
            var scrim = page.Locator(".mud-drawer-overlay--open .mud-overlay-scrim");
            if (await scrim.CountAsync() > 0 && await scrim.IsVisibleAsync())
                await scrim.ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2f } });
            await Assertions.Expect(page.GetByTestId("app-navigation-drawer")).ToHaveClassAsync(new Regex("mud-drawer--closed"));
        }
        else if (await page.GetByTestId("app-navigation-drawer").EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')"))
            await page.GetByTestId("navigation-toggle").ClickAsync();

        var mobileMenu = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        await page.GetByTestId(mobileMenu ? "mobile-overflow" : "theme-preference-menu").ClickAsync();
        await page.GetByTestId($"{(mobileMenu ? "mobile-theme-option" : "theme-option")}-{theme}").ClickAsync();
        await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "What would you like to work on?", Exact = true })).ToBeVisibleAsync();
        await AssertHealthyAsync(page);
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"chat-welcome-{theme}-{width}x{height}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });

        await page.GetByRole(AriaRole.Button, new() { Name = "Create a script", Exact = false }).ClickAsync();
        const string starter = "Help me create a script. Ask me what it should do and which environment it will run in.";
        await Assertions.Expect(composer).ToHaveValueAsync(starter);
        Assert.Equal(0, api.Creates);
        Assert.Equal(0, api.Sends);
        await composer.PressAsync("Control+Enter");
        await Assertions.Expect(page.Locator(".chat-message-operator")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".chat-message-operator")).ToContainTextAsync(starter);
        await Assertions.Expect(page.Locator(".chat-message-assistant")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".chat-code-block pre code")).ToHaveTextAsync("Get-Service | Select-Object Name, Status");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Copy code", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(composer).ToHaveValueAsync("");
        Assert.Equal(1, api.Creates);
        Assert.Equal(1, api.Sends);
        await AssertHealthyAsync(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"chat-transcript-{theme}-{width}x{height}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });

        await page.GetByTestId("chat-tenant").SelectOptionAsync("18");
        var setup = page.GetByRole(AriaRole.Link, new() { Name = "Set up Netclaw", Exact = false });
        await Assertions.Expect(setup).ToBeVisibleAsync();
        await Assertions.Expect(setup).ToHaveAttributeAsync("href", "/account/integration-credentials?tab=netclaw&tenantId=18");
        await Assertions.Expect(page.Locator(".chat-message")).ToHaveCountAsync(0);
        await Assertions.Expect(composer).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Saved conversations", Exact = true }).GetByRole(AriaRole.Link)).ToHaveCountAsync(0);
        Assert.Equal(1, api.Sends);
        await AssertHealthyAsync(page);
        Assert.Empty(errors);
    }

    private static async Task AssertHealthyAsync(IPage page)
    {
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private sealed class ChatFixture : INetclawApiService
    {
        private readonly Guid _id = Guid.NewGuid();
        private volatile InvestigationSnapshotDto? _saved;
        private int _creates, _sends;
        public int Creates => Volatile.Read(ref _creates);
        public int Sends => Volatile.Read(ref _sends);
        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(17, "Operations", true, true), new(18, "Service desk", true, true)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(
            new NetclawConnectionDto(_id, tenantId, 1, tenantId == 17, "https://netclaw.example.test", tenantId == 17, DateTimeOffset.UtcNow, true, null));
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListChatsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>(tenantId == 17 && _saved is { } saved ? [saved.Investigation] : []);
        public Task<InvestigationSnapshotDto> CreateChatAsync(int tenantId, GeneralChatCreateRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _creates);
            var created = new InvestigationSnapshotDto(new(_id, tenantId, request.Title, InvestigationState.Idle, [], null, null, DateTimeOffset.UtcNow, "operator"), 0, null, [], [], []);
            _saved = created;
            return Task.FromResult(created);
        }
        public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default)
        {
            var saved = _saved;
            if (saved is null || tenantId != saved.Investigation.TenantId || id != _id)
                throw new HttpRequestException("Conversation unavailable in this tenant.", null, HttpStatusCode.NotFound);
            return Task.FromResult(saved with { Events = saved.Events.Where(item => item.Sequence > after).ToArray() });
        }
        public async Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default)
        {
            var saved = await GetAsync(tenantId, id, ct: ct);
            Interlocked.Increment(ref _sends);
            const string answer = "## Script draft\n\nThis **read-only** PowerShell draft lists service names and their current state.\n\n```powershell\nGet-Service | Select-Object Name, Status\n```\n\nReview the draft before choosing a target system.";
            var now = DateTimeOffset.UtcNow;
            var completed = saved with
            {
                LastSequence = 2,
                Events = [new(1, "operator", request.Text, "operator", null, null, now, request.MessageId), new(2, "assistant", answer, null, null, null, now)]
            };
            _saved = completed;
            return completed;
        }
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException("Chat must not request systems.");
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
