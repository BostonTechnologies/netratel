namespace NetRatel.Shared.Contracts.Flows;

/// <summary>A named typed value offered by authoring, validation and pure/runtime input binding.</summary>
public sealed record FlowBindingFieldDto(string Path, string Name, FlowDataType DataType,
    bool MayBeMissing = true, IReadOnlyList<string>? AllowedValues = null);

public static class FlowBindingCatalogue
{
    private static readonly IReadOnlyList<string> Outcomes =
        [FlowNodeCatalogue.Succeeded, FlowNodeCatalogue.Failed, FlowNodeCatalogue.TimedOut, FlowNodeCatalogue.Cancelled,
         FlowNodeCatalogue.Unknown, FlowNodeCatalogue.Unsupported, FlowNodeCatalogue.Denied];
    public static IReadOnlyList<FlowBindingFieldDto> ExecutionFields { get; } =
    [
        new("event.sourceKind", "Execution source", FlowDataType.Text, false, ["job", "script", "request"]),
        new("event.outcome", "Normalized outcome", FlowDataType.Text, false, Outcomes),
        new("event.rawStatus", "Original status", FlowDataType.Text, false),
        new("event.reason", "Original reason", FlowDataType.Text),
        new("event.reasonCode", "Original reason code", FlowDataType.Text),
        new("event.executionId", "Execution ID", FlowDataType.Text, false),
        new("event.attemptId", "Attempt ID", FlowDataType.Text, false),
        new("event.definition.id", "Definition ID", FlowDataType.Text),
        new("event.definition.name", "Definition name", FlowDataType.Text),
        new("event.definition.revision", "Pinned definition revision", FlowDataType.Text),
        new("event.definition.contentHash", "Pinned content hash", FlowDataType.Text),
        new("event.target.agentId", "Target client ID", FlowDataType.Text),
        new("event.target.displayName", "Target client name", FlowDataType.Text),
        new("event.target.hostName", "Target hostname", FlowDataType.Text),
        new("event.occurredAtUtc", "Durable occurrence time", FlowDataType.Text, false),
        new("event.startedAtUtc", "Execution start time", FlowDataType.Text),
        new("event.completedAtUtc", "Execution completion time", FlowDataType.Text),
        new("event.exitCode", "Exit code", FlowDataType.Number)
    ];
    public static IReadOnlyList<FlowBindingFieldDto> AlertFields { get; } =
    [
        new("alert.numericValue", "Measured value", FlowDataType.Number),
        new("alert.ruleName", "Rule name", FlowDataType.Text, false),
        new("alert.clientName", "Client name", FlowDataType.Text, false),
        new("alert.resource", "Resource", FlowDataType.Text, false),
        new("alert.metric", "Metric", FlowDataType.Text, false),
        new("alert.severity", "Severity", FlowDataType.Text, false),
        new("alert.serviceState", "Service state", FlowDataType.Text),
        new("alert.agentId", "Client ID", FlowDataType.Text, false),
        new("alert.ruleId", "Rule ID", FlowDataType.Text, false),
        new("alert.observedAtUtc", "Observed time", FlowDataType.Text, false)
    ];

    private static readonly IReadOnlyList<FlowBindingFieldDto> ExecutionResultFields =
    [
        new("outcome", "Terminal outcome", FlowDataType.Text, false, Outcomes),
        new("executionId", "Accepted execution ID", FlowDataType.Text),
        new("attemptId", "Execution attempt ID", FlowDataType.Text),
        new("exitCode", "Exit code", FlowDataType.Number),
        new("summary", "Result summary", FlowDataType.Text),
        new("stdout", "Bounded standard output", FlowDataType.Text),
        new("stderr", "Bounded standard error", FlowDataType.Text),
        new("data", "Structured result", FlowDataType.Json)
    ];
    private static readonly IReadOnlyList<FlowBindingFieldDto> InvestigationFields =
    [
        new("summary", "Investigation summary", FlowDataType.Text),
        new("evidence", "Investigation evidence", FlowDataType.Text),
        new("recommendations", "Recommendations", FlowDataType.Text),
        new("status", "Investigation outcome", FlowDataType.Text)
    ];
    private static readonly IReadOnlyList<FlowBindingFieldDto> MappingFields =
    [
        new("title", "Mapped title", FlowDataType.Text, false),
        new("description", "Mapped description", FlowDataType.Text, false),
        new("priority", "Mapped priority", FlowDataType.Number)
    ];
    private static readonly IReadOnlyList<FlowBindingFieldDto> ConditionFields =
        [new("result", "Condition result", FlowDataType.Boolean, false)];

    public static IReadOnlyList<FlowBindingFieldDto> OutputFields(FlowNodeKind kind) => kind switch
    {
        FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob or FlowNodeKind.RerunOriginalExecution => ExecutionResultFields,
        FlowNodeKind.InvestigateWithAi => InvestigationFields,
        FlowNodeKind.MapIncident => MappingFields,
        FlowNodeKind.Condition => ConditionFields,
        _ => []
    };
    public static FlowDataType? TypeForEvent(string path, bool? execution) =>
        (execution is not false ? ExecutionFields.FirstOrDefault(field => field.Path == path)?.DataType : null) ??
        (execution is not true ? AlertFields.FirstOrDefault(field => field.Path == path)?.DataType : null);
    public static FlowDataType? TypeForOutput(FlowNodeKind kind, string path) =>
        OutputFields(kind).FirstOrDefault(field => field.Path == path)?.DataType;
}
