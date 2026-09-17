using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayRegistrationFenceMatrixTests
{
    [Theory]
    [InlineData("command")]
    [InlineData("control")]
    [InlineData("file")]
    [InlineData("job")]
    [InlineData("log")]
    [InlineData("log-query")]
    [InlineData("telemetry")]
    [InlineData("terminal")]
    public async Task EveryRegistry_UsesPresenceFencesAndExactRegistrationLifetime(string gateway)
    {
        var register = CreateRegister(gateway);
        var connection = Guid.NewGuid();
        await using var first = register(connection, 5);
        first.IsCurrent().Should().BeTrue("an empty registry admits the candidate");
        await using var reconnect = register(connection, 5);
        first.Completion.IsCancellationRequested.Should().BeTrue();
        first.IsCurrent().Should().BeFalse();
        reconnect.Id.Should().NotBe(first.Id);
        await first.DisposeAsync();
        reconnect.IsCurrent().Should().BeTrue("late disposal owns only the old instance");

        Action lower = () => register(Guid.NewGuid(), 4);
        lower.Should().Throw<AgentGatewayRegistrationFencedException>();
        Action ambiguous = () => register(Guid.NewGuid(), 5);
        ambiguous.Should().Throw<AgentGatewayRegistrationFencedException>();
        reconnect.IsCurrent().Should().BeTrue();
        reconnect.Completion.IsCancellationRequested.Should().BeFalse();

        await using var newer = register(Guid.NewGuid(), 6);
        reconnect.Completion.IsCancellationRequested.Should().BeTrue();
        await reconnect.DisposeAsync();
        newer.IsCurrent().Should().BeTrue();
        newer.Id.Should().NotBe(reconnect.Id);
    }

    private static Func<Guid, ulong, Registration> CreateRegister(string gateway)
    {
        var client = new ClientKey(47, Guid.NewGuid());
        switch (gateway)
        {
            case "command":
                var commands = new AgentCommandGatewaySessionRegistry();
                return (connection, epoch) =>
                {
                    var r = commands.Register(client, connection, epoch);
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            case "control":
                var controls = new AgentControlSessionRegistry(TimeProvider.System);
                return (connection, epoch) =>
                {
                    var r = controls.Register(client, connection, epoch);
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            case "file":
                var files = new AgentFileGatewaySessionRegistry(new UnusedPresenceRouter());
                return (connection, epoch) =>
                {
                    var r = files.Register(client, connection, epoch);
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            case "job":
                var jobs = new AgentJobGatewaySessionRegistry();
                return (connection, epoch) =>
                {
                    var r = jobs.Register(client, connection, epoch);
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            case "log":
                var logs = new AgentLogGatewaySessionRegistry();
                return (connection, epoch) =>
                {
                    var r = logs.Register(client, connection, epoch, new AgentLogHello());
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            case "log-query":
                var states = new AgentLogGatewaySessionRegistry();
                var queries = new AgentLogGatewayQueryDispatcher(states);
                return (connection, epoch) =>
                {
                    var state = states.Register(client, connection, epoch, new AgentLogHello());
                    try
                    {
                        var r = queries.Register(state);
                        return new(r.RegistrationId, r.CompletionToken, () => r.IsCurrent, () =>
                        {
                            r.Dispose();
                            state.Dispose();
                            return ValueTask.CompletedTask;
                        });
                    }
                    catch
                    {
                        state.Dispose();
                        throw;
                    }
                };
            case "telemetry":
                var demand = new TelemetryInteractiveDemandRegistry(TimeProvider.System, new ConfigurationBuilder().Build());
                var telemetry = new AgentTelemetryGatewaySessionRegistry(demand, new GatewayTelemetryLiveRegistry());
                return (connection, epoch) =>
                {
                    var r = telemetry.Register(client, connection, epoch, false, "test");
                    return new(r.RegistrationId, r.CompletionToken, () => r.IsCurrent, r.DisposeAsync);
                };
            case "terminal":
                var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
                return (connection, epoch) =>
                {
                    var r = terminals.Register(client, connection, epoch, ["bash"]);
                    return Wrap(r, r.RegistrationId, r.CompletionToken, () => r.IsCurrent);
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(gateway));
        }
    }

    private static Registration Wrap(IDisposable registration, Guid id, CancellationToken completion, Func<bool> isCurrent) =>
        new(id, completion, isCurrent, () => { registration.Dispose(); return ValueTask.CompletedTask; });

    private sealed record Registration(Guid Id, CancellationToken Completion, Func<bool> IsCurrent, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }

    private sealed class UnusedPresenceRouter : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
