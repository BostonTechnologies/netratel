using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using NetRatel.Akka.Observability;
using Xunit;

namespace NetRatel.Tests.Akka;

[Collection(NetRatelAkkaTelemetryCollection.Name)]
public sealed class NetRatelAkkaTelemetryTests
{
    [Fact]
    public void Meter_ExposesRequiredBoundedRuntimeInstruments()
    {
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NetRatelAkkaTelemetry.MeterName)
            {
                instruments.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        NetRatelAkkaTelemetry.PresenceClientConnected();
        listener.RecordObservableInstruments();

        var expected = new[]
        {
            "akka_presence_connected_total", "akka_presence_disconnected_total",
            "akka_presence_heartbeat_expired_total", "akka_presence_active_clients",
            "akka_telemetry_accepted_total", "akka_telemetry_rejected_total", "akka_telemetry_active_clients",
            "akka_commands_created_total", "akka_commands_started_total", "akka_commands_completed_total",
            "akka_commands_failed_total", "akka_commands_cancelled_total", "akka_commands_active",
            "akka_command_replays_total", "akka_command_duplicates_total", "akka_command_recovery_failures_total",
            "akka_command_inbox_depth", "akka_command_outbox_depth",
            "akka_jobs_started_total", "akka_jobs_completed_total", "akka_jobs_failed_total",
            "akka_jobs_replayed_total", "akka_jobs_active", "akka_jobs_authority_running",
            "akka_terminal_sessions_opened_total", "akka_terminal_sessions_closed_total", "akka_terminal_frames_total",
            "akka_terminal_output_dropped_total", "akka_terminal_transport_registrations_total",
            "akka_terminal_transport_reconnects_total", "akka_terminal_start_replays_total",
            "akka_terminal_opening_timeouts_total", "akka_terminal_compensating_closes_total",
            "akka_terminal_stale_ptys_rejected_total", "akka_terminal_authority_events_total",
            "akka_terminal_authority_failures_total", "akka_terminal_active_sessions",
            "akka_terminal_sessions_active", "akka_terminal_active_transports", "akka_terminal_opened_sessions",
            "akka_terminal_opening_sessions", "akka_terminal_suspended_sessions",
            "akka_remote_support_sessions_total", "akka_remote_support_offers_total",
            "akka_remote_support_answers_total", "akka_remote_support_ice_total",
            "akka_remote_support_authority_events_total", "akka_remote_support_authority_failures_total",
            "akka_remote_support_sessions_completed", "akka_remote_support_active_sessions",
            "akka_filebrowser_requests_total", "akka_filebrowser_completed_total", "akka_filebrowser_failed_total",
            "akka_filebrowser_dropped_total", "akka_filebrowser_authority_events_total",
            "akka_filebrowser_ingress_depth", "akka_filebrowser_high_watermark",
            "akka_signalr_publish_total", "akka_signalr_drop_total", "akka_signalr_timeout_total",
            "akka_signalr_failure_total", "akka_signalr_queue_depth", "akka_signalr_high_watermark",
            "akka_signalr_subscriptions", "akka_signalr_groups", "akka_signalr_memberships",
            "akka_log_sessions_opened_total", "akka_log_sessions_closed_total", "akka_log_batches_total",
            "akka_log_records_total", "akka_log_bytes_total", "akka_log_dropped_records_total",
            "akka_log_resync_requests_total", "akka_log_active_sessions",
            "akka_authority_requests_total", "akka_authority_failures_total",
            "akka_presence_authority_events_total", "akka_telemetry_authority_events_total",
            "akka_commands_authority_events_total", "akka_command_authority_events_total",
            "akka_command_authority_failures_total", "akka_jobs_authority_events_total",
            "akka_jobs_authority_failures_total", "akka_jobs_authority_completed"
        };
        foreach (var name in expected)
        {
            instruments.Should().Contain(name);
        }

        instruments.Should().NotContain(name => name.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        instruments.Should().NotContain("akka_authority_active_paths");
    }

    [Fact]
    public void AuthorityMetrics_EmitOnlyBoundedFeatureAndRuntimeDimensions()
    {
        var measurements = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NetRatelAkkaTelemetry.MeterName &&
                (instrument.Name.StartsWith("akka_authority_", StringComparison.Ordinal) ||
                 instrument.Name.StartsWith("akka_presence_authority_", StringComparison.Ordinal) ||
                 instrument.Name.StartsWith("akka_filebrowser_authority_", StringComparison.Ordinal) ||
                 instrument.Name.StartsWith("akka_remote_support_authority_", StringComparison.Ordinal) ||
                 instrument.Name == "akka_remote_support_sessions_completed"))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var capturedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                capturedTags[tag.Key] = tag.Value;
            }

            measurements.Add((instrument.Name, value, capturedTags));
        });
        listener.Start();

        NetRatelAkkaTelemetry.RecordAuthorityRequest("presence", "akka", "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent("presence", "akka", "Development");
        NetRatelAkkaTelemetry.RecordAuthorityFailure("remote-support", "akka", "Development");
        NetRatelAkkaTelemetry.RemoteSupportAuthoritySessionCompleted("akka", "Development");

        var request = measurements.Should().ContainSingle(item =>
            item.Name == "akka_authority_requests_total" &&
            Equals(item.Tags.GetValueOrDefault("feature"), "presence")).Subject;
        request.Value.Should().Be(1);
        request.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["authority"] = "akka",
            ["feature"] = "presence",
            ["environment"] = "dev"
        });
        measurements.Should().Contain(item => item.Name == "akka_presence_authority_events_total");
        measurements.Should().Contain(item => item.Name == "akka_remote_support_authority_failures_total");
        measurements.Should().Contain(item => item.Name == "akka_remote_support_sessions_completed");

        measurements.Should().OnlyContain(item =>
            item.Tags.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "authority", "environment", "feature" }));
    }

    [Fact]
    public void AuthorityActivity_EmitsOnlyBoundedRuntimeTags()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NetRatelAkkaTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (NetRatelAkkaTelemetry.StartAuthorityActivity("presence", "akka", "heartbeat", "Development"))
        {
        }

        captured.Should().NotBeNull();
        captured!.OperationName.Should().Be("akka.authority.presence");
        captured.GetTagItem("authority").Should().Be("akka");
        captured.GetTagItem("feature").Should().Be("presence");
        captured.GetTagItem("environment").Should().Be("dev");
        captured.GetTagItem("netratel.akka.operation").Should().Be("heartbeat");
        captured.Tags.Should().NotContain(tag =>
            tag.Key.Contains("client", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("tenant", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("migration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ActivitySource_EmitsOnlyBoundedLifecycleTags()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NetRatelAkkaTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (NetRatelAkkaTelemetry.StartActivity("akka.command.lifecycle", "command", "Started"))
        {
        }

        captured.Should().NotBeNull();
        captured!.Source.Name.Should().Be(NetRatelAkkaTelemetry.ActivitySourceName);
        captured.GetTagItem("netratel.akka.surface").Should().Be("command");
        captured.GetTagItem("netratel.akka.lifecycle").Should().Be("Started");
        captured.Tags.Should().NotContain(tag =>
            tag.Key.Contains("client", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("tenant", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("command_id", StringComparison.OrdinalIgnoreCase));
    }
}
