using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Services;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientServiceControlExecutorTests
{
    [Theory]
    [InlineData("*.service", ClientServicePlatform.LinuxSystemd, "invalid_service_selection")]
    [InlineData("worker?.service", ClientServicePlatform.LinuxSystemd, "invalid_service_selection")]
    [InlineData("--bad.service", ClientServicePlatform.LinuxSystemd, "invalid_service_selection")]
    [InlineData("../worker.service", ClientServicePlatform.LinuxSystemd, "invalid_service_selection")]
    [InlineData("worker@.service", ClientServicePlatform.LinuxSystemd, "invalid_service_selection")]
    [InlineData("netratel-client.service", ClientServicePlatform.LinuxSystemd, "agent_service_protected")]
    [InlineData("NetRatel.Client", ClientServicePlatform.Windows, "agent_service_protected")]
    public async Task Rejected_identity_never_reaches_native_control(string name, ClientServicePlatform platform, string expected)
    {
        var inventory = new Inventory();
        var native = new Native();
        var executor = new ClientServiceControlExecutor(inventory, native, platform: platform);
        var result = await executor.ExecuteAsync(Payload(name, platform), default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expected);
        native.Calls.Should().Be(0);
        inventory.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(ClientServicePlatform.LinuxSystemd, "worker@instance.service")]
    [InlineData(ClientServicePlatform.Windows, "Spooler")]
    public async Task Controls_only_confirmed_identity_and_reports_observed_final_state(ClientServicePlatform platform, string name)
    {
        var inventory = new Inventory { Platform = platform, State = ClientServiceState.Running };
        var native = new Native { OnExecute = () => inventory.State = ClientServiceState.Stopped };
        var operation = Payload(name, platform);
        var result = await new ClientServiceControlExecutor(inventory, native, platform: platform).ExecuteAsync(operation, default);
        native.Calls.Should().Be(1);
        native.Operation.Should().Be(operation);
        inventory.Names.Should().Equal(name, name);
        result.Success.Should().BeTrue();
        result.State.Should().Be(ClientServiceState.Stopped);
        result.OperationId.Should().Be(operation.OperationId);
    }

    [Fact]
    public async Task Expired_queued_operation_and_missing_service_never_mutate()
    {
        var inventory = new Inventory { State = ClientServiceState.Missing };
        var native = new Native();
        var executor = new ClientServiceControlExecutor(inventory, native, platform: ClientServicePlatform.LinuxSystemd);
        var operation = Payload();
        var expired = await executor.ExecuteAsync(operation with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
        expired.ErrorCode.Should().Be("service_operation_expired");
        inventory.Calls.Should().Be(0);
        var missing = await executor.ExecuteAsync(operation, default);
        missing.ErrorCode.Should().Be("service_not_found");
        native.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(-11, ClientServiceControlAction.Start, true)]
    [InlineData(-11, ClientServiceControlAction.Stop, true)]
    [InlineData(11, ClientServiceControlAction.Start, true)]
    [InlineData(11, ClientServiceControlAction.Stop, true)]
    [InlineData(-2000, ClientServiceControlAction.Stop, true)]
    [InlineData(-11, ClientServiceControlAction.Stop, false)]
    [InlineData(11, ClientServiceControlAction.Stop, false)]
    [InlineData(-2000, ClientServiceControlAction.Stop, false)]
    public async Task Fresh_operations_tolerate_bounded_clock_skew_and_reach_observed_state(
        int agentClockOffsetMilliseconds, ClientServiceControlAction action, bool includesIssuedAt)
    {
        var clock = new Clock();
        var issuedAt = clock.Now;
        var expectedState = action == ClientServiceControlAction.Stop ? ClientServiceState.Stopped : ClientServiceState.Running;
        var inventory = new Inventory
        {
            Platform = ClientServicePlatform.Windows,
            State = action == ClientServiceControlAction.Stop ? ClientServiceState.Running : ClientServiceState.Stopped
        };
        var native = new Native { OnExecute = () => inventory.State = expectedState };
        var operation = Payload("FixtureService", ClientServicePlatform.Windows) with
        {
            Action = action, IssuedAtUtc = includesIssuedAt ? issuedAt : null,
            ExpiresAtUtc = issuedAt.Add(ClientServiceControlRules.DispatchLifetime)
        };
        var json = JsonSerializer.SerializeToElement(operation, TaskPayloadJsonContext.Default.ClientServiceControlPayload);
        json.TryGetProperty("issuedAtUtc", out _).Should().Be(includesIssuedAt);
        var received = json.Deserialize(TaskPayloadJsonContext.Default.ClientServiceControlPayload)!;
        clock.Now = issuedAt.AddMilliseconds(agentClockOffsetMilliseconds);

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.Windows)
            .ExecuteAsync(received, default);

        result.Success.Should().BeTrue();
        result.State.Should().Be(expectedState);
        native.Calls.Should().Be(1);
        inventory.Names.Should().Equal(operation.ServiceName, operation.ServiceName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Clock_skew_beyond_the_bound_never_observes_or_mutates(bool includesIssuedAt)
    {
        var clock = new Clock();
        var issuedAt = clock.Now.Add(ClientServiceControlRules.IssuanceClockSkew).AddTicks(1);
        var operation = Payload() with
        {
            IssuedAtUtc = includesIssuedAt ? issuedAt : null,
            ExpiresAtUtc = issuedAt.Add(ClientServiceControlRules.DispatchLifetime)
        };
        var inventory = new Inventory();
        var native = new Native();

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(operation, default);

        result.ErrorCode.Should().Be("service_operation_clock_skew");
        inventory.Calls.Should().Be(0);
        native.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Maximum_issuance_lifetime_is_enforced_independently_of_remaining_time()
    {
        var clock = new Clock();
        var issuedAt = clock.Now.AddMinutes(-1);
        var operation = Payload() with
        {
            IssuedAtUtc = issuedAt,
            ExpiresAtUtc = issuedAt.Add(ClientServiceControlRules.DispatchLifetime).AddTicks(1)
        };
        var inventory = new Inventory();
        var native = new Native();

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(operation, default);

        result.ErrorCode.Should().Be("invalid_service_operation_window");
        inventory.Calls.Should().Be(0);
        native.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_issuance_lifetime_never_mutates(int lifetimeSeconds)
    {
        var clock = new Clock();
        var expiry = clock.Now.AddSeconds(1);
        var operation = Payload() with { IssuedAtUtc = expiry.AddSeconds(-lifetimeSeconds), ExpiresAtUtc = expiry };
        var inventory = new Inventory();
        var native = new Native();

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(operation, default);

        result.ErrorCode.Should().Be("invalid_service_operation_window");
        inventory.Calls.Should().Be(0);
        native.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expiry_has_no_clock_skew_grace_period(bool includesIssuedAt)
    {
        var clock = new Clock();
        var operation = Payload() with
        {
            IssuedAtUtc = includesIssuedAt ? clock.Now.Subtract(ClientServiceControlRules.DispatchLifetime) : null,
            ExpiresAtUtc = clock.Now
        };
        var inventory = new Inventory();
        var native = new Native();

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(operation, default);

        result.ErrorCode.Should().Be("service_operation_expired");
        inventory.Calls.Should().Be(0);
        native.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Identity_lookup_cannot_extend_the_dispatch_deadline()
    {
        var clock = new Clock();
        var operation = Payload() with { IssuedAtUtc = clock.Now, ExpiresAtUtc = clock.Now.AddSeconds(1) };
        var inventory = new Inventory
        {
            State = ClientServiceState.Running,
            OnCollect = () => clock.Now = operation.ExpiresAtUtc
        };
        var native = new Native();

        var result = await new ClientServiceControlExecutor(inventory, native, clock, ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(operation, default);

        result.ErrorCode.Should().Be("service_operation_expired");
        inventory.Calls.Should().Be(1);
        native.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Successful_controller_exit_without_expected_state_is_not_success()
    {
        var inventory = new Inventory { State = ClientServiceState.Running };
        var result = await new ClientServiceControlExecutor(inventory, new Native(), platform: ClientServicePlatform.LinuxSystemd)
            .ExecuteAsync(Payload(), default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("service_state_not_reached");
        result.State.Should().Be(ClientServiceState.Running);
    }

    [Fact]
    public async Task Platform_mismatch_and_partial_identity_are_explicit()
    {
        var inventory = new Inventory { Complete = false };
        var native = new Native();
        var executor = new ClientServiceControlExecutor(inventory, native, platform: ClientServicePlatform.LinuxSystemd);
        (await executor.ExecuteAsync(Payload("Spooler", ClientServicePlatform.Windows), default)).ErrorCode.Should().Be("platform_unsupported");
        (await executor.ExecuteAsync(Payload(), default)).ErrorCode.Should().Be("service_identity_unavailable");
        native.Calls.Should().Be(0);
    }

    private static ClientServiceControlPayload Payload(string name = "worker.service", ClientServicePlatform platform = ClientServicePlatform.LinuxSystemd) =>
        new(Guid.NewGuid(), name, platform, ClientServiceControlAction.Stop, DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Native : IClientServiceControlAdapter
    {
        public int Calls { get; private set; }
        public ClientServiceControlPayload? Operation { get; private set; }
        public Action? OnExecute { get; init; }
        public Task<string?> ExecuteAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken)
        {
            Calls++;
            Operation = operation;
            OnExecute?.Invoke();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Inventory : IServiceInventoryCollector
    {
        public int Calls { get; private set; }
        public List<string> Names { get; } = [];
        public ClientServicePlatform Platform { get; init; } = ClientServicePlatform.LinuxSystemd;
        public ClientServiceState State { get; set; }
        public bool Complete { get; init; } = true;
        public Action? OnCollect { get; init; }
        public Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
        {
            Calls++;
            Names.AddRange(names);
            OnCollect?.Invoke();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ServiceCollectionResult(Guid.NewGuid(), ServiceSnapshotKind.Watch,
                Complete ? ServiceCollectionStatus.Complete : ServiceCollectionStatus.Partial, now,
                [new(names.Single(), names.Single(), Platform, State, State.ToString(), null, null, null, null, null, now,
                    AuthoritativeMissing: State == ClientServiceState.Missing)]));
        }
    }
}
