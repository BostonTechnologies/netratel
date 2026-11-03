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
        await Assertions.Expect(page.GetByTestId("chat-send")).ToBeInViewportAsync(new() { Ratio = 1 });
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

    [Theory]
    [InlineData(false, 1366, 900, "light")]
    [InlineData(false, 390, 844, "dark")]
    [InlineData(true, 1366, 900, "dark")]
    [InlineData(true, 390, 844, "light")]
    public async Task Shared_conversation_fullscreen_preserves_draft_details_and_focus_and_sends_once(bool investigation, int width, int height, string theme)
    {
        var api = new ChatFixture(investigation);
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<INetclawApiService>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        var page = await context.NewPageAsync();
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        try { await page.GotoAsync(host.BaseAddress + (investigation ? $"/ai/ops?tenantId=17&investigation={api.Id}" : $"/ai/chat?tenantId=17&chat={api.Id}")); }
        catch (Exception ex) { throw new InvalidOperationException(string.Join("\n", host.StartupServerDiagnostics), ex); }
        var surface = page.GetByTestId("netclaw-conversation");
        await surface.WaitForAsync();
        await Assertions.Expect(page.Locator($"html[data-netratel-theme='{theme}']")).ToHaveCountAsync(1);
        if (width < 1280)
        {
            var scrim = page.Locator(".mud-drawer-overlay--open .mud-overlay-scrim");
            if (await scrim.CountAsync() > 0 && await scrim.IsVisibleAsync())
                await scrim.ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2f } });
            await Assertions.Expect(page.GetByTestId("app-navigation-drawer")).ToHaveClassAsync(new Regex("mud-drawer--closed"));
        }
        var composer = page.GetByTestId("chat-message");
        await composer.FillAsync("draft survives fullscreen");
        var trace = surface.Locator(".netclaw-trace-row").First;
        await trace.Locator("summary").ClickAsync();
        await Assertions.Expect(trace).ToHaveAttributeAsync("open", "");
        await surface.EvaluateAsync("e => e.dataset.identityProbe = 'same-surface'");
        var maximize = surface.GetByRole(AriaRole.Button, new() { Name = "Maximise conversation", Exact = true });
        await maximize.ClickAsync();
        await Assertions.Expect(surface).ToHaveAttributeAsync("aria-modal", "true");
        await Assertions.Expect(surface).ToHaveAttributeAsync("data-identity-probe", "same-surface");
        await Assertions.Expect(composer).ToHaveValueAsync("draft survives fullscreen");
        await Assertions.Expect(trace).ToHaveAttributeAsync("open", "");
        await Assertions.Expect(surface.GetByRole(AriaRole.Button, new() { Name = "Restore conversation", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(page.GetByTestId("chat-send")).ToBeInViewportAsync(new() { Ratio = 1 });
        await surface.GetByRole(AriaRole.Button, new() { Name = "Chat display settings", Exact = true }).ClickAsync();
        await Assertions.Expect(surface.Locator(".netclaw-display-panel")).ToBeVisibleAsync();
        await composer.PressAsync("Escape");
        await Assertions.Expect(surface.Locator(".netclaw-display-panel")).ToHaveCountAsync(0);
        await Assertions.Expect(surface).ToHaveAttributeAsync("aria-modal", "true");
        await composer.PressAsync("Escape");
        await Assertions.Expect(surface).Not.ToHaveAttributeAsync("aria-modal", "true");
        await Assertions.Expect(maximize).ToBeFocusedAsync();
        await Assertions.Expect(composer).ToHaveValueAsync("draft survives fullscreen");
        await maximize.ClickAsync();
        await Assertions.Expect(surface).ToHaveAttributeAsync("aria-modal", "true");
        await composer.PressAsync("Control+Enter");
        await Assertions.Expect(composer).ToHaveValueAsync("");
        Assert.Equal(1, api.Sends);
        await surface.GetByRole(AriaRole.Button, new() { Name = "Restore conversation", Exact = true }).ClickAsync();
        await AssertHealthyAsync(page); Assert.Empty(errors);
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"shared-{(investigation ? "investigation" : "chat")}-{theme}-{width}.png"), FullPage = true });
        var previousOverflow = await page.EvaluateAsync<string>("() => document.body.style.overflow");
        await maximize.ClickAsync();
        await Assertions.Expect(surface).ToHaveAttributeAsync("aria-modal", "true");
        await page.EvaluateAsync("() => Blazor.navigateTo('/ai/chat?tenantId=18')");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Connect with Netclaw", Exact = true })).ToBeVisibleAsync();
        await page.WaitForFunctionAsync("previous => document.body.style.overflow === previous", previousOverflow);
        Assert.False(await page.GetByTestId("navigation-toggle").EvaluateAsync<bool>("e => !!e.closest('[inert]')"));
        await AssertHealthyAsync(page); Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false, 1366, 768)]
    [InlineData(false, 390, 844)]
    [InlineData(false, 1366, 600)]
    [InlineData(true, 1366, 768)]
    [InlineData(true, 390, 844)]
    public async Task Long_history_keeps_composer_and_archive_visible_and_enter_sends_once(bool investigation, int width, int height)
    {
        var api = new ChatFixture(investigation, dense: true);
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<INetclawApiService>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + (investigation ? $"/ai/ops?tenantId=17&investigation={api.Id}" : $"/ai/chat?tenantId=17&chat={api.Id}"));
        var surface = page.GetByTestId("netclaw-conversation");
        await surface.WaitForAsync();
        await CloseNavigationAsync(page, width, height);
        var composer = page.GetByTestId("chat-message");
        var send = page.GetByTestId("chat-send");
        // Check geometry before interaction: clicking/filling can scroll broken layouts into view.
        await Assertions.Expect(composer).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(send).ToBeInViewportAsync(new() { Ratio = 1 });
        await composer.FillAsync("First line");
        await composer.PressAsync("Shift+Enter");
        await composer.PressSequentiallyAsync("Second line");
        await Assertions.Expect(composer).ToHaveValueAsync("First line\nSecond line");
        Assert.Equal(0, api.Sends);
        await composer.PressAsync("Enter");
        await Assertions.Expect(composer).ToHaveValueAsync("");
        Assert.Equal(1, api.Sends);
        await Assertions.Expect(send).ToBeInViewportAsync(new() { Ratio = 1 });
        await composer.FillAsync("Another turn");
        await composer.PressAsync("Enter");
        await Assertions.Expect(composer).ToHaveValueAsync("");
        Assert.Equal(2, api.Sends);
        await Assertions.Expect(surface.Locator(".chat-message-assistant").Last).ToBeInViewportAsync();
        await Assertions.Expect(surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        await surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true }).ClickAsync();
        var confirm = page.GetByTestId("archive-confirm");
        await Assertions.Expect(confirm).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(confirm).ToBeEnabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep open", Exact = true }).ClickAsync();
        await Assertions.Expect(confirm).ToHaveCountAsync(0);
        await surface.GetByRole(AriaRole.Button, new() { Name = "Maximise conversation", Exact = true }).ClickAsync();
        await surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true }).ClickAsync();
        await Assertions.Expect(confirm).ToBeInViewportAsync(new() { Ratio = 1 });
        await confirm.PressAsync("Escape");
        await Assertions.Expect(confirm).ToHaveCountAsync(0);
        await Assertions.Expect(surface).ToHaveAttributeAsync("aria-modal", "true");
        await surface.GetByRole(AriaRole.Button, new() { Name = "Restore conversation", Exact = true }).ClickAsync();
        await Assertions.Expect(surface.Locator(".chat-message-assistant").Last).ToBeInViewportAsync();
        await AssertHealthyAsync(page);
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"long-{(investigation ? "ops" : "chat")}-{width}x{height}.png"), FullPage = true });
    }

    [Theory]
    [InlineData(false, 1366, 768)]
    [InlineData(true, 390, 844)]
    public async Task Unresolved_long_conversation_shows_recovery_result_and_can_archive_after_a_failed_attempt(bool investigation, int width, int height)
    {
        var api = new ChatFixture(investigation, dense: true, state: InvestigationState.DeliveryUnknown) { FailNextArchive = true };
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<INetclawApiService>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + (investigation ? $"/ai/ops?tenantId=17&investigation={api.Id}" : $"/ai/chat?tenantId=17&chat={api.Id}"));
        var surface = page.GetByTestId("netclaw-conversation");
        await surface.WaitForAsync();
        await CloseNavigationAsync(page, width, height);
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"recovery-{(investigation ? "ops" : "chat")}-{width}.png"), FullPage = true });
        var recover = surface.GetByRole(AriaRole.Button, new() { Name = "Recover session", Exact = true });
        await Assertions.Expect(recover).ToBeInViewportAsync(new() { Ratio = 1 });
        await recover.ClickAsync();
        await Assertions.Expect(surface.Locator(".chat-recovery")).ToContainTextAsync("Fixture session checked. Historical outcome is unavailable. Nothing was resent.");
        await Assertions.Expect(surface.Locator(".chat-action-feedback")).ToContainTextAsync("Session check finished");
        await Assertions.Expect(surface.Locator(".chat-recovery p").First).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(surface.Locator(".chat-recovery small")).ToBeInViewportAsync(new() { Ratio = 1 });
        Assert.Equal(1, api.Recoveries); Assert.Equal(0, api.Sends);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"recovery-result-{(investigation ? "ops" : "chat")}-{width}.png"), FullPage = true });
        await surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true }).ClickAsync();
        var confirm = page.GetByTestId("archive-confirm");
        await Assertions.Expect(confirm).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(confirm).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "I understand that remote actions", Exact = false }).CheckAsync();
        await confirm.ClickAsync();
        await Assertions.Expect(surface.GetByRole(AriaRole.Alert)).ToContainTextAsync("Fixture archive failed. Retry after refreshing.");
        await Assertions.Expect(surface.GetByRole(AriaRole.Alert)).ToBeInViewportAsync(new() { Ratio = 1 });
        await surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true }).ClickAsync();
        await Assertions.Expect(confirm).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "I understand that remote actions", Exact = false }).CheckAsync();
        await confirm.ClickAsync();
        await Assertions.Expect(surface.Locator(".chat-archived")).ToContainTextAsync("This conversation is archived");
        Assert.Equal(new InvestigationArchiveRequest(Guid.Empty, true), api.LastArchive);
        Assert.Equal(0, api.Sends);
        await AssertHealthyAsync(page);
    }

    [Fact]
    public async Task Rejected_provider_history_keeps_archive_and_send_visible_on_mobile_without_offering_reconnect_as_repair()
    {
        var api = new ChatFixture(false, dense: true, recovery: new(null, "The model provider rejected a name in this session's saved history (HTTP 400). Reconnecting cannot rewrite that history. Archive this conversation and start a new chat, or have the daemon administrator repair the saved history before continuing. Nothing was resent.", "provider_history_invalid_name"));
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<INetclawApiService>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + $"/ai/chat?tenantId=17&chat={api.Id}");
        var surface = page.GetByTestId("netclaw-conversation"); await surface.WaitForAsync();
        await CloseNavigationAsync(page, 390, 844);
        await Assertions.Expect(surface.Locator(".chat-assistant-identity")).ToContainTextAsync("History rejected");
        await Assertions.Expect(surface.GetByRole(AriaRole.Button, new() { Name = "Recover session", Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("chat-message")).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(page.GetByTestId("chat-send")).ToBeInViewportAsync(new() { Ratio = 1 });
        var archive = surface.GetByRole(AriaRole.Button, new() { Name = "Archive chat", Exact = true });
        await Assertions.Expect(archive).ToBeInViewportAsync(new() { Ratio = 1 }); await archive.ClickAsync();
        await Assertions.Expect(page.GetByTestId("archive-confirm")).ToBeInViewportAsync(new() { Ratio = 1 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep open", Exact = true }).ClickAsync();
        Assert.Equal(0, api.Sends); Assert.Equal(0, api.Recoveries);
        await AssertHealthyAsync(page);
    }

    private static async Task CloseNavigationAsync(IPage page, int width, int height)
    {
        if (width >= 1280) return;
        var scrim = page.Locator(".mud-drawer-overlay--open .mud-overlay-scrim");
        if (await scrim.CountAsync() > 0 && await scrim.IsVisibleAsync())
            await scrim.ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2f } });
        await Assertions.Expect(page.GetByTestId("app-navigation-drawer")).ToHaveClassAsync(new Regex("mud-drawer--closed"));
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
        private int _creates, _sends, _recoveries;
        private readonly bool _dense;
        public bool FailNextArchive { get; set; }
        public InvestigationArchiveRequest? LastArchive { get; private set; }
        public int Recoveries => Volatile.Read(ref _recoveries);
        public Guid Id => _id;
        private readonly bool _investigation;
        public ChatFixture(bool? investigation = null, bool dense = false, InvestigationState state = InvestigationState.Idle, InvestigationRecoveryDto? recovery = null)
        {
            _investigation = investigation == true; _dense = dense;
            if (investigation is null) return;
            var now = DateTimeOffset.UtcNow;
            InvestigationEventDto[] events = [new(1, "assistant", "## Saved answer\n\n```sh\necho safe\n```", null, null, null, now),
                new(2, "tool_call", "netclaw/example: started", null, "call-fixture", null, now,
                    Trace: new("netclaw/example", "started", now, "fixture-session", Guid.NewGuid(), "{\"path\":\"/safe/\"}", null, null, null, false, false))];
            if (dense) events = [.. events, .. Enumerable.Range(3, 118).Select(i => new InvestigationEventDto(i, i % 2 == 0 ? "assistant" : "operator", $"Saved message {i}. This is a long conversation used to check independent scrolling and visible controls.", null, null, null, now))];
            _saved = new(new(_id, 17, "Saved fixture conversation", state, _investigation ? [Guid.NewGuid()] : [], null, null, now, "operator"), events.Length, null, events, [], [], Recovery: recovery);
        }
        public int Creates => Volatile.Read(ref _creates);
        public int Sends => Volatile.Read(ref _sends);
        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(17, "Operations", true, true), new(18, "Service desk", true, true)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(
            new NetclawConnectionDto(_id, tenantId, 1, tenantId == 17, "https://netclaw.example.test", tenantId == 17, DateTimeOffset.UtcNow, true, null));
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListChatsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>(tenantId == 17 ? History() : []);
        private IReadOnlyList<InvestigationSummaryDto> History() => _saved is { } saved
            ? [saved.Investigation, .. Enumerable.Range(1, _dense ? 35 : 0).Select(i => saved.Investigation with { Id = Guid.NewGuid(), Title = $"Previous conversation {i}" })] : [];
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
                LastSequence = saved.LastSequence + 2, ActiveMessageId = request.MessageId,
                Events = [.. saved.Events, new(saved.LastSequence + 1, "operator", request.Text, "operator", null, null, now, request.MessageId), new(saved.LastSequence + 2, "assistant", answer, null, null, null, now)]
            };
            _saved = completed;
            return completed;
        }
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => _investigation ? Task.FromResult<IReadOnlyList<InvestigationClientDto>>([]) : throw new NotSupportedException("Chat must not request systems.");
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>(History());
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> TestAsync(int tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawConnectionDto> EnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ApproveAsync(int tenantId, Guid id, string callId, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<InvestigationSnapshotDto> ReconcileAsync(int tenantId, Guid id, CancellationToken ct = default)
        {
            var saved = await GetAsync(tenantId, id, ct: ct); Interlocked.Increment(ref _recoveries);
            _saved = saved with { Recovery = new(DateTimeOffset.UtcNow, "Fixture session checked. Historical outcome is unavailable. Nothing was resent.") };
            return _saved;
        }
        public async Task<InvestigationSnapshotDto> ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, CancellationToken ct = default)
        {
            var saved = await GetAsync(tenantId, id, ct: ct); LastArchive = request;
            if (FailNextArchive) { FailNextArchive = false; throw new HttpRequestException("Fixture archive failed. Retry after refreshing.", null, HttpStatusCode.Conflict); }
            _saved = saved with { Investigation = saved.Investigation with { State = InvestigationState.Archived } };
            return _saved;
        }
    }
}
