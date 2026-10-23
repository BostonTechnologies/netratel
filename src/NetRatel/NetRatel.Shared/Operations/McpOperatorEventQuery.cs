namespace NetRatel.Shared.Operations;

/// <summary>Exact metadata filters for the bounded operator event list.</summary>
public sealed record McpOperatorEventQuery(string? EventType = null, string? CorrelationId = null, string? Status = null)
{
    public const int MaximumFilterLength = 256;

    public bool HasFilters => EventType is not null || CorrelationId is not null || Status is not null;

    public bool IsValid => IsBoundedText(EventType) && IsBoundedText(CorrelationId)
        && Status is null or "Pending" or "Published" or "Failed" or "Disabled";

    private static bool IsBoundedText(string? value) => value is null
        || (value.Length is > 0 and <= MaximumFilterLength && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl));
}
