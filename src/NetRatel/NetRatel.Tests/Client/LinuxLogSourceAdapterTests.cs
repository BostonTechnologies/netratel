using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Logging.Linux;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxLogSourceAdapterTests
{
    [Fact]
    public async Task DiscoverAsync_advertises_only_fixed_journal_sources_and_safe_units()
    {
        var runner = new FakeLinuxRunner();
        var adapter = new LinuxLogSourceAdapter(runner);

        var sources = await adapter.DiscoverAsync(CancellationToken.None);

        sources.Select(source => source.SourceId).Should().Contain(["linux-journal-system", "linux-journal-kernel", "linux-journal-authentication"]);
        sources.Should().ContainSingle(source => source.DisplayName == "NetRatel Client Service");
        sources.Select(source => source.SourceId).Should().NotContain(source => source.Contains("unsafe", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadHistoryAsync_uses_typed_arguments_and_keeps_journal_cursor_opaque()
    {
        var runner = new FakeLinuxRunner();
        var adapter = new LinuxLogSourceAdapter(runner);
        await adapter.DiscoverAsync(CancellationToken.None);

        var page = await adapter.ReadHistoryAsync(new ClientLogQuery(
            "linux-journal-system", null, 25, null, null, ["Error"], [], "gateway"), CancellationToken.None);

        page.Records.Should().ContainSingle();
        page.Records[0].Cursor.Should().Be("s=opaque-journal-cursor");
        page.Records[0].Severity.Should().Be("Error");
        runner.Calls.Last().Arguments.Should().Contain("--priority=3");
        runner.Calls.Last().Arguments.Should().NotContain(argument => argument.Contains("gateway", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadHistoryAsync_applies_the_typed_category_filter()
    {
        var runner = new FakeLinuxRunner();
        var adapter = new LinuxLogSourceAdapter(runner);
        await adapter.DiscoverAsync(CancellationToken.None);

        var page = await adapter.ReadHistoryAsync(new ClientLogQuery(
            "linux-journal-system", null, 25, null, null, [], [], null, Categories: ["netratel-client.service"]), CancellationToken.None);

        page.Records.Should().ContainSingle();
        page.Records[0].Category.Should().Be("netratel-client.service");
    }

    [Fact]
    public async Task ReadHistoryAsync_returns_a_typed_before_cursor_for_follow_up_pages()
    {
        var runner = new FakeLinuxRunner();
        var adapter = new LinuxLogSourceAdapter(runner);
        await adapter.DiscoverAsync(CancellationToken.None);

        var page = await adapter.ReadHistoryAsync(new ClientLogQuery(
            "linux-journal-system", null, 1, null, null, [], [], null), CancellationToken.None);

        page.HasMore.Should().BeTrue();
        page.PreviousCursor.Should().Be("before:s=newest");

        await adapter.ReadHistoryAsync(new ClientLogQuery(
            "linux-journal-system", page.PreviousCursor, 1, null, null, [], [], null), CancellationToken.None);

        runner.Calls.Last().Arguments.Should().Contain("--cursor=s=newest");
    }

    private sealed class FakeLinuxRunner : ILinuxLogCommandRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<LinuxCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments));
            if (executable == "systemctl") return Task.FromResult(new LinuxCommandResult(0, ["netratel-client.service loaded active running", "unsafe;unit.service loaded active running"], ""));
            if (arguments.Contains("--lines=26"))
            {
                return Task.FromResult(new LinuxCommandResult(0,
                    ["{\"__CURSOR\":\"s=opaque-journal-cursor\",\"__REALTIME_TIMESTAMP\":\"1720000000000000\",\"PRIORITY\":\"3\",\"_SYSTEMD_UNIT\":\"netratel-client.service\",\"MESSAGE\":\"gateway failed\"}"], ""));
            }
            if (arguments.Contains("--lines=2"))
            {
                return Task.FromResult(new LinuxCommandResult(0,
                    ["{\"__CURSOR\":\"s=newest\",\"__REALTIME_TIMESTAMP\":\"1720000000000000\",\"PRIORITY\":\"6\",\"_SYSTEMD_UNIT\":\"netratel-client.service\",\"MESSAGE\":\"newest\"}",
                     "{\"__CURSOR\":\"s=older\",\"__REALTIME_TIMESTAMP\":\"1710000000000000\",\"PRIORITY\":\"6\",\"_SYSTEMD_UNIT\":\"netratel-client.service\",\"MESSAGE\":\"older\"}"], ""));
            }
            return Task.FromResult(new LinuxCommandResult(0, [], ""));
        }

        public async IAsyncEnumerable<string> FollowAsync(string executable, IReadOnlyList<string> arguments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
