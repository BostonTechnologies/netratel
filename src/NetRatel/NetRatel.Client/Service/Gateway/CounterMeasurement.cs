using System;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Client.Service.Gateway;

internal readonly record struct CounterReadClock(DateTimeOffset Utc, long Timestamp, DateTimeOffset AttributedUtc)
{
    public static CounterReadClock Read(TimeProvider time)
    {
        var utc = time.GetUtcNow();
        return new(utc, time.GetTimestamp(), utc);
    }

    public TelemetryMeasurementWindow? MeasureFrom(CounterReadClock previous, TimeProvider time, out CounterReadClock next)
    {
        next = this;
        var microseconds = time.GetElapsedTime(previous.Timestamp, Timestamp).Ticks / TimeSpan.TicksPerMicrosecond;
        if (microseconds is <= 0 or > MetricMeasurementWindow.MaximumWindowSeconds * 1_000_000L) return null;
        var attributed = previous.AttributedUtc.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
        if ((attributed - Utc).Duration() > TimeSpan.FromSeconds(MetricMeasurementWindow.MaximumClockAdjustmentSeconds)) return null;
        next = this with { AttributedUtc = attributed };
        return new()
        {
            StartedAtUtc = TimestampOf(previous.Utc), EndedAtUtc = TimestampOf(Utc),
            ElapsedMicroseconds = (ulong)microseconds, AttributedEndUtc = TimestampOf(attributed)
        };
    }

    private static Timestamp TimestampOf(DateTimeOffset time) => Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(time);
}
