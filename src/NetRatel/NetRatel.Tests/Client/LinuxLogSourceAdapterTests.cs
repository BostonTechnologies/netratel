using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Logging.Linux;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxLogSourceAdapterTests
{
    [Fact]
    public async Task DiscoverAsync_keeps_runtime_and_fixed_file_logs_when_journalctl_is_missing()
    {
        var runner = new FailingLinuxRunner("journalctl", new Win32Exception(2));
        var adapter = new LinuxLogSourceAdapter(runner);

        var sources = await adapter.DiscoverAsync(CancellationToken.None);

        runner.Calls.Should().Equal("journalctl");
        sources.Should().OnlyContain(source => source.Kind == "file" && source.Available);
        sources.Select(source => source.SourceId).Should().OnlyContain(id =>
            id == "linux-file-syslog" || id == "linux-file-messages" || id == "linux-file-authentication" ||
            id == "linux-file-secure" || id == "linux-file-kernel");
        var page = await adapter.ReadHistoryAsync(new ClientLogQuery("linux-journal-system", null, 100, null, null, [], [], null), CancellationToken.None);
        page.ErrorCode.Should().Be("source_unavailable");

        var gateway = new AgentLogGatewayClient(new GatewayClientOptions { Endpoint = "https://gateway.test" });
        var session = new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid());
        var hello = gateway.CreateHello(session, Guid.NewGuid(), sources);
        hello.Hello.Sources[0].SourceId.Should().Be("netratel-runtime");
        hello.Hello.Sources[0].Available.Should().BeTrue();
        hello.TenantId.Should().Be(session.TenantId);
        hello.ClientId.Should().Be(session.AgentId.ToString("D"));
        hello.ConnectionId.Should().Be(session.ConnectionId.ToString("D"));
        hello.ConnectionEpoch.Should().Be(session.ConnectionEpoch);
    }

    [Fact]
    public async Task DiscoverAsync_keeps_fixed_journal_sources_when_only_systemctl_is_missing()
    {
        var runner = new FailingLinuxRunner("systemctl", new Win32Exception(2));
        var adapter = new LinuxLogSourceAdapter(runner);

        var sources = await adapter.DiscoverAsync(CancellationToken.None);

        runner.Calls.Should().Equal("journalctl", "systemctl");
        sources.Where(source => source.Kind == "journal").Select(source => source.SourceId).Should().Equal(
            "linux-journal-system", "linux-journal-kernel", "linux-journal-authentication");
        var page = await adapter.ReadHistoryAsync(new ClientLogQuery("linux-journal-system", null, 25, null, null, [], [], null), CancellationToken.None);
        page.Records.Should().ContainSingle();
        page.ErrorCode.Should().BeNull();
    }

    [Theory]
    [InlineData("journalctl", 13)]
    [InlineData("journalctl", 8)]
    [InlineData("systemctl", 13)]
    public async Task DiscoverAsync_propagates_native_failures_other_than_missing_executables(string executable, int nativeError)
    {
        var failure = new Win32Exception(nativeError);
        var adapter = new LinuxLogSourceAdapter(new FailingLinuxRunner(executable, failure));

        var actual = await Assert.ThrowsAsync<Win32Exception>(() => adapter.DiscoverAsync(CancellationToken.None));

        actual.Should().BeSameAs(failure);
    }

    [Theory]
    [InlineData("journalctl")]
    [InlineData("systemctl")]
    public async Task DiscoverAsync_preserves_requested_cancellation_when_an_executable_is_missing(string executable)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new LinuxLogSourceAdapter(new FailingLinuxRunner(executable, new Win32Exception(2), cancellation.Cancel));

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DiscoverAsync(cancellation.Token));

        actual.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_start_a_discovery_command_after_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeLinuxRunner();
        var adapter = new LinuxLogSourceAdapter(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.DiscoverAsync(cancellation.Token));

        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task FollowFileAsync_reports_truncation_before_delivering_the_new_file_contents()
    {
        var directory = Directory.CreateTempSubdirectory("netratel-log-source-");
        var path = Path.Combine(directory.FullName, "fixture.log");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 1024) + "\n");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var follow = LinuxLogSourceAdapter.FollowFileAsync("linux-file-messages", path,
                new ClientLogQuery("linux-file-messages", null, 100, null, null, [], [], null), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            // MoveNext captures the genuine initial offset before its provider polling interval.
            var next = follow.MoveNextAsync().AsTask();
            await File.WriteAllTextAsync(path, string.Empty, cancellation.Token);
            Assert.True(await next);
            Assert.True(follow.Current.ResyncRequired);
            Assert.Null(follow.Current.Record);

            await File.AppendAllTextAsync(path, "post-truncation-marker\n", cancellation.Token);
            Assert.True(await follow.MoveNextAsync());
            Assert.False(follow.Current.ResyncRequired);
            Assert.Equal("post-truncation-marker", follow.Current.Record!.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

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

    private sealed class FailingLinuxRunner(string executableToFail, Win32Exception failure, Action? beforeFailure = null) : ILinuxLogCommandRunner
    {
        private readonly FakeLinuxRunner _available = new();
        public List<string> Calls { get; } = [];

        public Task<LinuxCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(executable);
            if (executable == executableToFail)
            {
                beforeFailure?.Invoke();
                throw failure;
            }
            return _available.RunAsync(executable, arguments, cancellationToken);
        }

        public IAsyncEnumerable<string> FollowAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            _available.FollowAsync(executable, arguments, cancellationToken);
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
