namespace NetRatel.Shared.Contracts.Monitoring;

public enum MetricMeasurementQuality { Legacy, Valid, WarmUp, Reset, Missing, Failed, Unsupported, Partial }

public sealed record MetricMeasurementWindow(DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc,
    long ElapsedMicroseconds, DateTimeOffset AttributedEndUtc)
{
    public const int DefaultWindowLimitSeconds = 15;
    public const int MaximumWindowSeconds = 75;
    public const int MaximumClockAdjustmentSeconds = 30;
}
