using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionEventContractTests
{
    [Theory]
    [InlineData("Success", FlowExecutionOutcome.Succeeded)]
    [InlineData("Completed", FlowExecutionOutcome.Succeeded)]
    [InlineData("Failed", FlowExecutionOutcome.Failed)]
    [InlineData("TimedOut", FlowExecutionOutcome.TimedOut)]
    [InlineData("Cancelled", FlowExecutionOutcome.Cancelled)]
    [InlineData("DeliveryUnknown", FlowExecutionOutcome.Unknown)]
    public void Accepted_terminal_status_is_normalized_without_erasing_unknown(string raw, FlowExecutionOutcome expected)
        => Assert.Equal(expected, FlowExecutionOutcomes.FromAuthoritativeStatus(raw));

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    [InlineData("Disconnected")]
    [InlineData("Stale")]
    [InlineData(null)]
    public void Nonterminal_or_observational_state_does_not_become_a_terminal_event(string? raw)
        => Assert.Null(FlowExecutionOutcomes.FromAuthoritativeStatus(raw));

    [Theory]
    [InlineData("Running", FlowExecutionOutcome.Succeeded, false)]
    [InlineData("Running", FlowExecutionOutcome.Failed, false)]
    [InlineData("Pending", FlowExecutionOutcome.Unknown, false)]
    [InlineData("Stale", FlowExecutionOutcome.Unknown, false)]
    [InlineData("Disconnected", FlowExecutionOutcome.Unknown, false)]
    [InlineData("Failed", FlowExecutionOutcome.Succeeded, false)]
    [InlineData("DeliveryUnknown", FlowExecutionOutcome.Succeeded, false)]
    [InlineData("Cancelled", FlowExecutionOutcome.Succeeded, false)]
    [InlineData("TimedOut", FlowExecutionOutcome.Succeeded, false)]
    [InlineData("Success", FlowExecutionOutcome.Succeeded, true)]
    [InlineData("Completed", FlowExecutionOutcome.Succeeded, true)]
    [InlineData("Completed", FlowExecutionOutcome.Failed, true)]
    [InlineData("Completed", FlowExecutionOutcome.TimedOut, true)]
    [InlineData("Completed", FlowExecutionOutcome.Cancelled, true)]
    [InlineData("Completed", FlowExecutionOutcome.Unknown, true)]
    [InlineData("Failed", FlowExecutionOutcome.Denied, true)]
    [InlineData("Failed", FlowExecutionOutcome.TimedOut, true)]
    [InlineData("Failed", FlowExecutionOutcome.Unknown, true)]
    public void Event_shape_requires_a_terminal_raw_state_and_never_infers_success_from_failure(
        string raw, FlowExecutionOutcome outcome, bool valid)
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Script,
            new("42", "Synthetic native script", "7"), "native-script-request", "native-script-request",
            new(Guid.NewGuid()), outcome, raw, at, at)
        {
            CompletedAtUtc = at,
            ExitCode = raw == "Completed" && outcome == FlowExecutionOutcome.Failed ? 7 : null
        };
        var result = FlowExecutionEventValidator.Validate(value);
        Assert.Equal(valid, result.Valid);
        if (!valid)
            Assert.Contains(outcome == FlowExecutionOutcome.Succeeded && FlowExecutionOutcomes.FromAuthoritativeStatus(raw) is not null
                ? "event_raw_success_mismatch" : "event_raw_status_not_terminal", result.Issues);
    }

    [Fact]
    public void Validation_rejects_unbounded_causation_and_unpinned_replay_without_authorizing_payload()
    {
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Job,
            new("42", "Synthetic check", "revision-1"), "9007199254740993", "9007199254740993", new(Guid.NewGuid()),
            FlowExecutionOutcome.Unknown, "DeliveryUnknown", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Causation = new("root", HopCount: FlowExecutionEventLimits.MaximumHopCount + 1),
            Replay = new(FlowExecutionSourceKind.Job, new("42"), new(null), "https://invalid.example/inputs", "42", "42", true),
            Results = [new("job-result", "42", new string('z', 64))]
        };
        var result = FlowExecutionEventValidator.Validate(value);
        Assert.False(result.Valid);
        Assert.Contains("event_causation_budget_exceeded", result.Issues);
        Assert.Contains("event_replay_material_unpinned", result.Issues);
        Assert.Contains("event_reference_hash_invalid", result.Issues);
    }

    [Fact]
    public void Accepted_native_task_identifiers_up_to_256_characters_are_not_hashed_or_truncated()
    {
        var id = new string('x', 256);
        var definition = new FlowExecutionDefinitionDto("42", "Synthetic script", "7", new string('a', 64));
        var target = new FlowExecutionTargetDto(Guid.NewGuid());
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Script,
            definition, id, id, target, FlowExecutionOutcome.Failed, "Failed",
            DateTimeOffset.Parse("2026-11-05T12:00:00Z"), DateTimeOffset.Parse("2026-11-05T12:00:01Z"))
        { Replay = new(FlowExecutionSourceKind.Script, definition, target, "script-execution:" + id, id, id, true) };
        Assert.True(FlowExecutionEventValidator.Validate(value).Valid);
        var json = JsonSerializer.Serialize(value, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
        Assert.Equal(id, JsonSerializer.Deserialize(json, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!.ExecutionId);
    }

    [Fact]
    public void Replay_uses_stable_target_identity_and_only_pinned_script_or_job_backing_material()
    {
        var target = new FlowExecutionTargetDto(Guid.NewGuid(), "Old display name");
        var definition = new FlowExecutionDefinitionDto("42", "Synthetic job", "revision-1", new string('a', 64));
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Request,
            definition, "42", "42", target, FlowExecutionOutcome.Denied, "Failed",
            DateTimeOffset.Parse("2026-11-05T12:00:00Z"), DateTimeOffset.Parse("2026-11-05T12:00:01Z"))
        {
            Replay = new(FlowExecutionSourceKind.Job, definition, target with { DisplayName = "Renamed endpoint" },
                "job-run:42", "42", "42", true)
        };
        Assert.True(FlowExecutionEventValidator.Validate(value).Valid);
        Assert.Equal("Failed", value.RawStatus);
        Assert.Equal(FlowExecutionOutcome.Denied, value.Outcome);
        Assert.Contains("event_replay_material_unpinned", FlowExecutionEventValidator.Validate(value with
            { Replay = value.Replay with { Definition = definition with { ContentHash = null } } }).Issues);
        Assert.Contains("event_replay_material_unpinned", FlowExecutionEventValidator.Validate(value with
            { Replay = value.Replay with { SourceKind = FlowExecutionSourceKind.Request, InputsReference = "request:42" } }).Issues);
    }

    [Fact]
    public void Required_timestamps_and_well_formed_unicode_keep_event_identity_stable_after_encoding()
    {
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7, FlowExecutionSourceKind.Request,
            new("42", "Synthetic request"), "42", "42", new(Guid.NewGuid()),
            FlowExecutionOutcome.Failed, "Failed", DateTimeOffset.Parse("2026-11-05T12:00:00Z"), DateTimeOffset.Parse("2026-11-05T12:00:01Z"));
        Assert.True(FlowExecutionEventValidator.Validate(value).Valid);
        Assert.Contains("event_timestamp_required", FlowExecutionEventValidator.Validate(value with { OccurredAtUtc = default }).Issues);
        Assert.Contains("event_timestamp_required", FlowExecutionEventValidator.Validate(value with { ObservedAtUtc = default }).Issues);
        var malformed = value with { Definition = value.Definition with { Name = new string((char)0xD800, 1) } };
        Assert.Contains("event_definition_name_invalid", FlowExecutionEventValidator.Validate(malformed).Issues);
    }

    [Fact]
    public void Envelope_round_trip_preserves_large_ids_native_status_and_missing_versus_empty_data()
    {
        const string id = "18446744073709551615";
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7,
            FlowExecutionSourceKind.Request, new("42", "Nightly synthetic check", "revision-1"), id, id,
            new(Guid.NewGuid(), "Synthetic workstation"), FlowExecutionOutcome.Succeeded, "Completed",
            DateTimeOffset.Parse("2026-11-05T12:00:00Z"), DateTimeOffset.Parse("2026-11-05T12:00:01Z"))
        { RequestKind = "synthetic-service" };
        var json = JsonSerializer.Serialize(value, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
        var restored = JsonSerializer.Deserialize(json, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.Equal(id, restored.ExecutionId);
        Assert.Equal(id, restored.AttemptId);
        Assert.Equal("Completed", restored.RawStatus);
        Assert.Equal("synthetic-service", restored.RequestKind);
        Assert.Null(restored.Results);
        Assert.DoesNotContain("results", json);

        var empty = JsonSerializer.Serialize(value with { Results = [] },
            FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
        Assert.Contains("\"results\":[]", empty);
    }
}
