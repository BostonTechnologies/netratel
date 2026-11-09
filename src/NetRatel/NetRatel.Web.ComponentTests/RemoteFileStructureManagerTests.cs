using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Web.Services.FileSystem;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteFileStructureManagerTests
{
    private static readonly Guid Agent = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");

    [Fact]
    public async Task TreeStartedLoadCommitsCanonicalChildrenAndNotifiesAfterThePackagesSecondCallerReturns()
    {
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = Provider(_ => { calls++; return completion.Task; });
        var directory = provider.Directory("/alpha");
        var notifications = new List<RemoteDirectoryLoadStatus>();
        provider.ListingChanged += () =>
        {
            var state = provider.DirectoryListing(directory);
            notifications.Add(state.Status);
            if (state.Status == RemoteDirectoryLoadStatus.Loaded)
                directory.Children.Should().ContainSingle().Which.FullPath.Should().Be("/alpha/notes.txt");
            return Task.CompletedTask;
        };
        var first = directory.LoadChildren();
        await directory.LoadChildren(); // The pinned Hierarchical API returns early for this second consumer.
        provider.DirectoryListing(directory).Status.Should().Be(RemoteDirectoryLoadStatus.Loading);
        first.IsCompleted.Should().BeFalse();
        completion.SetResult(Listing("/alpha", [new("notes.txt", "/alpha/notes.txt", false, 5)]));
        await first;
        notifications.Should().Equal(RemoteDirectoryLoadStatus.Loading, RemoteDirectoryLoadStatus.Loaded);
        provider.Directory("/alpha").Should().BeSameAs(directory);
        directory.Children.Single().Parent.Should().BeSameAs(directory);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task LateOtherDirectoryAndInvalidatedGenerationCannotReplaceCurrentAuthority()
    {
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Provider(request => request.RequestUri!.Query.Contains("alpha", StringComparison.Ordinal)
            ? late.Task : Task.FromResult(Listing("/beta", [new("current.txt", "/beta/current.txt", false, 1)])));
        var old = provider.Directory("/alpha");
        var pending = old.LoadChildren();
        provider.CurrentPath = "/beta";
        var current = provider.Directory("/beta");
        await current.LoadChildren();
        late.SetResult(Listing("/alpha", [new("late.txt", "/alpha/late.txt", false, 2)]));
        await pending;
        provider.Listing!.Path.Should().Be("/beta");
        current.Children.Single().Name.Should().Be("current.txt");

        var stale = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleProvider = Provider(_ => stale.Task);
        var staleNode = staleProvider.Directory("/alpha");
        var staleLoad = staleNode.LoadChildren();
        staleProvider.Invalidate();
        var replacement = staleProvider.Directory("/alpha");
        stale.SetResult(Listing("/alpha", [new("stale.txt", "/alpha/stale.txt", false, 2)]));
        await staleLoad;
        staleProvider.TryEntry(staleNode, out _).Should().BeFalse();
        staleProvider.DirectoryListing(replacement).Status.Should().Be(RemoteDirectoryLoadStatus.NotLoaded);
        staleProvider.Listing.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, RemoteDirectoryLoadStatus.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, RemoteDirectoryLoadStatus.Unavailable)]
    [InlineData(HttpStatusCode.OK, RemoteDirectoryLoadStatus.Loaded)]
    public async Task FailedAndAuthoritativelyEmptyListingsRemainDistinct(HttpStatusCode status, RemoteDirectoryLoadStatus expected)
    {
        var provider = Provider(_ => Task.FromResult(status == HttpStatusCode.OK ? Listing("/alpha", [])
            : new HttpResponseMessage(status) { Content = JsonContent.Create(new { detail = "Synthetic listing unavailable" }) }));
        var directory = provider.Directory("/alpha");
        await directory.LoadChildren();
        var state = provider.DirectoryListing(directory);
        state.Status.Should().Be(expected);
        directory.Children.Should().BeEmpty();
        if (expected == RemoteDirectoryLoadStatus.Unavailable)
        {
            state.Error.Should().Contain("Synthetic listing unavailable");
            provider.Listing.Should().BeNull();
        }
        else state.Response!.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellationAndForeignOrOversizeResponsesNeverBecomeAuthoritativeEmptyDirectories()
    {
        foreach (var response in new[]
        {
            new GatewayFileSystemListResponse(4, Agent, "/alpha", "fixture", []),
            new GatewayFileSystemListResponse(3, Agent, "/other", "fixture", []),
            new GatewayFileSystemListResponse(3, Agent, "/alpha", "fixture", Enumerable.Range(0, 4097).Select(i => new GatewayFileSystemEntryDto($"{i}", $"/alpha/{i}", false, 0)).ToArray())
        })
        {
            var provider = Provider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) }));
            var directory = provider.Directory("/alpha");
            await directory.LoadChildren();
            provider.DirectoryListing(directory).Status.Should().Be(RemoteDirectoryLoadStatus.Unavailable);
            provider.Listing.Should().BeNull();
        }
        var cancelled = Provider(_ => Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        var node = cancelled.Directory("/alpha");
        await node.LoadChildren();
        cancelled.DirectoryListing(node).Error.Should().Contain("cancelled");
    }

    private static RemoteFileStructureManager Provider(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new GatewayFileSystemApiService(new Factory(respond)), 3, Agent, "/alpha", CancellationToken.None);
    private static HttpResponseMessage Listing(string path, GatewayFileSystemEntryDto[] entries) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new GatewayFileSystemListResponse(3, Agent, path, "fixture", entries) { CanCreateDirectory = true }) };
    private sealed class Factory(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(respond)) { BaseAddress = new("https://netratel.test") };
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
