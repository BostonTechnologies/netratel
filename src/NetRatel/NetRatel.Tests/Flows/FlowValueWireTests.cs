using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowValueWireTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Absent_and_captured_null_literals_remain_distinct_after_wire_round_trip()
    {
        var missing = new FlowInputBindingDto(FlowInputSource.Literal, FlowDataType.Json);
        var capturedNull = missing with { Literal = JsonSerializer.SerializeToElement<object?>(null) };
        var missingJson = JsonSerializer.Serialize(missing, Json);
        var nullJson = JsonSerializer.Serialize(capturedNull, Json);
        Assert.DoesNotContain("literal", missingJson);
        Assert.Contains("\"literal\":null", nullJson);
        Assert.Null(JsonSerializer.Deserialize<FlowInputBindingDto>(missingJson, Json)!.Literal);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.Deserialize<FlowInputBindingDto>(nullJson, Json)!.Literal!.Value.ValueKind);

        var eventBinding = new FlowInputBindingDto(FlowInputSource.Event, FlowDataType.Text, "event.executionId");
        var restoredEvent = JsonSerializer.Deserialize<FlowInputBindingDto>(JsonSerializer.Serialize(eventBinding, Json), Json)!;
        Assert.Null(restoredEvent.Literal);
    }

    [Fact]
    public void Captured_null_snapshot_keeps_its_value_and_state_after_history_serialization()
    {
        var snapshot = new FlowTraceSnapshotDto(FlowTraceDataState.Null) { Value = JsonSerializer.SerializeToElement<object?>(null) };
        var serialized = JsonSerializer.Serialize(snapshot, Json);
        var restored = JsonSerializer.Deserialize<FlowTraceSnapshotDto>(serialized, Json)!;
        Assert.Equal(FlowTraceDataState.Null, restored.State);
        Assert.Equal(JsonValueKind.Null, restored.Value!.Value.ValueKind);
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, Json));
    }

}
