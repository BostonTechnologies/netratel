using AwesomeAssertions;
using NetRatel.Client.Service.Services;
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
        public Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
        {
            Calls++;
            Names.AddRange(names);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ServiceCollectionResult(Guid.NewGuid(), ServiceSnapshotKind.Watch,
                Complete ? ServiceCollectionStatus.Complete : ServiceCollectionStatus.Partial, now,
                [new(names.Single(), names.Single(), Platform, State, State.ToString(), null, null, null, null, null, now,
                    AuthoritativeMissing: State == ClientServiceState.Missing)]));
        }
    }
}
