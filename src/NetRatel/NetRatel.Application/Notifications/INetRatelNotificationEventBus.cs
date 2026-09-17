using System.Threading.Channels;

namespace NetRatel.Application.Notifications;

public interface INetRatelNotificationEventBus
{
    ChannelReader<NetRatelNotificationDto> Subscribe(string? tenantId);
    void Publish(NetRatelNotificationDto notification);
    void Unsubscribe(string? tenantId, ChannelReader<NetRatelNotificationDto> reader);
}
