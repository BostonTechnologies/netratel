namespace NetRatel.Application.Events;

public interface ICorrelationContext
{
    string? Current { get; }
    string GetOrCreate();
}
