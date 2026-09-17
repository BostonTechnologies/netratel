using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReportsUnavailableWhenPresenceAuthorityIsDisabled()
    {
        var check = new AgentGatewayHealthCheck(
            new StubPresenceRouter(),
            new NetRatelAkkaMigrationOptions
            {
                Enabled = true,
                PresenceEnabled = true,
                GatewayEnabled = true
            });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["mode"].Should().Be("local-shadow");
        result.Data["presenceAuthority"].Should().Be("unavailable");
        result.Data["operationalPresenceAuthority"].Should().Be("unavailable");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsAkkaAuthorityWhenPresenceAuthorityIsEnabled()
    {
        var check = new AgentGatewayHealthCheck(
            new StubPresenceRouter("akka"),
            new NetRatelAkkaMigrationOptions
            {
                Enabled = true,
                PresenceEnabled = true,
                GatewayEnabled = true,
                PresenceAuthorityEnabled = true
            });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("Akka presence authority");
        result.Data["mode"].Should().Be("akka");
        result.Data["authorityMode"].Should().Be("akka");
        result.Data["presenceAuthority"].Should().Be("akka");
        result.Data["operationalPresenceAuthority"].Should().Be("akka");
        result.Data["presenceAuthorityEnabled"].Should().Be(true);
    }

    private sealed class StubPresenceRouter(string mode = "local-shadow") : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(
            StartGatewayPresenceSession message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PresenceMessageResult> RecordHeartbeatAsync(
            RecordGatewayHeartbeat message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PresenceMessageResult> EndSessionAsync(
            EndGatewayPresenceSession message,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceRouteStatus(0, DateTimeOffset.UtcNow, mode));
    }
}
