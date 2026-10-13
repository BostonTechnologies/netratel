using AwesomeAssertions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdateActivityTests
{
    [Fact]
    public void Bounded_notifications_are_tenant_authorized_and_disposal_removes_the_viewer()
    {
        var broker = new ClientUpdateActivityBroker();
        using var canary = broker.Subscribe(7, [7])!;
        using var other = broker.Subscribe(null, [8])!;
        broker.Publish(new(Guid.NewGuid(), 8));
        canary.Reader.TryRead(out _).Should().BeFalse();
        other.Reader.TryRead(out _).Should().BeTrue();
        ClientUpdateActivityNotification last = null!;
        for (var index = 0; index < 100; index++)
        { last = new(Guid.NewGuid(), 7); broker.Publish(last); }
        canary.Reader.TryRead(out var pending).Should().BeTrue();
        pending.Should().Be(last, "the latest bounded invalidation reconciles all committed changes");
        canary.Reader.TryRead(out _).Should().BeFalse();
        other.Reader.TryRead(out _).Should().BeFalse();
        canary.Dispose();
        broker.Publish(new(Guid.NewGuid(), 7));
        canary.Reader.TryRead(out _).Should().BeFalse();
        canary.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Listener_reconnection_invalidates_existing_viewers_for_a_fresh_snapshot()
    {
        var broker = new ClientUpdateActivityBroker();
        using var viewer = broker.Subscribe(7, [7])!;
        broker.SetConnected(true);
        viewer.Reader.TryRead(out _).Should().BeTrue();
        broker.SetConnected(false);
        broker.IsConnected.Should().BeFalse();
        viewer.Reader.TryRead(out _).Should().BeTrue();
        broker.SetConnected(true);
        broker.IsConnected.Should().BeTrue();
        viewer.Reader.TryRead(out _).Should().BeTrue();
    }
}
