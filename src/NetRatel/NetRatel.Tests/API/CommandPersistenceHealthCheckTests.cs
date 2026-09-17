using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.Application.Commands;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class CommandPersistenceHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ReportsBoundedPersistenceDiagnostics()
    {
        var check = new CommandPersistenceHealthCheck(new StubStore(
            new CommandPersistenceDiagnostics(
                12,
                3,
                5,
                2,
                4,
                DateTimeOffset.UtcNow,
                "shadow-only",
                "spacetimedb")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["inboxDepth"].Should().Be(12L);
        result.Data["outboxDepth"].Should().Be(3L);
        result.Data["replayCount"].Should().Be(5UL);
        result.Data["duplicateDetectionCount"].Should().Be(2UL);
        result.Data["recoverySuccessCount"].Should().Be(4UL);
        result.Data["commandAuthority"].Should().Be("spacetimedb");
    }

    [Fact]
    public async Task CheckHealth_IsUnhealthyWhenPersistenceCannotBeRead()
    {
        var check = new CommandPersistenceHealthCheck(new StubStore(
            new InvalidOperationException("database unavailable")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class StubStore : ICommandPersistenceStore
    {
        private readonly CommandPersistenceDiagnostics? _diagnostics;
        private readonly Exception? _exception;

        internal StubStore(CommandPersistenceDiagnostics diagnostics) => _diagnostics = diagnostics;

        internal StubStore(Exception exception) => _exception = exception;

        public Task<CommandPersistenceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) =>
            _exception is null
                ? Task.FromResult(_diagnostics!)
                : Task.FromException<CommandPersistenceDiagnostics>(_exception);

        public Task<CommandPersistenceWriteResult> RecordAsync(
            CommandLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(
            CommandKey command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingOutboxAsync(
            int maxCount,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void RecordRecoverySucceeded()
        {
        }
    }
}
