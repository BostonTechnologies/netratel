namespace NetRatel.Application.Events;

public interface IEventRecorder
{
    Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default);
    // Failure notifications must not flush the failed request's tracked mutations.
    Task RecordIndependentAsync(DomainEvent domainEvent, CancellationToken ct = default) => RecordAsync(domainEvent, ct);
}
