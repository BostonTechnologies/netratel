using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.Application.Commands;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class CommandShadowHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ReportsBoundedCommandDiagnostics()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var check = new CommandShadowHealthCheck(new ProbeCommandRouter(
            new ClientCommandRouteStatus(
                3,
                10,
                2,
                4,
                5,
                timestamp,
                "local-shadow",
                "spacetimedb")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeCommands"].Should().Be(3);
        result.Data["completedCommands"].Should().Be(10UL);
        result.Data["failedCommands"].Should().Be(2UL);
        result.Data["invalidTransitions"].Should().Be(4UL);
        result.Data["staleEvents"].Should().Be(5UL);
        result.Data["commandAuthority"].Should().Be("spacetimedb");
    }

    [Fact]
    public async Task CheckHealth_IsUnhealthyWhenActorRouteCannotBeProbed()
    {
        var check = new CommandShadowHealthCheck(new ProbeCommandRouter(
            new InvalidOperationException("probe failed")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class ProbeCommandRouter : IClientCommandRouter
    {
        private readonly ClientCommandRouteStatus? _status;
        private readonly Exception? _exception;

        public ProbeCommandRouter(ClientCommandRouteStatus status)
        {
            _status = status;
        }

        public ProbeCommandRouter(Exception exception)
        {
            _exception = exception;
        }

        public Task<ClientCommandRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            _exception is null
                ? Task.FromResult(_status!)
                : Task.FromException<ClientCommandRouteStatus>(_exception);

        public Task<CommandMessageResult> RecordAsync(
            RecordCommandLifecycleEvent message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CommandShadowState> GetStateAsync(
            CommandKey command,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
