using FluentAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class InteractiveHelperRegistryTests
{
    [Fact]
    public void Registry_KeepsHelpersForDifferentWindowsSessions()
    {
        var registry = new InteractiveHelperRegistry("0.4.86");
        var console = Helper(sessionId: 1, processId: 101, version: "0.4.86+one");
        var rdp = Helper(sessionId: 4, processId: 404, version: "0.4.86+two");

        registry.TryRegister(console, out _, out _).Should().BeTrue();
        registry.TryRegister(rdp, out _, out _).Should().BeTrue();

        registry.Count.Should().Be(2);
        registry.Get(1).Should().BeSameAs(console);
        registry.Get(4).Should().BeSameAs(rdp);
    }

    [Fact]
    public void Registry_RejectsStaleDuplicateWhenCompatibleHelperIsConnected()
    {
        var registry = new InteractiveHelperRegistry("0.4.86");
        var current = Helper(sessionId: 4, processId: 401, version: "0.4.86+current");
        var stale = Helper(sessionId: 4, processId: 402, version: "0.4.85");
        registry.TryRegister(current, out _, out _).Should().BeTrue();

        registry.TryRegister(stale, out var replaced, out var reason).Should().BeFalse();

        replaced.Should().BeNull();
        reason.Should().Be("existing_service_compatible_helper_preferred");
        registry.Get(4).Should().BeSameAs(current);
    }

    [Fact]
    public void Registry_ReplacesStaleDuplicateWithCompatibleHelper()
    {
        var registry = new InteractiveHelperRegistry("0.4.86");
        var stale = Helper(sessionId: 4, processId: 401, version: "0.4.85");
        var current = Helper(sessionId: 4, processId: 402, version: "0.4.86+current");
        registry.TryRegister(stale, out _, out _).Should().BeTrue();

        registry.TryRegister(current, out var replaced, out var reason).Should().BeTrue();

        replaced.Should().BeSameAs(stale);
        reason.Should().BeNull();
        registry.Get(4).Should().BeSameAs(current);
    }

    [Fact]
    public void Registry_RemovesOnlyMatchingConnectionInstance()
    {
        var registry = new InteractiveHelperRegistry("0.4.86");
        var old = Helper(sessionId: 4, processId: 401, version: "0.4.85");
        var current = Helper(sessionId: 4, processId: 402, version: "0.4.86");
        registry.TryRegister(old, out _, out _).Should().BeTrue();
        registry.TryRegister(current, out _, out _).Should().BeTrue();

        registry.Remove(old).Should().BeFalse();
        registry.Get(4).Should().BeSameAs(current);
        registry.Remove(current).Should().BeTrue();
        registry.Get(4).Should().BeNull();
    }

    private static ConnectedUserHelper Helper(int sessionId, int processId, string version) =>
        new(
            sessionId,
            processId,
            version,
            $"user-{sessionId}",
            DateTimeOffset.UtcNow,
            new StreamWriter(Stream.Null));
}
