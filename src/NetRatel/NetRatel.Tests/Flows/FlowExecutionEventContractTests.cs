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

    [Fact]
    public void Envelope_round_trip_preserves_large_ids_native_status_and_missing_versus_empty_data()
    {
        const string id = "18446744073709551615";
        var value = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 7,
            FlowExecutionSourceKind.Request, new("42", "Nightly synthetic check", "revision-1"), id, id,
            new(Guid.NewGuid(), "Synthetic workstation"), FlowExecutionOutcome.Succeeded, "Completed",
            DateTimeOffset.Parse("2026-11-05T12:00:00Z"), DateTimeOffset.Parse("2026-11-05T12:00:01Z"));
        var json = JsonSerializer.Serialize(value, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
        var restored = JsonSerializer.Deserialize(json, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.Equal(id, restored.ExecutionId);
        Assert.Equal(id, restored.AttemptId);
        Assert.Equal("Completed", restored.RawStatus);
        Assert.Null(restored.Results);
        Assert.DoesNotContain("results", json);

        var empty = JsonSerializer.Serialize(value with { Results = [] },
            FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
        Assert.Contains("\"results\":[]", empty);
    }
}
