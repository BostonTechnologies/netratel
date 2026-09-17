using System.Threading.Channels;

namespace NetRatel.Application.Requests;

public sealed record RequestChangedEvent(
    int RequestId,
    string ChangeType,
    DateTimeOffset ChangedAtUtc);

public interface IRequestEventBus
{
    ChannelReader<RequestChangedEvent> Subscribe();
    void Publish(RequestChangedEvent change);
    void Unsubscribe(ChannelReader<RequestChangedEvent> reader);
}
