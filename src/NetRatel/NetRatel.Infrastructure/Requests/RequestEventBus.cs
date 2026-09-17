using System.Collections.Concurrent;
using System.Threading.Channels;
using NetRatel.Application.Requests;

namespace NetRatel.Infrastructure.Requests;

public sealed class RequestEventBus : IRequestEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<RequestChangedEvent>> _subscriptions = new();

    public ChannelReader<RequestChangedEvent> Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<RequestChangedEvent>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscriptions[id] = channel;
        return channel.Reader;
    }

    public void Publish(RequestChangedEvent change)
    {
        foreach (var pair in _subscriptions)
        {
            if (!pair.Value.Writer.TryWrite(change) && _subscriptions.TryRemove(pair.Key, out var removed))
            {
                removed.Writer.TryComplete();
            }
        }
    }

    public void Unsubscribe(ChannelReader<RequestChangedEvent> reader)
    {
        foreach (var pair in _subscriptions)
        {
            if (!ReferenceEquals(pair.Value.Reader, reader))
            {
                continue;
            }

            if (_subscriptions.TryRemove(pair.Key, out var removed))
            {
                removed.Writer.TryComplete();
            }

            break;
        }
    }
}
