using System.Threading.Channels;
using FluentAssertions;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Requests;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RequestEventBusTests
{
    [Fact]
    public async Task Publish_Delivers_Request_Changes_To_Subscribers()
    {
        var bus = new RequestEventBus();
        var reader = bus.Subscribe();
        var change = new RequestChangedEvent(42, "completed", DateTimeOffset.UtcNow);

        bus.Publish(change);

        var received = await ReadAsync(reader);
        received.Should().Be(change);
    }

    [Fact]
    public void Unsubscribe_Removes_Subscriber()
    {
        var bus = new RequestEventBus();
        var reader = bus.Subscribe();

        bus.Unsubscribe(reader);

        reader.Completion.IsCompleted.Should().BeTrue();
    }

    private static async Task<RequestChangedEvent> ReadAsync(ChannelReader<RequestChangedEvent> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var hasItem = await reader.WaitToReadAsync(cts.Token);
        hasItem.Should().BeTrue();
        reader.TryRead(out var item).Should().BeTrue();
        return item!;
    }
}
