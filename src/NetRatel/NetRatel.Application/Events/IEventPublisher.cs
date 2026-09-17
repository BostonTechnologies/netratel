using NetRatel.Application.Notifications;

namespace NetRatel.Application.Events;

public interface IEventPublisher
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken ct);
}
