using AwesomeAssertions;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientReleaseCataloguePagingTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Traversal_has_no_missing_or_duplicate_releases_across_source_page_boundaries(int pageSize)
    {
        const int count = 237;
        var received = new List<long>();
        for (var page = 0; ; page++)
        {
            var result = await ClientReleaseCataloguePaging.ReadAsync((source, refresh, ct) => Task.FromResult(Source(source, count)), page, pageSize, false);
            result.Items.Count.Should().BeLessThanOrEqualTo(pageSize);
            result.Page.Should().Be(page);
            received.AddRange(result.Items.Select(item => item.Id));
            if (!result.HasMore) break;
            page.Should().BeLessThan(30, "discovery must remain bounded");
        }
        received.Should().Equal(Enumerable.Range(0, count).Select(index => (long)index));
    }

    [Fact]
    public async Task Refresh_forces_one_prefix_fetch_and_uses_cached_slices()
    {
        var calls = new List<(int Page, bool Refresh)>();
        Task<GitHubClientReleasePageModel> Read(int page, bool refresh, CancellationToken ct)
        {
            calls.Add((page, refresh));
            return Task.FromResult(Source(page, 200));
        }
        var result = await ClientReleaseCataloguePaging.ReadAsync(Read, 1, 50, true);
        result.Items.Select(item => item.Id).Should().Equal(Enumerable.Range(50, 50).Select(index => (long)index));
        calls.Should().Equal((4, true), (2, false), (3, false));
    }

    [Fact]
    public async Task Incomplete_source_page_preserves_warning_and_does_not_skip_unavailable_rows()
    {
        var calls = new List<int>();
        Task<GitHubClientReleasePageModel> Read(int page, bool refresh, CancellationToken ct)
        {
            calls.Add(page);
            var result = Source(page, 200);
            if (page == 1)
            {
                result.Items = [];
                result.Warning = "Upstream temporarily unavailable; refresh this page.";
            }
            return Task.FromResult(result);
        }
        var result = await ClientReleaseCataloguePaging.ReadAsync(Read, 0, 100, false);
        result.Items.Select(item => item.Id).Should().Equal(Enumerable.Range(0, 20).Select(index => (long)index));
        result.Warning.Should().Contain("temporarily unavailable");
        calls.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Last_supported_page_stops_at_the_catalogue_scan_boundary()
    {
        var calls = new List<int>();
        Task<GitHubClientReleasePageModel> Read(int page, bool refresh, CancellationToken ct)
        {
            calls.Add(page);
            var result = Source(page, 700);
            result.ScanLimitReached = page == 29;
            result.Warning = page == 29 ? "The scan limit was reached; older matching releases may exist." : null;
            return Task.FromResult(result);
        }
        var result = await ClientReleaseCataloguePaging.ReadAsync(Read, 5, 100, false);
        result.Items.Should().HaveCount(100);
        result.HasMore.Should().BeFalse();
        result.ScanLimitReached.Should().BeTrue();
        result.Warning.Should().Contain("scan limit");
        calls.Should().Equal(25, 26, 27, 28, 29);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 10)]
    [InlineData(6, 100)]
    public async Task Invalid_page_requests_are_rejected_before_any_fetch(int page, int pageSize)
    {
        var called = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClientReleaseCataloguePaging.ReadAsync((source, refresh, ct) =>
        {
            called = true;
            return Task.FromResult(Source(source, 100));
        }, page, pageSize, false));
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_prevents_fetching_later_slices()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientReleaseCataloguePaging.ReadAsync((page, refresh, ct) =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(Source(page, 200));
        }, 0, 100, false, cancellation.Token));
        calls.Should().Be(1);
    }

    private static GitHubClientReleasePageModel Source(int page, int count) => new()
    {
        Page = page,
        Items = Enumerable.Range(0, count).Skip(page * 20).Take(20)
            .Select(index => new GitHubClientReleaseModel { Id = index, Version = $"0.4.{index}", Name = $"Release {index}" }).ToList(),
        HasMore = (page + 1) * 20 < count,
        RefreshedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };
}
