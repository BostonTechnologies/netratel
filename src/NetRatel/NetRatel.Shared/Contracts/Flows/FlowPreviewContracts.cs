using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>An explicitly simulated external result. Absent output is unavailable; JSON null is captured null.</summary>
public sealed record FlowPreviewFixtureDto(Guid NodeId, string Outcome)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Output { get; init; }
}

/// <summary>Schema-two pure preview data. Exactly the graph trigger's source must be supplied.</summary>
public sealed record FlowExecutionDryRunRequest(FlowGraphDto Graph, FlowExecutionEventDto? Input = null,
    FlowEventDataDto? Alert = null, IReadOnlyList<FlowPreviewFixtureDto>? Fixtures = null);

public static class FlowPreviewLimits
{
    public const int MaximumFixtureBytes = 16384;
    public const int MaximumFixtureTotalBytes = 65536;
    public const int MaximumRequestBytes = FlowGraphV2Limits.MaximumGraphBytes +
        FlowExecutionEventLimits.MaximumEnvelopeBytes + MaximumFixtureTotalBytes + 8192;
}
