using System.Collections.Concurrent;
using System.Threading.Channels;
using NetRatel.Application.Notifications;

namespace NetRatel.Infrastructure.Notifications;

public sealed class NetRatelNotificationEventBus : INetRatelNotificationEventBus
{
    private const string GlobalKey = "__global__";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<NetRatelNotificationDto>>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<NetRatelNotificationDto> Subscribe(string? tenantId)
    {
        var key = NormalizeKey(tenantId);
        var bucket = _subscriptions.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Channel<NetRatelNotificationDto>>());
        var id = Guid.NewGuid();

        var channel = Channel.CreateBounded<NetRatelNotificationDto>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        bucket[id] = channel;
        return channel.Reader;
    }

    public void Publish(NetRatelNotificationDto notification)
    {
        PublishToKey(GlobalKey, notification);
        if (!string.IsNullOrWhiteSpace(notification.TenantId))
            PublishToKey(notification.TenantId, notification);
    }

    public void Unsubscribe(string? tenantId, ChannelReader<NetRatelNotificationDto> reader)
    {
        var key = NormalizeKey(tenantId);
        if (!_subscriptions.TryGetValue(key, out var bucket))
            return;

        foreach (var pair in bucket)
        {
            if (!ReferenceEquals(pair.Value.Reader, reader))
                continue;

            if (bucket.TryRemove(pair.Key, out var removed))
                removed.Writer.TryComplete();

            break;
        }

        if (bucket.IsEmpty)
            _subscriptions.TryRemove(key, out _);
    }

    private void PublishToKey(string? key, NetRatelNotificationDto notification)
    {
        var normalized = NormalizeKey(key);
        if (!_subscriptions.TryGetValue(normalized, out var bucket))
            return;

        foreach (var pair in bucket)
        {
            if (!pair.Value.Writer.TryWrite(notification) && bucket.TryRemove(pair.Key, out var removed))
                removed.Writer.TryComplete();
        }

        if (bucket.IsEmpty)
            _subscriptions.TryRemove(normalized, out _);
    }

    private static string NormalizeKey(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? GlobalKey : tenantId.Trim();
}
