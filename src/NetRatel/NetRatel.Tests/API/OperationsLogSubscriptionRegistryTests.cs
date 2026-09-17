using System.Security.Claims;
using FluentAssertions;
using NetRatel.API.Realtime.Operations;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OperationsLogSubscriptionRegistryTests
{
    [Fact]
    public void RemoveConnection_returns_only_sources_with_no_remaining_subscribers_for_agent_side_cleanup()
    {
        var registry = new OperationsLogSubscriptionRegistry();
        var client = new ClientKey(3, Guid.Parse("50cd3e05-a7df-4af2-b41c-b6d84682e89c"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "operator")], "test"));
        const string group = "operations:logs:3:50cd3e05-a7df-4af2-b41c-b6d84682e89c";

        registry.TryRegister("connection", principal).Should().BeTrue();
        registry.TryAdd("connection", group, client, "linux-journal-system", out var firstJoin, out var firstFollow).Should().BeTrue();
        registry.TryAdd("connection", group, client, "linux-file-syslog", out var secondJoin, out var secondFollow).Should().BeTrue();

        firstJoin.Should().BeTrue();
        secondJoin.Should().BeFalse();
        firstFollow.Should().BeTrue();
        secondFollow.Should().BeTrue();
        registry.RemoveConnection("connection").Select(lease => lease.Subscription).Should().BeEquivalentTo(new[]
        {
            new OperationsLogSubscription(group, client, "linux-journal-system"),
            new OperationsLogSubscription(group, client, "linux-file-syslog")
        });
    }

    [Fact]
    public void Removing_one_of_two_subscribers_keeps_the_agent_follow_running()
    {
        var registry = new OperationsLogSubscriptionRegistry();
        var client = new ClientKey(3, Guid.Parse("50cd3e05-a7df-4af2-b41c-b6d84682e89c"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "operator")], "test"));
        const string group = "operations:logs:3:50cd3e05-a7df-4af2-b41c-b6d84682e89c";

        registry.TryRegister("connection-a", principal).Should().BeTrue();
        registry.TryRegister("connection-b", principal).Should().BeTrue();
        registry.TryAdd("connection-a", group, client, "linux-journal-system", out _, out var firstFollow).Should().BeTrue();
        registry.TryAdd("connection-b", group, client, "linux-journal-system", out _, out var secondFollow).Should().BeTrue();

        firstFollow.Should().BeTrue();
        secondFollow.Should().BeFalse();
        registry.Remove("connection-a", group, client, "linux-journal-system", out _, out var stopFirst).Should().BeTrue();
        stopFirst.Should().BeNull();
        registry.Remove("connection-b", group, client, "linux-journal-system", out _, out var stopSecond).Should().BeTrue();
        stopSecond.Should().NotBeNull();
        registry.TryClaimFollowStop(stopSecond!).Should().BeTrue();
    }

    [Fact]
    public void Replacing_a_source_subscription_fences_the_delayed_stop_from_the_previous_connection()
    {
        var registry = new OperationsLogSubscriptionRegistry();
        var client = new ClientKey(9, Guid.Parse("c1759dad-b2f1-4950-87cd-a8e579ac99d0"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "operator")], "test"));
        const string group = "operations:logs:9:c1759dad-b2f1-4950-87cd-a8e579ac99d0";

        registry.TryRegister("previous", principal).Should().BeTrue();
        registry.TryAdd("previous", group, client, "windows-event-application", out _, out var initialFollow).Should().BeTrue();
        initialFollow.Should().BeTrue();
        registry.Remove("previous", group, client, "windows-event-application", out _, out var delayedStop).Should().BeTrue();
        delayedStop.Should().NotBeNull();

        registry.TryRegister("replacement", principal).Should().BeTrue();
        registry.TryAdd("replacement", group, client, "windows-event-application", out _, out var replacementFollow).Should().BeTrue();
        replacementFollow.Should().BeTrue();

        registry.TryClaimFollowStop(delayedStop!).Should().BeFalse();
        registry.Remove("replacement", group, client, "windows-event-application", out _, out var replacementStop).Should().BeTrue();
        registry.TryClaimFollowStop(replacementStop!).Should().BeTrue();
    }
}
