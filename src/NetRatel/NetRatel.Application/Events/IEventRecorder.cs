namespace NetRatel.Application.Events;

public interface IEventRecorder
{
    Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default);
}
