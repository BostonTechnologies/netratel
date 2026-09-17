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
    public void Meter_ExposesTheRequiredBoundedMigrationInstruments()
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
            "akka_telemetry_accepted_total", "akka_telemetry_rejected_total",
            "akka_telemetry_active_clients", "akka_commands_created_total",
            "akka_commands_started_total", "akka_commands_completed_total",
            "akka_commands_failed_total", "akka_commands_cancelled_total", "akka_commands_active",
            "akka_command_replays_total", "akka_command_duplicates_total",
            "akka_command_recovery_failures_total", "akka_command_inbox_depth", "akka_command_outbox_depth",
            "akka_jobs_started_total", "akka_jobs_completed_total", "akka_jobs_failed_total",
            "akka_jobs_replayed_total", "akka_jobs_active", "akka_terminal_sessions_opened_total",
            "akka_terminal_sessions_closed_total", "akka_terminal_frames_total",
            "akka_terminal_active_sessions", "akka_remote_support_sessions_total",
            "akka_remote_support_offers_total", "akka_remote_support_answers_total",
            "akka_remote_support_ice_total", "akka_remote_support_active_sessions",
            "akka_filebrowser_requests_total", "akka_filebrowser_completed_total",
            "akka_filebrowser_failed_total", "akka_filebrowser_dropped_total",
            "akka_filebrowser_ingress_depth", "akka_filebrowser_high_watermark",
            "akka_signalr_publish_total", "akka_signalr_drop_total", "akka_signalr_timeout_total",
            "akka_signalr_failure_total", "akka_signalr_queue_depth", "akka_signalr_high_watermark",
            "akka_signalr_subscriptions", "akka_signalr_groups", "akka_signalr_memberships",
            "akka_authority_requests_total", "akka_authority_failures_total",
            "akka_authority_fallback_total", "akka_authority_active_paths",
            "akka_presence_authority_events_total", "akka_telemetry_authority_events_total",
            "akka_filebrowser_authority_events_total",
            "akka_commands_authority_events_total", "akka_command_authority_events_total",
            "akka_command_authority_failures_total", "akka_command_authority_active",
            "akka_jobs_authority_events_total", "akka_remote_support_authority_events_total",
            "akka_remote_support_authority_failures_total", "akka_remote_support_authority_active",
            "akka_remote_support_sessions_active", "akka_remote_support_sessions_completed"
        };
        foreach (var name in expected)
        {
            instruments.Should().Contain(name);
        }
    }

    [Fact]
    public void AuthorityMetrics_EmitOnlyRequiredBoundedDimensions()
    {
        var measurements = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NetRatelAkkaTelemetry.MeterName &&
                (instrument.Name.StartsWith("akka_authority_", StringComparison.Ordinal) ||
                 instrument.Name.StartsWith("akka_remote_support_authority_", StringComparison.Ordinal) ||
                 instrument.Name is "akka_remote_support_sessions_active" or "akka_remote_support_sessions_completed"))
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

        NetRatelAkkaTelemetry.ConfigureAuthorityPaths(
            presenceActive: true,
            telemetryActive: true,
            fileBrowseActive: true,
            commandsActive: true,
            jobsActive: false,
            remoteSupportActive: true,
            terminalActive: false,
            signalRActive: false,
            environment: "Development");
        NetRatelAkkaTelemetry.RecordAuthorityRequest(
            "presence", "akka", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "presence", "akka", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "telemetry", "unavailable", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "file-browser", "akka", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "commands", "akka", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "jobs", "unavailable", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.RecordAuthorityEvent(
            "remote-support", "akka", fallbackUsed: false, "Development");
        NetRatelAkkaTelemetry.SetRemoteSupportAuthoritySessionsActive(1);
        NetRatelAkkaTelemetry.RemoteSupportAuthoritySessionCompleted(
            "akka", fallbackUsed: false, "Development");
        listener.RecordObservableInstruments();

        var request = measurements.Should().ContainSingle(item =>
            item.Name == "akka_authority_requests_total" &&
            Equals(item.Tags.GetValueOrDefault("feature"), "presence")).Subject;
        request.Value.Should().Be(1);
        request.Tags.Should().Contain(new Dictionary<string, object?>
        {
            ["authority"] = "akka",
            ["migration_phase"] = "authority-cutover",
            ["feature"] = "presence",
            ["fallback_used"] = "false",
            ["environment"] = "dev"
        });

        measurements.Should().Contain(item =>
            item.Name == "akka_authority_active_paths" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "presence") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_authority_active_paths" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "commands") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_authority_active_paths" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "telemetry") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_authority_active_paths" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "file-browser") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_authority_active_paths" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "remote-support") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_remote_support_authority_events_total" &&
            Equals(item.Tags["feature"], "remote-support") &&
            Equals(item.Tags["authority"], "akka"));
        measurements.Should().Contain(item =>
            item.Name == "akka_remote_support_authority_active" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "remote-support") &&
            Equals(item.Tags["fallback_used"], "false"));
        measurements.Should().Contain(item =>
            item.Name == "akka_remote_support_sessions_active" &&
            item.Value == 1 &&
            Equals(item.Tags["feature"], "remote-support"));
        measurements.Should().Contain(item =>
            item.Name == "akka_remote_support_sessions_completed" &&
            Equals(item.Tags["feature"], "remote-support") &&
            Equals(item.Tags["environment"], "dev"));
    }

    [Fact]
    public void AuthorityActivity_EmitsCutoverProofWithoutIdentityCardinality()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NetRatelAkkaTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (NetRatelAkkaTelemetry.StartAuthorityActivity(
                   "presence", "akka", "heartbeat", fallbackUsed: false, "Development"))
        {
        }

        captured.Should().NotBeNull();
        captured!.OperationName.Should().Be("akka.authority.presence");
        captured.GetTagItem("authority").Should().Be("akka");
        captured.GetTagItem("migration_phase").Should().Be("authority-cutover");
        captured.GetTagItem("feature").Should().Be("presence");
        captured.GetTagItem("fallback_used").Should().Be(false);
        captured.GetTagItem("environment").Should().Be("dev");
        captured.GetTagItem("netratel.akka.operation").Should().Be("heartbeat");
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

        using (NetRatelAkkaTelemetry.StartActivity(
                   "akka.command.lifecycle", "command", "Started"))
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
