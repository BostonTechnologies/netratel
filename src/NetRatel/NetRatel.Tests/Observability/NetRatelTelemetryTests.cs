using System.Diagnostics.Metrics;
using FluentAssertions;
using NetRatel.Application.Events;
using NetRatel.Application.Observability;
using Xunit;

namespace NetRatel.Tests.Observability;

public sealed class NetRatelTelemetryTests
{
    [Fact]
    public void RecordOutboxEventRecorded_UsesLowCardinalityTags()
    {
        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NetRatelTelemetry.MeterName &&
                instrument.Name == "netratel_outbox_events_recorded_total")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                snapshot[tag.Key] = tag.Value;
            }

            measurements.Add(snapshot);
        });
        listener.Start();

        NetRatelTelemetry.RecordOutboxEventRecorded(new DomainEvent
        {
            EventType = "DomainEvent.JobRun.Failed",
            Source = "JobTaskBridge",
            CorrelationId = "corr-test",
            TenantId = "tenant-123",
            EntityId = "job-456",
            Severity = "Error"
        });

        measurements.Should().ContainSingle();
        measurements[0].Should().Contain("severity", "Error");
        measurements[0].Should().Contain("source", "JobTaskBridge");
        measurements[0].Should().Contain("event_type", "DomainEvent.JobRun.Failed");
        measurements[0].Should().Contain("status", "Pending");
        measurements[0].Keys.Should().NotContain(["tenant_id", "entity_id", "correlation_id", "user_id"]);
    }
}
