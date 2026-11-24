using AwesomeAssertions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentJobGatewaySessionRegistryTests
{
    [Fact]
    public async Task Protected_dispatch_requires_an_exact_capable_session_and_uses_its_own_frame()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var owner = new OwnerKey(client, connection, 4);
        var dispatch = new JobGatewayStepDispatch(501, 502, 503, 1, "task", "correlation", TaskKinds.ExecLibraryScript,
            "{}", 1, 3, 3, DateTimeOffset.UtcNow, owner);
        var parameters = new Dictionary<string, string> { ["Canary"] = "synthetic-protected-value" };
        using var older = registry.Register(client, connection, 4);
        await Assert.ThrowsAsync<ProtectedJobDispatchUnavailableException>(() => registry.DispatchProtectedAsync(owner, dispatch, parameters, ct));
        Assert.False(older.Reader.TryRead(out _));

        using var current = registry.RegisterWithCapabilities(client, connection, 4, provisional: false, supportsProtectedParameters: true);
        var foreign = new OwnerKey(new ClientKey(72, client.AgentId), connection, 4);
        await Assert.ThrowsAsync<ProtectedJobDispatchUnavailableException>(() => registry.DispatchProtectedAsync(foreign, dispatch with { ExpectedOwner = foreign }, parameters, ct));
        Assert.False(current.Reader.TryRead(out _));
        await registry.DispatchProtectedAsync(owner, dispatch, parameters, ct);
        var frame = await current.Reader.ReadAsync(ct);
        Assert.Equal(GatewayJobFrame.PayloadOneofCase.ProtectedDispatch, frame.PayloadCase);
        Assert.Null(frame.Dispatch);
        Assert.Equal("{}", frame.ProtectedDispatch.Step.PayloadJson);
        Assert.Equal(parameters["Canary"], frame.ProtectedDispatch.Parameters["Canary"]);
        Assert.Equal(client.TenantId, frame.ProtectedDispatch.Step.TenantId);
        Assert.Equal(connection.ToString("D"), frame.ConnectionId);
        Assert.True(frame.ProtectedDispatch.ExpiresAtUtc.ToDateTimeOffset() > dispatch.RequestedAtUtc);
    }

    [Fact]
    public async Task DispatchAndCancel_AreFencedToTheRegisteredAgentSession()
    {
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        var requestedAt = DateTimeOffset.UtcNow;

        await registry.DispatchAsync(client, new JobGatewayStepDispatch(
            501, 502, 503, 1, "job-task-1", "job-correlation-1", "exec-sh", "{}", 1, 3, 3, requestedAt), CancellationToken.None);
        await registry.CancelAsync(client, 501, "operator_cancelled", CancellationToken.None);

        var dispatch = await registration.Reader.ReadAsync();
        dispatch.Dispatch.JobRunId.Should().Be(501);
        dispatch.Dispatch.JobStepId.Should().Be(502);
        dispatch.Dispatch.JobStepRunId.Should().Be(503);
        dispatch.Dispatch.NextVersion.Should().Be(3);
        dispatch.Dispatch.NextSequence.Should().Be(3);
        dispatch.TenantId.Should().Be(client.TenantId);
        dispatch.ClientId.Should().Be(client.AgentId.ToString("D"));

        var cancel = await registration.Reader.ReadAsync();
        cancel.Cancel.JobRunId.Should().Be(501);
        cancel.Cancel.Reason.Should().Be("operator_cancelled");
        cancel.Sequence.Should().BeGreaterThan(dispatch.Sequence);
    }

    [Fact]
    public void DispatchWithoutAnAdmittedSession_IsExplicitlyUnavailable()
    {
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(72, Guid.NewGuid());

        registry.IsAvailable(client).Should().BeFalse();
        var action = () => registry.DispatchAsync(client, new JobGatewayStepDispatch(
            501, 502, 503, 1, "job-task-2", "job-correlation-2", "exec-sh", "{}", 1, 3, 3, DateTimeOffset.UtcNow), CancellationToken.None);

        action.Should().ThrowAsync<AgentJobGatewaySessionUnavailableException>();
    }

    [Fact]
    public async Task Register_RejectsStaleFences_AndAnOldDisposeCannotRemoveTheCurrentSession()
    {
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var first = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        using var current = registry.Register(client, Guid.NewGuid(), connectionEpoch: 5);

        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 5);
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        first.Dispose();
        await registry.CancelAsync(client, 501, "test", CancellationToken.None);
        (await current.Reader.ReadAsync()).Cancel.JobRunId.Should().Be(501);
    }
    [Fact]
    public async Task ExactReconnect_CompletesOldOwnership_AndLateDisposePreservesReplacement()
    {
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var old = registry.Register(client, connection, 5);
        using var current = registry.Register(client, connection, 5);

        old.IsCurrent.Should().BeFalse();
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        current.RegistrationId.Should().NotBe(old.RegistrationId);
        old.Activate().Should().BeFalse();
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        await registry.CancelAsync(client, 501, "test", CancellationToken.None);
        (await current.Reader.ReadAsync()).Cancel.Should().NotBeNull();
    }

    [Fact]
    public void ProvisionalRegistration_IsInvisibleUntilActivated_AndCannotActivateAfterReplacement()
    {
        var registry = new AgentJobGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var candidate = registry.Register(client, connection, 5, provisional: true);
        candidate.IsCurrent.Should().BeTrue();
        registry.IsAvailable(client).Should().BeFalse();
        candidate.Activate().Should().BeTrue();
        registry.IsAvailable(client).Should().BeTrue();

        using var replacement = registry.Register(client, connection, 5, provisional: true);
        candidate.Activate().Should().BeFalse();
        registry.IsAvailable(client).Should().BeFalse();
        replacement.Activate().Should().BeTrue();
        registry.IsAvailable(client).Should().BeTrue();
    }
}
