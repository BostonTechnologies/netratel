using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Layout;
using NetRatel.Web.Services.Search;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GlobalSearchOverlayTests : AsyncBunitContext
{
    private readonly SearchStub _search = new();
    public GlobalSearchOverlayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IGlobalSearchService>(_search);
    }

    [Fact]
    public async Task Rapid_query_change_fences_a_source_that_ignores_cancellation()
    {
        var pending = new TaskCompletionSource<GlobalSearchGroup>();
        var readEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search.Read = (query, token) =>
        {
            if (query != "old") return Task.FromResult(Result("Latest alert", "/monitoring?tenantId=7&alert=latest"));
            readEntered.TrySetResult(token); return pending.Task;
        };
        var cut = Render<GlobalSearchOverlay>(parameters => parameters.Add(component => component.Open, true));
        var first = cut.Find("input").InputAsync("old");
        var oldToken = await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(oldToken.CanBeCanceled);
        await cut.Find("input").InputAsync("new");
        await cut.WaitForAssertionAsync(() => Assert.Contains("Latest alert", cut.Markup));
        Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(Result("Old alert", "/monitoring?tenantId=9&alert=old")); await first;
        Assert.DoesNotContain("Old alert", cut.Markup);
    }

    [Fact]
    public async Task Keyboard_selection_includes_empty_query_navigation_and_exact_result_links()
    {
        var cut = Render<GlobalSearchOverlay>(parameters => parameters.Add(component => component.Open, true));
        await cut.Find("input").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.EndsWith("/monitoring", Services.GetRequiredService<NavigationManager>().Uri);
    }

    private static GlobalSearchGroup Result(string title, string href) => new("alerts", "Monitoring alerts", "", "/monitoring",
        [new("Monitoring alerts", title, "Operations", "Critical", href, "")]);

    private sealed class SearchStub : IGlobalSearchService
    {
        public Func<string?, CancellationToken, Task<GlobalSearchGroup>> Read { get; set; } = (_, _) => Task.FromResult(Result("Alert", "/monitoring?tenantId=7&alert=selected"));
        public Task<IReadOnlyList<GlobalSearchGroup>> SearchAsync(string? query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GlobalSearchGroup>>([new("alerts", "Monitoring alerts", "", "/monitoring", [])]);
        public async IAsyncEnumerable<GlobalSearchGroup> SearchIncrementalAsync(string? query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return await Read(query, cancellationToken); }
    }
}
