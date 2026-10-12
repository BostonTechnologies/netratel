using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentControlUninstallRegistryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Uninstall_requires_admitted_capability_and_the_exact_target_connection()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var key = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var request = Request();
        using var provisional = registry.Register(key, connection, 5, provisional: true);
        provisional.SetCapabilities(["uninstall-v1"]);
        registry.GetAvailability(key).Should().BeNull();
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestUninstallAsync(key, connection, 5, request, Deadline, default));
        provisional.Activate().Should().BeTrue();
        registry.GetAvailability(key)!.Capabilities.Should().Equal("uninstall-v1");
        Action changeCapabilities = () => provisional.SetCapabilities([]);
        changeCapabilities.Should().Throw<InvalidOperationException>();

        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestUninstallAsync(key, Guid.NewGuid(), 5, request, Deadline, default));
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestUninstallAsync(key, connection, 6, request, Deadline, default));
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestUninstallAsync(new ClientKey(8, key.AgentId), connection, 5, request, Deadline, default));
        provisional.Reader.TryRead(out _).Should().BeFalse();

        var oldClient = new ClientKey(7, Guid.NewGuid());
        using var old = registry.Register(oldClient, connection, 1);
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestUninstallAsync(oldClient, connection, 1, request, Deadline, default));
        old.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Identical_dispatches_share_one_command_and_acknowledgements_complete_only_their_operation()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var key = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, key, connection, 5);
        var first = Request();
        var second = Request();
        var pendingFirst = registry.RequestUninstallAsync(key, connection, 5, first, Deadline, default);
        var duplicateFirst = registry.RequestUninstallAsync(key, connection, 5, first.Clone(), Deadline, default);
        var firstFrame = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        firstFrame.TenantId.Should().Be(key.TenantId);
        firstFrame.ClientId.Should().Be(key.AgentId.ToString("D"));
        firstFrame.ConnectionId.Should().Be(connection.ToString("D"));
        firstFrame.ConnectionEpoch.Should().Be(5);
        firstFrame.UninstallRequest.Should().Be(first);
        registration.Reader.TryRead(out _).Should().BeFalse("an identical retry shares its outstanding acknowledgement");

        var pendingSecond = registry.RequestUninstallAsync(key, connection, 5, second, Deadline, default);
        var secondFrame = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        secondFrame.UninstallRequest.OperationId.Should().Be(second.OperationId);
        secondFrame.Sequence.Should().BeGreaterThan(firstFrame.Sequence);
        registration.TryCompleteUninstall(new() { OperationId = Guid.NewGuid().ToString("D"), Accepted = true }).Should().BeFalse();
        registration.TryCompleteUninstall(new() { OperationId = second.OperationId, Accepted = false, FailureCode = "launch_rejected" }).Should().BeTrue();
        (await pendingSecond).Accepted.Should().BeFalse();
        pendingFirst.IsCompleted.Should().BeFalse();
        duplicateFirst.IsCompleted.Should().BeFalse();
        registration.TryCompleteUninstall(new() { OperationId = first.OperationId, Accepted = true }).Should().BeTrue();
        (await pendingFirst).OperationId.Should().Be(first.OperationId);
        (await duplicateFirst).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Changed_payload_cannot_replace_a_pending_operation()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var key = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, key, connection, 5);
        var original = Request();
        var pending = registry.RequestUninstallAsync(key, connection, 5, original, Deadline, default);
        await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var changed = original.Clone();
        changed.CompletionToken = new string('B', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RequestUninstallAsync(key, connection, 5, changed, Deadline, default));
        pending.IsCompleted.Should().BeFalse();
        registration.Reader.TryRead(out _).Should().BeFalse();
        registration.TryCompleteUninstall(new() { OperationId = original.OperationId, Accepted = true }).Should().BeTrue();
        (await pending).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Replacement_cancels_old_dispatch_and_old_registration_cannot_acknowledge_new_work()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var key = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var old = Admit(registry, key, connection, 5);
        var request = Request();
        var oldPending = registry.RequestUninstallAsync(key, connection, 5, request, Deadline, default);
        await old.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        using var current = Admit(registry, key, connection, 5);
        var acknowledgement = new UninstallAcknowledgement { OperationId = request.OperationId, Accepted = true };
        old.TryCompleteUninstall(acknowledgement).Should().BeFalse();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldPending);
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        current.Reader.TryRead(out _).Should().BeFalse("replacement does not replay an old session's destructive command");

        var retry = registry.RequestUninstallAsync(key, connection, 5, request, Deadline, default);
        await current.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        old.TryCompleteUninstall(acknowledgement).Should().BeFalse();
        retry.IsCompleted.Should().BeFalse();
        current.TryCompleteUninstall(acknowledgement).Should().BeTrue();
        (await retry).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_ping_and_uninstall_frames_have_strictly_increasing_wire_sequences()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var key = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var registration = Admit(registry, key, connection, 5);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(async index =>
        {
            await start.Task;
            if (index % 2 == 0) await registry.RequestPingAsync(key, Deadline, default);
            else await registry.RequestUninstallAsync(key, connection, 5, Request(), Deadline, default);
        }).ToArray();
        start.SetResult();
        ulong previous = 0;
        for (var index = 0; index < requests.Length; index++)
        {
            var frame = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            frame.Sequence.Should().BeGreaterThan(previous);
            previous = frame.Sequence;
            if (frame.PayloadCase == GatewayControlFrame.PayloadOneofCase.PingRequest)
                registration.TryCompletePing(Guid.Parse(frame.PingRequest.RequestId), DateTimeOffset.UtcNow).Should().BeTrue();
            else
                registration.TryCompleteUninstall(new() { OperationId = frame.UninstallRequest.OperationId, Accepted = true }).Should().BeTrue();
        }
        await Task.WhenAll(requests);
    }

    private static AgentControlSessionRegistration Admit(AgentControlSessionRegistry registry, ClientKey key, Guid connection, ulong epoch)
    {
        var registration = registry.Register(key, connection, epoch, provisional: true);
        registration.SetCapabilities(["uninstall-v1"]);
        registration.Activate().Should().BeTrue();
        return registration;
    }

    private static UninstallRequest Request() => new()
    {
        OperationId = Guid.NewGuid().ToString("D"), CompletionToken = new string('A', 64),
        ExpectedKeyFingerprint = "fixture-device-key", StartDeadlineUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5))
    };
}
