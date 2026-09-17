using FluentAssertions;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdateStateMachineTests
{
    [Theory]
    [InlineData(ClientUpdateAttemptState.Claimed, ClientUpdateAttemptState.Downloading, true)]
    [InlineData(ClientUpdateAttemptState.Downloading, ClientUpdateAttemptState.Staged, true)]
    [InlineData(ClientUpdateAttemptState.Staged, ClientUpdateAttemptState.Activating, true)]
    [InlineData(ClientUpdateAttemptState.Activating, ClientUpdateAttemptState.RolledBack, true)]
    [InlineData(ClientUpdateAttemptState.GatewayReadmitted, ClientUpdateAttemptState.RollbackUnverified, true)]
    [InlineData(ClientUpdateAttemptState.Claimed, ClientUpdateAttemptState.Accepted, false)]
    [InlineData(ClientUpdateAttemptState.Downloading, ClientUpdateAttemptState.RolledBack, false)]
    [InlineData(ClientUpdateAttemptState.Accepted, ClientUpdateAttemptState.Downloading, false)]
    public void TransitionPolicy_IsConditionalAndMonotonic(
        ClientUpdateAttemptState current,
        ClientUpdateAttemptState next,
        bool expected)
    {
        ClientUpdateAuthorityService.IsAllowedTransition(current, next).Should().Be(expected);
    }
}
