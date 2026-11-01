using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GitHubReleasePaginationTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Selected_size_slices_the_filtered_catalogue_without_missing_or_repeating_releases(int size)
    {
        using var handler = new Pages();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var catalog = Catalog(http);
        var first = await catalog.ListAsync("all", 0, size, false, default);
        var second = await catalog.ListAsync("all", 1, size, false, default);
        Assert.Equal(Enumerable.Range(0, size).Select(index => (long)index + 1), first.Items.Select(item => item.Id));
        Assert.Equal(Enumerable.Range(size, Math.Min(size, 125 - size)).Select(index => (long)index + 1), second.Items.Select(item => item.Id));
        Assert.True(first.HasMore);
        Assert.Equal(2 * size < 125, second.HasMore);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        var requests = handler.Requests;
        await catalog.ListAsync("all", 0, size, false, default);
        Assert.Equal(requests, handler.Requests); // page size changes reuse upstream metadata cache.
    }

    [Fact]
    public async Task Channel_filter_is_applied_before_variable_paging_and_legacy_call_still_returns_twenty()
    {
        using var http = new HttpClient(new Pages()) { BaseAddress = new Uri("https://api.github.com/") };
        var catalog = Catalog(http);
        var first = await catalog.ListAsync("stable", 0, 50, false, default);
        var second = await catalog.ListAsync("stable", 1, 50, false, default);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(13, second.Items.Count);
        Assert.All(first.Items.Concat(second.Items), item => Assert.False(item.IsPrerelease));
        Assert.Equal(Enumerable.Range(0, 125).Where(index => index % 2 == 0).Select(index => (long)index + 1),
            first.Items.Concat(second.Items).Select(item => item.Id));
        Assert.False(second.HasMore);
        Assert.Equal(20, (await catalog.ListAsync("all", 0, false, default)).Items.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.ListAsync("all", 0, 200, false, default));
    }

    private static GitHubClientReleaseCatalog Catalog(HttpClient http) => new(http,
        new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<GitHubClientReleaseCatalog>.Instance);

    private sealed class Pages : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
            Assert.Equal("30", query["per_page"]);
            var page = int.Parse(query["page"]);
            var releases = Enumerable.Range(0, 125).Skip((page - 1) * 30).Take(30).Select(index => new
            {
                id = index + 1,
                tag_name = $"v1.2.{index}" + (index % 2 == 1 ? "-beta.1" : ""),
                name = $"Example release {index}",
                draft = false,
                prerelease = index % 2 == 1,
                published_at = "2026-08-01T00:00:00Z",
                assets = Array.Empty<object>()
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json")
            };
            if (page * 30 < 125)
                response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/BostonTechnologies/netratel/releases?page={page + 1}>; rel=\"next\"");
            return Task.FromResult(response);
        }
    }
}
