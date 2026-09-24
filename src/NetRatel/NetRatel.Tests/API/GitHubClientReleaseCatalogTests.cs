using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GitHubClientReleaseCatalogTests
{
    [Fact]
    public async Task StableFilterScansOlderPagesAndDoesNotAdvertiseIncompletePackAsReady()
    {
        var requests = new List<Uri>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!);
            var page = request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal) ? 1 : 2;
            var response = Json(page == 1
                ? $"[{Release(1, "v0.1.0-rc.9", true, true)}]"
                : $"[{Release(2, "v0.1.0", false, false)}]");
            if (page == 1) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/BostonTechnologies/netratel/releases?page=2>; rel=\"next\"");
            return response;
        });
        var catalog = Catalog(client);

        var result = await catalog.ListAsync("stable", 0, false, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].Version.Should().Be("0.1.0");
        result.Items[0].PublicationState.Should().Be("incomplete publication evidence");
        result.Items[0].DetailsUrl.Should().Be("https://github.com/BostonTechnologies/netratel/releases/tag/v0.1.0");
        result.HasMore.Should().BeFalse();
        requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RefreshUsesEtagAndRetainsCachedReleasesOnNotModifiedAndRateLimit()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            if (calls == 1)
            {
                var first = Json($"[{Release(1, "v0.1.0-rc.9", true, true)}]");
                first.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"one\"");
                return first;
            }
            request.Headers.IfNoneMatch.Should().ContainSingle(x => x.Tag == "\"one\"");
            if (calls == 2) return new HttpResponseMessage(HttpStatusCode.NotModified);
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return limited;
        });
        var catalog = Catalog(client);

        (await catalog.ListAsync("all", 0, false, CancellationToken.None)).Items.Should().HaveCount(1);
        (await catalog.ListAsync("all", 0, true, CancellationToken.None)).Items.Should().HaveCount(1);
        var stale = await catalog.ListAsync("all", 0, true, CancellationToken.None);

        stale.Items.Should().HaveCount(1);
        stale.Warning.Should().Contain("rate limited");
        (await catalog.ListAsync("all", 0, true, CancellationToken.None)).Items.Should().HaveCount(1);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task DraftMalformedAndMismatchedPrereleaseMetadataAreNotEligible()
    {
        using var client = Client(_ => Json($"[{{\"id\":1,\"draft\":true}},{{\"id\":2,\"draft\":false}}," +
                                            Release(3, "v0.1.0-rc.1", false, true) + "]"));
        var catalog = Catalog(client);

        var result = await catalog.ListAsync("all", 0, false, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].PublicationState.Should().Be("incompatible prerelease metadata");
        result.Items[0].ClientAssets.Should().ContainSingle();
    }

    [Fact]
    public async Task TimeoutKeepsCachedResultsAndBacksOffFurtherRequests()
    {
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            if (calls == 1) return Json($"[{Release(1, "v0.1.0-rc.9", true, true)}]");
            throw new TaskCanceledException("transport timeout");
        });
        var catalog = Catalog(client);
        (await catalog.ListAsync("all", 0, false, CancellationToken.None)).Items.Should().HaveCount(1);

        var stale = await catalog.ListAsync("all", 0, true, CancellationToken.None);
        stale.Items.Should().HaveCount(1);
        stale.Warning.Should().Contain("temporarily unavailable");
        (await catalog.ListAsync("all", 0, true, CancellationToken.None)).Items.Should().HaveCount(1);
        calls.Should().Be(2);
    }

    private static GitHubClientReleaseCatalog Catalog(HttpClient client) => new(
        client, new ConfigurationBuilder().Build(), TimeProvider.System,
        NullLogger<GitHubClientReleaseCatalog>.Instance);

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new StubHandler(handler)) { BaseAddress = new Uri("https://api.github.com/") };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static string Release(long id, string tag, bool prerelease, bool complete) => $$"""
        {"id":{{id}},"tag_name":"{{tag}}","name":"{{tag}}","draft":false,"prerelease":{{prerelease.ToString().ToLowerInvariant()}},
         "published_at":"2026-08-01T00:00:00Z","assets":[
           {"id":11,"name":"netratel-client-{{tag.TrimStart('v')}}-linux-x64.tar.gz","state":"uploaded","size":100,"digest":"sha256:abc"}
           {{(complete ? ",{\"id\":12,\"name\":\"publication.json\",\"state\":\"uploaded\",\"size\":1},{\"id\":13,\"name\":\"SHA256SUMS\",\"state\":\"uploaded\",\"size\":1}" : "")}}
         ]}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
