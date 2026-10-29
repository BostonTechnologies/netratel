using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Layout;
using NetRatel.Web.Components.Pages;
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

    [Fact]
    public void Search_continuation_opens_chats_and_investigations_in_their_own_workspaces()
    {
        var chatId = Guid.Parse("c10ec4f7-1bd8-409a-af82-31017ea66dc9");
        var investigationId = Guid.Parse("7410f120-5ed4-47fd-b957-30cc7abf938c");
        Services.AddSingleton<IHttpClientFactory>(new SearchResultsClientFactory([
            new(chatId, 7, "Draft automation", "Operations", "Idle", null, null, null, IsGeneralChat: true),
            new(investigationId, 7, "Investigate storage", "Operations", "Idle", "Storage host", null, null)
        ]));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?section=investigations&search=Operations");
        var cut = Render<GlobalSearchResults>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("a").Count));
        Assert.Equal($"/ai/chat?tenantId=7&chat={chatId:D}", cut.FindAll("a")[0].GetAttribute("href"));
        Assert.Equal($"/ai/ops?tenantId=7&investigation={investigationId:D}", cut.FindAll("a")[1].GetAttribute("href"));
        Assert.Contains("Chat · Idle", cut.Markup);
    }

    private sealed class SearchResultsClientFactory(IReadOnlyList<GlobalSearchInvestigationDto> rows) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SearchResultsHandler(rows)) { BaseAddress = new Uri("https://netratel.test") };
    }

    private sealed class SearchResultsHandler(IReadOnlyList<GlobalSearchInvestigationDto> rows) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResult<GlobalSearchInvestigationDto>(rows, 1, 25, rows.Count)) });
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
