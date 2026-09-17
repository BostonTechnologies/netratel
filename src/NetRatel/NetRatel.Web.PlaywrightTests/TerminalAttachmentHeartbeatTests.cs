using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class TerminalAttachmentHeartbeatTests : IAsyncLifetime
{
    private const string Generation = "18446744073709551615";
    private readonly ConcurrentQueue<string> _browserErrors = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage _page = null!;

    [Fact]
    public async Task ActualModule_ImportsAndRenewsWithTheConfirmedLeaseAndExactGeneration()
    {
        await _page.EvaluateAsync("fixture.result.attachmentLeaseId = 'lease-confirmed'");
        await StartAsync();
        await _page.Clock.RunForAsync(30_000);

        Assert.Equal(2, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        Assert.True(await _page.EvaluateAsync<bool>("fixture.calls[0][5]"));
        Assert.False(await _page.EvaluateAsync<bool>("fixture.calls[1][5]"));
        Assert.Equal("lease-initial", await _page.EvaluateAsync<string>("fixture.calls[0][3]"));
        Assert.Equal("lease-confirmed", await _page.EvaluateAsync<string>("fixture.calls[1][3]"));
        Assert.Equal(Generation, await _page.EvaluateAsync<string>("fixture.calls[1][2]"));
        Assert.True(await _page.EvaluateAsync<bool>("fixture.calls[0][4] === fixture.calls[1][4] && !!fixture.calls[1][4]"));
        Assert.Empty(_browserErrors);
    }

    [Fact]
    public async Task VisibleActivation_CoalescesAndUsesTheLeaseFromThePendingRenewal()
    {
        await _page.EvaluateAsync("fixture.heartbeatMode = 'hold'");
        await StartAsync();
        await _page.EvaluateAsync("""
            () => {
                window.dispatchEvent(new Event('focus'));
                document.dispatchEvent(new Event('visibilitychange'));
            }
            """);
        Assert.Equal(0, await CountAsync("OnGatewayTerminalBrowserActive"));
        await _page.EvaluateAsync("""
            () => {
                fixture.heartbeatMode = 'normal';
                fixture.resolveHeartbeat({ shouldContinue: true, ownershipConfirmed: true, attachmentLeaseId: 'lease-active' });
            }
            """);

        Assert.Equal(1, await CountAsync("OnGatewayTerminalBrowserActive"));
        Assert.Equal(new[] { "OnGatewayTerminalBrowserActive", "session-1", Generation, "lease-active" },
            await _page.EvaluateAsync<string[]>("fixture.calls.find(call => call[0] === 'OnGatewayTerminalBrowserActive')"));

        await _page.EvaluateAsync("""
            () => {
                fixture.visibility = 'hidden';
                document.dispatchEvent(new Event('visibilitychange'));
                window.dispatchEvent(new Event('focus'));
            }
            """);
        Assert.Equal(1, await CountAsync("OnGatewayTerminalBrowserActive"));
        await _page.EvaluateAsync("""
            () => {
                fixture.visibility = 'visible';
                document.dispatchEvent(new Event('visibilitychange'));
            }
            """);
        Assert.Equal(2, await CountAsync("OnGatewayTerminalBrowserActive"));
        Assert.Empty(_browserErrors);
    }

    [Fact]
    public async Task BfCache_ResumesRenewalAndReconciliationWithoutAcceptingTheFrozenAttempt()
    {
        await _page.EvaluateAsync("fixture.heartbeatMode = 'hold'");
        await StartAsync();
        await _page.EvaluateAsync("""
            () => {
                fixture.resolveFrozenHeartbeat = fixture.resolveHeartbeat;
                window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
                window.dispatchEvent(new Event('focus'));
            }
            """);
        await _page.Clock.RunForAsync(1_000);
        Assert.Equal(1, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        Assert.Equal(0, await CountAsync("OnGatewayTerminalBrowserActive"));

        await _page.EvaluateAsync("""
            () => {
                fixture.heartbeatMode = 'normal';
                window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
            }
            """);
        Assert.Equal(2, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        Assert.Equal(1, await CountAsync("OnGatewayTerminalBrowserActive"));
        await _page.EvaluateAsync("fixture.resolveFrozenHeartbeat(false)");
        await _page.Clock.RunForAsync(30_000);
        Assert.Equal(3, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));

        await _page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }))");
        await _page.Clock.RunForAsync(60_000);
        Assert.Equal(3, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        await _page.EvaluateAsync("window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }))");
        Assert.Equal(4, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        Assert.Equal(2, await CountAsync("OnGatewayTerminalBrowserActive"));
        Assert.Empty(_browserErrors);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("unload")]
    [InlineData("pagehide")]
    [InlineData("rejected-lease")]
    public async Task Cleanup_RemovesTimersAndActivityListeners(string cleanup)
    {
        if (cleanup == "rejected-lease")
        {
            await _page.EvaluateAsync("fixture.result = false");
        }
        await StartAsync();
        await _page.EvaluateAsync("""
            cleanup => {
                if (cleanup === 'stop') fixture.module.stopTerminalAttachmentHeartbeat(fixture.id);
                if (cleanup === 'unload') window.dispatchEvent(new Event('beforeunload'));
                if (cleanup === 'pagehide') window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false }));
                window.dispatchEvent(new Event('focus'));
                document.dispatchEvent(new Event('visibilitychange'));
                window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
            }
            """, cleanup);
        await _page.Clock.RunForAsync(90_000);

        Assert.Equal(1, await CountAsync("OnGatewayTerminalAttachmentHeartbeat"));
        Assert.Equal(0, await CountAsync("OnGatewayTerminalBrowserActive"));
        Assert.Equal(0, await _page.EvaluateAsync<int>("fixture.listeners.length"));
        Assert.Empty(_browserErrors);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("reject")]
    [InlineData("hold")]
    public async Task CallbackFailuresAndTimeouts_AllowLaterRenewalAndActivation(string failure)
    {
        await _page.EvaluateAsync("mode => { fixture.heartbeatMode = mode; fixture.activeMode = mode; }", failure);
        await StartAsync();
        await _page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        await _page.Clock.RunForAsync(24_001);
        await _page.EvaluateAsync("fixture.heartbeatMode = fixture.activeMode = 'normal'");
        await _page.Clock.RunForAsync(6_000);
        await _page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");

        Assert.True(await CountAsync("OnGatewayTerminalAttachmentHeartbeat") >= 2);
        Assert.Equal(2, await CountAsync("OnGatewayTerminalBrowserActive"));
        Assert.Empty(_browserErrors);
    }

    private Task StartAsync() => _page.EvaluateAsync("""
        generation => {
            fixture.id = fixture.module.startTerminalAttachmentHeartbeat(fixture.dotNet, 'session-1', generation, 'lease-initial');
        }
        """, Generation);

    private Task<int> CountAsync(string method) => _page.EvaluateAsync<int>(
        "method => fixture.calls.filter(call => call[0] === method).length", method);

    public async Task InitializeAsync()
    {
        var script = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/js/terminalAttachmentHeartbeat.js")));
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        _page = await _browser.NewPageAsync();
        _page.PageError += (_, error) => _browserErrors.Enqueue(error);
        _page.Console += (_, message) =>
        {
            if (message.Type == "error") _browserErrors.Enqueue(message.Text);
        };
        await _page.RouteAsync("http://terminal.test/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = route.Request.Url.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/html",
            Body = route.Request.Url.EndsWith(".js", StringComparison.Ordinal) ? script : "<!doctype html><title>Terminal browser lifecycle</title>"
        }));
        await _page.Clock.InstallAsync();
        await _page.GotoAsync("http://terminal.test/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.Clock.PauseAtAsync(DateTime.UtcNow.Date.AddDays(1));
        await _page.EvaluateAsync("""
            async () => {
                window.fixture = {
                    module: await import('/terminalAttachmentHeartbeat.js'),
                    visibility: 'visible',
                    calls: [],
                    listeners: [],
                    heartbeatMode: 'normal',
                    activeMode: 'normal',
                    result: { shouldContinue: true, ownershipConfirmed: true }
                };
                Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => fixture.visibility });
                for (const target of [window, document]) {
                    const add = target.addEventListener.bind(target);
                    const remove = target.removeEventListener.bind(target);
                    target.addEventListener = (type, listener, options) => {
                        fixture.listeners.push({ target, type, listener });
                        add(type, listener, options);
                    };
                    target.removeEventListener = (type, listener, options) => {
                        fixture.listeners = fixture.listeners.filter(item => item.target !== target || item.type !== type || item.listener !== listener);
                        remove(type, listener, options);
                    };
                }
                fixture.dotNet = {
                    invokeMethodAsync: (...args) => {
                        fixture.calls.push(args);
                        const heartbeat = args[0] === 'OnGatewayTerminalAttachmentHeartbeat';
                        const mode = heartbeat ? fixture.heartbeatMode : fixture.activeMode;
                        if (mode === 'throw') throw new Error('Circuit unavailable');
                        if (mode === 'reject') return Promise.reject(new Error('Circuit unavailable'));
                        if (mode === 'hold') return new Promise(resolve => {
                            if (heartbeat) fixture.resolveHeartbeat = resolve;
                            else fixture.resolveActive = resolve;
                        });
                        return Promise.resolve(heartbeat ? fixture.result : undefined);
                    }
                };
            }
            """);
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
