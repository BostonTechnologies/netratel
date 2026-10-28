using System.Text.Json;
using Google.Protobuf;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.ComputerUse;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentComputerUseGatewayTests
{
    [Fact]
    public async Task DispatchRequiresTheExactPresenceFenceAndAdvertisedCapability()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, client, connection);
        var command = Command();

        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestComputerUseAsync(client, Guid.NewGuid(), 9, command, TimeSpan.FromSeconds(5), default));
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestComputerUseAsync(new ClientKey(8, client.AgentId), connection, 9, command, TimeSpan.FromSeconds(5), default));
        Assert.False(registration.Reader.TryRead(out _));

        var execution = registry.RequestComputerUseAsync(client, connection, 9, command, TimeSpan.FromSeconds(5), default);
        var dispatch = await registration.Reader.ReadAsync();
        Assert.Equal(client.TenantId, dispatch.TenantId);
        Assert.Equal(connection.ToString("D"), dispatch.ConnectionId);
        Assert.Equal(command, JsonSerializer.Deserialize<ComputerUseCommand>(dispatch.ComputerUseDispatch.CommandJson.Span, ComputerUseContract.JsonOptions));
        var observed = Result(command);
        Assert.True(registration.TryCompleteComputerUse(Completion(observed)));
        Assert.Equal(observed, await execution);
    }

    [Fact]
    public async Task CallerCancellationReachesTheAgentAndDoesNotReplayInput()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, client, connection);
        using var cancellation = new CancellationTokenSource();
        var command = Command() with { Operation = ComputerUseOperations.Act, ObservationId = "fresh-observation",
            Action = new(ComputerUseActionKinds.Type, Text: "bounded input") };
        var execution = registry.RequestComputerUseAsync(client, connection, 9, command, TimeSpan.FromSeconds(5), cancellation.Token);
        var dispatch = await registration.Reader.ReadAsync();
        cancellation.Cancel();

        var revoke = await registration.Reader.ReadAsync();
        Assert.Equal(command.OperationId.ToString("D"), revoke.ComputerUseCancellation.OperationId);
        Assert.True(revoke.Sequence > dispatch.Sequence);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.False(registration.TryCompleteComputerUse(Completion(Result(command))));
        Assert.False(registration.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReplyForAnotherWindowCannotCompleteTheOperation()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, client, connection);
        var command = Command();
        var execution = registry.RequestComputerUseAsync(client, connection, 9, command, TimeSpan.FromSeconds(5), default);
        await registration.Reader.ReadAsync();
        var wrong = Result(command) with { Observation = new("state", DateTimeOffset.UtcNow,
            command.Target with { WindowId = "another-window" }, "") };

        Assert.Throws<InvalidDataException>(() => registration.TryCompleteComputerUse(Completion(wrong)));
        Assert.False(execution.IsCompleted);
        Assert.True(registration.TryCompleteComputerUse(Completion(Result(command))));
        await execution;
    }

    [Fact]
    public async Task ReplacingTheControlStreamFencesPendingAndLateDesktopResults()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var old = Admit(registry, client, connection);
        var command = Command();
        var execution = registry.RequestComputerUseAsync(client, connection, 9, command, TimeSpan.FromSeconds(5), default);
        await old.Reader.ReadAsync();
        using var replacement = Admit(registry, client, connection);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.False(old.TryCompleteComputerUse(Completion(Result(command))));
        Assert.False(replacement.TryCompleteComputerUse(Completion(Result(command))));
        Assert.True(replacement.IsCurrent);
    }

    [Fact]
    public async Task ConcurrentDiscoveryRequestsStayBoundedAndDoNotBreakTheControlStream()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, client, connection);
        var commands = Enumerable.Range(0, 32).Select(_ => Command() with
        {
            Operation = ComputerUseOperations.Capabilities,
            SessionId = Guid.Empty,
            SessionRevision = 0
        }).ToArray();
        var executions = new Task<ComputerUseResult>[commands.Length];
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = commands.Select((command, index) => Task.Run(async () =>
        {
            await start.Task;
            executions[index] = registry.RequestComputerUseAsync(client, connection, 9, command, TimeSpan.FromSeconds(10), default);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(callers);

        var dispatched = 0;
        while (registration.Reader.TryRead(out var frame))
        {
            var command = JsonSerializer.Deserialize<ComputerUseCommand>(frame.ComputerUseDispatch.CommandJson.Span, ComputerUseContract.JsonOptions)!;
            Assert.True(registration.TryCompleteComputerUse(Completion(Result(command))));
            dispatched++;
        }
        Assert.Equal(8, dispatched);
        var results = await Task.WhenAll(executions);
        Assert.Equal(24, results.Count(result => result.Code == "agent_busy"));
        Assert.True(registration.IsCurrent);

        var next = Command();
        var resumed = registry.RequestComputerUseAsync(client, connection, 9, next, TimeSpan.FromSeconds(5), default);
        Assert.True(registration.Reader.TryRead(out _));
        Assert.True(registration.TryCompleteComputerUse(Completion(Result(next))));
        Assert.Equal(ComputerUseStatuses.Observed, (await resumed).Status);
    }

    [Fact]
    public async Task AgentWithoutComputerUseCapabilityCannotReceiveCommands()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = registry.Register(client, connection, 9);
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestComputerUseAsync(client, connection, 9, Command(), TimeSpan.FromSeconds(5), default));
        Assert.False(registration.Reader.TryRead(out _));
    }

    private static AgentControlSessionRegistration Admit(AgentControlSessionRegistry registry, ClientKey client, Guid connection)
    {
        var registration = registry.Register(client, connection, 9, provisional: true);
        registration.SetCapabilities([ComputerUseContract.Capability]);
        Assert.True(registration.Activate());
        return registration;
    }

    private static ComputerUseCommand Command() => new(ComputerUseContract.Version, Guid.NewGuid(), Guid.NewGuid(),
        ComputerUseOperations.Observe, new(3, "test-user-sid-hash", 123, "window-42"), ComputerUseProviders.CuaDriver,
        true, DateTimeOffset.UtcNow.AddMinutes(1), SessionRevision: 2);

    private static ComputerUseResult Result(ComputerUseCommand command) => new(ComputerUseContract.Version,
        command.OperationId, command.SessionId, ComputerUseStatuses.Observed, Provider: command.Provider);

    private static ComputerUseCompletion Completion(ComputerUseResult result) => new()
    {
        OperationId = result.OperationId.ToString("D"),
        ResultJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(result, ComputerUseContract.JsonOptions))
    };
}
