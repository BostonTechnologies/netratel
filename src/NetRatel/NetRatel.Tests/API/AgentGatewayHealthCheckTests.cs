using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReportsTheNormalAkkaPresenceRuntime()
    {
        var check = new AgentGatewayHealthCheck(new StubPresenceRouter(activeActors: 4));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["mode"].Should().Be("akka");
        result.Data["activeClientActors"].Should().Be(4);
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsUnhealthyWhenTheRequiredPresenceRouteFails()
    {
        var check = new AgentGatewayHealthCheck(new StubPresenceRouter(throwOnProbe: true));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("unavailable");
    }

    [Fact]
    public async Task CheckHealthAsync_PropagatesCallerCancellation()
    {
        var check = new AgentGatewayHealthCheck(new StubPresenceRouter(throwOnProbe: true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => check.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class StubPresenceRouter(
        int activeActors = 0,
        bool throwOnProbe = false) : IClientPresenceRouter
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

        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken)
        {
            if (throwOnProbe)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(new ClientPresenceRouteStatus(activeActors, DateTimeOffset.UtcNow, "akka"));
        }
    }
}
