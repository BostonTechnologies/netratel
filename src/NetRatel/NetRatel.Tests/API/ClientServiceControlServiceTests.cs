using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientServiceControlServiceTests
{
    private static readonly ClientKey Key = new(7, Guid.Parse("68530ae8-6cea-468b-bf2c-93ff30725b0d"));
    private static readonly Guid Connection = Guid.Parse("301f588b-bf6f-4f15-aadb-22eb7d35407e");

    [Fact]
    public async Task Intent_is_durable_before_dispatch_and_retries_only_read_same_operation()
    {
        await using var scenario = await Scenario.CreateAsync();
        var request = Request();
        var issuedAt = scenario.Clock.GetUtcNow();
        scenario.Dispatcher.BeforeDispatch = async () =>
        {
            await using var observer = scenario.OpenDb();
            var stored = await observer.ClientServiceOperations.SingleAsync();
            stored.Id.Should().Be(request.OperationId);
            stored.Status.Should().Be("Pending");
            (await observer.OutboxMessages.SingleAsync()).Type.Should().Be("agent.service.requested");
            scenario.Clock.Now = scenario.Clock.Now.AddSeconds(10);
        };
        var response = await scenario.Service.RequestAsync(Key, request, "operator", default);
        response.Status.Should().Be("Dispatched");
        (await scenario.Service.RequestAsync(Key, request, "operator", default)).OperationId.Should().Be(response.OperationId);
        scenario.Dispatcher.Commands.Should().ContainSingle();
        var payload = JsonSerializer.Deserialize<ClientServiceControlPayload>(scenario.Dispatcher.Commands.Single().Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload!.OperationId.Should().Be(request.OperationId);
        payload.ServiceName.Should().Be("worker.service");
        payload.Action.Should().Be(ClientServiceControlAction.Restart);
        payload.IssuedAtUtc.Should().Be(issuedAt);
        payload.ExpiresAtUtc.Should().Be(issuedAt.Add(ClientServiceControlRules.DispatchLifetime));

        var result = new ClientServiceControlResult(request.OperationId, request.ServiceName, request.Platform, request.Action,
            true, ClientServiceState.Running, scenario.Clock.GetUtcNow());
        await scenario.Service.RecordLifecycleAsync(Key, request.OperationId.ToString("N"), CommandLifecycleStatus.Completed,
            scenario.Clock.GetUtcNow(), JsonSerializer.Serialize(result), default);
        var completed = await scenario.Service.ReadAsync(Key, request.OperationId, default);
        completed.Status.Should().Be("Completed");
        completed.Result!.State.Should().Be(ClientServiceState.Running);
        (await scenario.Db.OutboxMessages.CountAsync()).Should().Be(2);
        await scenario.Service.RecordLifecycleAsync(Key, request.OperationId.ToString("N"), CommandLifecycleStatus.Completed,
            scenario.Clock.GetUtcNow(), JsonSerializer.Serialize(result), default);
        (await scenario.Db.OutboxMessages.CountAsync()).Should().Be(2, "a repeated receipt cannot duplicate audit entries");
    }

    [Theory]
    [InlineData("unsupported", "service_control_unsupported")]
    [InlineData("missing", "service_not_in_inventory")]
    [InlineData("disabled", "client_not_active")]
    [InlineData("disabled-status", "client_not_active")]
    public async Task Ineligible_target_does_not_dispatch(string defect, string code)
    {
        await using var scenario = await Scenario.CreateAsync();
        var request = Request();
        if (defect == "unsupported") scenario.Sessions.SetServiceControlSupport(Key, scenario.Registration.RegistrationId, false);
        if (defect == "missing") request = request with { ServiceName = "unknown.service" };
        if (defect == "disabled")
        {
            (await scenario.Db.Agents.SingleAsync()).IsEnabled = false;
            await scenario.Db.SaveChangesAsync();
        }
        if (defect == "disabled-status")
        {
            (await scenario.Db.Agents.SingleAsync()).Status = AgentStatus.Disabled;
            await scenario.Db.SaveChangesAsync();
        }
        var rejected = await Assert.ThrowsAsync<ClientServiceControlException>(() => scenario.Service.RequestAsync(Key, request, "operator", default));
        rejected.Code.Should().Be(code);
        scenario.Dispatcher.Commands.Should().BeEmpty();
        (await scenario.Db.ClientServiceOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_completed_receipt_without_the_expected_observed_state_is_unconfirmed()
    {
        await using var scenario = await Scenario.CreateAsync();
        var request = Request();
        await scenario.Service.RequestAsync(Key, request, "operator", default);
        var inconsistent = new ClientServiceControlResult(request.OperationId, request.ServiceName, request.Platform, request.Action,
            true, ClientServiceState.Stopped, scenario.Clock.GetUtcNow());
        await scenario.Service.RecordLifecycleAsync(Key, request.OperationId.ToString("N"), CommandLifecycleStatus.Completed,
            scenario.Clock.GetUtcNow(), JsonSerializer.Serialize(inconsistent), default);
        var result = await scenario.Service.ReadAsync(Key, request.OperationId, default);
        result.Status.Should().Be("Failed");
        result.FailureCode.Should().Be("service_result_unconfirmed");
        result.MayHaveExecuted.Should().BeTrue();
        result.Result.Should().BeNull();
    }

    [Fact]
    public async Task Correlation_prevents_cross_client_result_and_request_identity_reuse()
    {
        await using var scenario = await Scenario.CreateAsync();
        var request = Request();
        await scenario.Service.RequestAsync(Key, request, "operator", default);
        await scenario.Service.RecordLifecycleAsync(new ClientKey(Key.TenantId, Guid.NewGuid()), request.OperationId.ToString("N"),
            CommandLifecycleStatus.Completed, scenario.Clock.GetUtcNow(), "{}", default);
        (await scenario.Service.ReadAsync(Key, request.OperationId, default)).Status.Should().Be("Dispatched");
        (await Assert.ThrowsAsync<ClientServiceControlException>(() => scenario.Service.RequestAsync(Key,
            request with { Action = ClientServiceControlAction.Stop }, "operator", default))).Code.Should().Be("service_operation_identity_mismatch");
        (await Assert.ThrowsAsync<ClientServiceControlException>(() => scenario.Service.ReadAsync(new ClientKey(8, Key.AgentId), request.OperationId, default)))
            .StatusCode.Should().Be(404);
        scenario.Dispatcher.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Delayed_operation_remains_unknown_and_never_replays()
    {
        await using var scenario = await Scenario.CreateAsync();
        var request = Request();
        await scenario.Service.RequestAsync(Key, request, "operator", default);
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(4);
        var outcome = await scenario.Service.RequestAsync(Key, request, "operator", default);
        outcome.Status.Should().Be("OutcomeUnknown");
        outcome.MayHaveExecuted.Should().BeTrue();
        scenario.Dispatcher.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Capability_is_rechecked_on_the_actual_replacement_command_session()
    {
        var sessions = new AgentCommandGatewaySessionRegistry();
        using var capable = sessions.Register(Key, Connection, 1);
        sessions.SetServiceControlSupport(Key, capable.RegistrationId, true);
        sessions.SupportsServiceControl(Key).Should().BeTrue();
        using var older = sessions.Register(Key, Guid.NewGuid(), 2);
        sessions.SetServiceControlSupport(Key, capable.RegistrationId, true);
        sessions.SupportsServiceControl(Key).Should().BeFalse();
        await Assert.ThrowsAsync<AgentServiceControlUnavailableException>(() => sessions.DispatchAsync(Key,
            new("operation", "correlation", DateTimeOffset.UtcNow, 1, 1, ClientServiceControlRules.TaskType, "{}", 0, Key.TenantId), default));
        older.Reader.TryRead(out _).Should().BeFalse();
    }

    private static ClientServiceControlRequest Request() =>
        new(Guid.NewGuid(), "worker.service", ClientServicePlatform.LinuxSystemd, ClientServiceControlAction.Restart, "Routine service maintenance");

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly DbContextOptions<OrchestratorDbContext> _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public OrchestratorDbContext Db { get; }
        public Clock Clock { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        public AgentCommandGatewaySessionRegistry Sessions { get; } = new();
        public AgentCommandGatewayRegistration Registration { get; }
        public ClientServiceControlService Service { get; }

        private Scenario()
        {
            Db = OpenDb();
            Registration = Sessions.Register(Key, Connection, 1);
            Sessions.SetServiceControlSupport(Key, Registration.RegistrationId, true);
            var router = new Router();
            Service = new(Db, new ClientServicesCoordinator(router, new EmptyClientServiceWatchPolicySource(router, Clock),
                new Telemetry(), Clock, new Presence(), Sessions), Dispatcher, Sessions, Clock);
        }

        public OrchestratorDbContext OpenDb() => new(_options);
        public static async Task<Scenario> CreateAsync()
        {
            var scenario = new Scenario();
            scenario.Db.Agents.Add(new Agent { Id = Key.AgentId, TenantId = Key.TenantId, Name = "fixture-host", IsEnabled = true, Status = AgentStatus.Active });
            await scenario.Db.SaveChangesAsync();
            return scenario;
        }
        public async ValueTask DisposeAsync() { Registration.Dispose(); await Db.DisposeAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 17, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Dispatcher : IAgentCommandAuthorityDispatcher
    {
        public List<(string Id, string Payload)> Commands { get; } = [];
        public Func<Task>? BeforeDispatch { get; set; }
        public bool IsAvailable(ClientKey client) => true;
        public async Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken)
        {
            client.Should().Be(Key);
            taskType.Should().Be(ClientServiceControlRules.TaskType);
            if (BeforeDispatch is not null) await BeforeDispatch();
            Commands.Add((commandId, payloadJson));
        }
    }

    private sealed class Router : IClientServicesRouter
    {
        public Task<ClientServicesState> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(new ClientServicesState(Key,
            1, 1, 1, new(Guid.NewGuid(), 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new("worker.service", "Worker", ClientServicePlatform.LinuxSystemd, ClientServiceState.Running, "active", null, "loaded", "active", "running", "enabled", DateTimeOffset.UtcNow)]),
            null, [], [], 1));
        public Task<ClientServicesState> UpdateWatchPolicyAsync(ClientServiceWatchPolicy policy, CancellationToken cancellationToken) => GetSnapshotAsync(policy.Client, cancellationToken);
        public Task<ClientServicesMessageResult> RecordAsync(RecordClientServicesChunk message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Telemetry : IAgentTelemetryGatewaySessionRegistry
    {
        private readonly Guid _registration = Guid.NewGuid();
        public AgentTelemetryGatewaySessionStatus GetStatus(ClientKey client) => new(true, false, "fixture", 1)
        { SupportsServices = true, ConnectionId = Connection, RegistrationId = _registration };
        public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool supportsDynamicSampling, string agentVersion) => throw new NotSupportedException();
        public void PublishPolicy(ClientKey client, TelemetrySamplingPolicyState policy) => throw new NotSupportedException();
    }

    private sealed class Presence : IClientPresenceRouter
    {
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(Key, ClientPresenceStatus.Online, 1, Connection, 1, null, "fixture", [], null, "akka", true));
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
