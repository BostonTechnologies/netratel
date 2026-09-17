using FluentAssertions;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentJobGatewaySessionRegistryTests
{
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
