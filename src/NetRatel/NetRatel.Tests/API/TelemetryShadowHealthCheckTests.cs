using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TelemetryShadowHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_ReportsBoundedTelemetryDiagnostics()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var check = new TelemetryShadowHealthCheck(new ProbeTelemetryRouter(
            new ClientTelemetryRouteStatus(
                3,
                10,
                2,
                timestamp,
                timestamp.AddMinutes(-1),
                "local-shadow",
                "spacetimedb")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeTelemetryClients"].Should().Be(3);
        result.Data["acceptedCount"].Should().Be(10UL);
        result.Data["rejectedCount"].Should().Be(2UL);
        result.Data["telemetryAuthority"].Should().Be("spacetimedb");
    }

    [Fact]
    public async Task CheckHealth_IsUnhealthyWhenActorRouteCannotBeProbed()
    {
        var check = new TelemetryShadowHealthCheck(new ProbeTelemetryRouter(
            new InvalidOperationException("probe failed")));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private sealed class ProbeTelemetryRouter : IClientTelemetryRouter
    {
        private readonly ClientTelemetryRouteStatus? _status;
        private readonly Exception? _exception;

        public ProbeTelemetryRouter(ClientTelemetryRouteStatus status)
        {
            _status = status;
        }

        public ProbeTelemetryRouter(Exception exception)
        {
            _exception = exception;
        }

        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            _exception is null
                ? Task.FromResult(_status!)
                : Task.FromException<ClientTelemetryRouteStatus>(_exception);

        public Task<TelemetryMessageResult> RecordAsync(
            RecordTelemetrySnapshot message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientTelemetryState> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientTelemetryReadModelSnapshot(Array.Empty<TelemetrySnapshot>(), DateTimeOffset.UtcNow));
    }
}
