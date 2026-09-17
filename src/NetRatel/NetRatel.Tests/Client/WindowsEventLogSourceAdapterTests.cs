using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Logging.Windows;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class WindowsEventLogSourceAdapterTests
{
    [Fact]
    public async Task DiscoverAsync_advertises_common_channels_and_preserves_security_access_reason()
    {
        var adapter = new WindowsEventLogSourceAdapter(new FakeWindowsEventLogPlatform(), () => true);

        var sources = await adapter.DiscoverAsync(CancellationToken.None);

        sources.Should().Contain(source => source.SourceId == "windows-eventlog-application" && source.Available);
        sources.Should().Contain(source => source.SourceId == "windows-eventlog-security" && !source.Available && source.UnavailableReason == "access_denied");
        sources.Should().Contain(source => source.DisplayName == "Microsoft-Windows-Example/Operational" && source.Available);
        sources.Where(source => source.Available).Should().OnlyContain(source => source.FilterCapabilities.Contains("category") && source.FilterCapabilities.Contains("provider") && source.FilterCapabilities.Contains("event-id"));
    }

    [Fact]
    public async Task ReadHistoryAsync_applies_typed_filters_and_uses_an_opaque_before_cursor()
    {
        var adapter = new WindowsEventLogSourceAdapter(new FakeWindowsEventLogPlatform(), () => true);
        await adapter.DiscoverAsync(CancellationToken.None);
        var query = new ClientLogQuery("windows-eventlog-application", null, 1, null, null, ["Error"], [], null, ["Example.Provider"], [42], ["Application"]);

        var page = await adapter.ReadHistoryAsync(query, CancellationToken.None);

        page.Records.Should().ContainSingle();
        page.Records[0].EventId.Should().Be(42);
        page.Records[0].RecordId.Should().Be(30);
        page.Records[0].Cursor.Should().StartWith("eventlog:");
        page.Records[0].Cursor.Should().NotContain("Application");
        page.PreviousCursor.Should().StartWith("before:eventlog:");

        var older = await adapter.ReadHistoryAsync(query with { Cursor = page.PreviousCursor }, CancellationToken.None);
        older.Records.Should().ContainSingle();
        older.Records[0].RecordId.Should().Be(20);
    }

    [Fact]
    public async Task FollowAsync_translates_a_watcher_gap_into_a_resync_signal()
    {
        var adapter = new WindowsEventLogSourceAdapter(new FakeWindowsEventLogPlatform(includeGap: true), () => true);
        await adapter.DiscoverAsync(CancellationToken.None);
        var query = new ClientLogQuery("windows-eventlog-application", null, 25, null, null, [], [], null);

        await using var enumerator = adapter.FollowAsync(query, CancellationToken.None).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Record.Should().BeNull();
        enumerator.Current.ResyncRequired.Should().BeTrue();
    }

    private sealed class FakeWindowsEventLogPlatform(bool includeGap = false) : IWindowsEventLogPlatform
    {
        private static readonly WindowsEventLogEntry[] ApplicationEntries =
        [
            new("Application", 30, 42, DateTimeOffset.Parse("2026-08-24T05:00:00Z"), "Error", "Example.Provider", "host-a", "first"),
            new("Application", 20, 42, DateTimeOffset.Parse("2026-08-24T04:00:00Z"), "Error", "Example.Provider", "host-a", "older"),
            new("Application", 10, 7, DateTimeOffset.Parse("2026-08-24T03:00:00Z"), "Information", "Other.Provider", "host-a", "ignored")
        ];

        public Task<IReadOnlyList<WindowsEventLogChannel>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowsEventLogChannel>>(
            [
                new("Application", true, null),
                new("System", true, null),
                new("Security", false, "access_denied"),
                new("Microsoft-Windows-Example/Operational", true, null)
            ]);

        public async IAsyncEnumerable<WindowsEventLogEntry> ReadNewestAsync(string channel, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (string.Equals(channel, "Application", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var entry in ApplicationEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return entry;
                }
            }
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<WindowsEventLogWatchItem> WatchAsync(string channel, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (includeGap) yield return new(null, RequiresResync: true);
            await Task.CompletedTask;
        }
    }
}
