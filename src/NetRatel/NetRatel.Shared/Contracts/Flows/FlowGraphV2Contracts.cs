using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Schema two is opt-in. Schema one limits and serialized members remain unchanged.</summary>
public static class FlowGraphV2Limits
{
    public const int SchemaVersion = 2;
    public const int MaximumNodes = 32;
    public const int MaximumEdges = 64;
    public const int MaximumGraphBytes = 131072;
    public const int MaximumEventBytes = 24576;
    public const int MaximumActionBytes = 49152;
    public const int MaximumValueDepth = 16;
    public const int MaximumInvestigationInputBytes = 4096;
    public const int MaximumRoutePoints = 8;
    public const int MaximumBindings = 32;
    public const int MaximumBindingBytes = 4096;
    public const int MaximumPathLength = 256;
    public const int MaximumSelectedDefinitions = 128;
    public const int MaximumRequestSourceSystems = 32;
    public const int MaximumOperationMinutes = 1440;
    public const int MaximumRecoveryAttempts = 3;
}

public enum FlowExecutionScope { AllInTenant = 1, SelectedDefinitions = 2 }
public enum FlowInputSource { Event = 1, NodeOutput = 2, Literal = 3, SecretReference = 4 }
public enum FlowDataType { Text = 1, Number = 2, Boolean = 3, Json = 4, SecretReference = 5 }
public enum FlowPortDirection { Input = 1, Output = 2 }

/// <summary>Values are bound through this allowlisted data contract, never CLR names or executable expressions.</summary>
public sealed record FlowInputBindingDto(FlowInputSource Source, FlowDataType DataType, string? Path = null,
    Guid? NodeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(FlowNullableJsonElementConverter))] JsonElement? Literal = null);
public sealed record FlowParameterDefinitionDto(string Name, FlowDataType DataType, bool Required = false);
public sealed record FlowPredicateDto(FlowInputBindingDto Input, FlowComparison Comparison, JsonElement Value);
public sealed record FlowExecutionDefinitionReferenceDto(FlowExecutionSourceKind SourceKind, string Id)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

public sealed record FlowExecutionTriggerSettingsDto(IReadOnlyList<FlowExecutionSourceKind> Sources,
    IReadOnlyList<FlowExecutionOutcome> Outcomes, FlowExecutionScope Scope)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DefinitionIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FlowExecutionDefinitionReferenceDto>? DefinitionReferences { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? AgentIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequestKinds { get; init; }
    public bool AggregateCorrelatedEvents { get; init; } = true;
}

/// <summary>Publish must resolve the definition and parameter schema under current authority and pin a server-owned snapshot.</summary>
public sealed record FlowExecutionActionSettingsDto(FlowExecutionSourceKind SourceKind, FlowExecutionDefinitionDto Definition)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowExecutionTargetDto? Target { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PinnedSnapshotReference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FlowParameterDefinitionDto>? Parameters { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobTargetDefinition? JobTarget { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobExecutionPolicy? JobExecutionPolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NetRatel.Shared.ClientEnvironment? JobTargetEnvironment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlowJobTargetBindingDto? TargetBinding { get; init; }
    public int TimeoutMinutes { get; init; } = 60;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NetRatel.Shared.ClientEnvironment Environment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; init; }
}

/// <summary>One typed value mapping, evaluated as data under the existing bounded Flow binding contract.</summary>
public sealed record FlowJobTargetBindingDto(JobRuntimeTargetKind Kind, FlowInputBindingDto Value, bool AllOf = false);

public sealed record FlowRerunSettingsDto(int MaximumAttempts = 1, int TimeoutMinutes = 60);

/// <summary>Finite world coordinates are presentation data; they never add an executable node or choose an outcome.</summary>
public sealed record FlowEdgeRouteDto(IReadOnlyList<FlowPositionDto> Points);

/// <summary>Outcome selects control flow. PayloadSchema identifies the separately carried immutable event/output context.</summary>
public sealed record FlowPortDescriptorDto(string Id, string Name, FlowPortDirection Direction,
    string PayloadSchema, string ValueLabel);
public sealed record FlowNodeDescriptorDto(FlowNodeKind Kind, string Name, string Category,
    IReadOnlyList<FlowPortDescriptorDto> Ports);

public static class FlowNodeCatalogue
{
    public const string ContextSchema = "flow-context/v2";
    public const string Input = "in";
    public const string Event = "event";
    public const string Result = "result";
    public const string True = "true";
    public const string False = "false";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";
    public const string Unsupported = "unsupported";
    public const string Denied = "denied";

    private static FlowPortDescriptorDto In() => new(Input, "Context", FlowPortDirection.Input, ContextSchema, "Event and prior outputs");
    private static FlowPortDescriptorDto Out(string id, string name, string value) => new(id, name, FlowPortDirection.Output, ContextSchema, value);
    private static IReadOnlyList<FlowPortDescriptorDto> Outcomes(string value) =>
        [In(), Out(Succeeded, "Succeeded", value), Out(Failed, "Failed", value), Out(TimedOut, "Timed out", value),
         Out(Cancelled, "Cancelled", value), Out(Unknown, "Unknown", value), Out(Unsupported, "Unsupported", value), Out(Denied, "Denied", value)];

    public static IReadOnlyList<FlowNodeDescriptorDto> Nodes { get; } =
    [
        new(FlowNodeKind.AlertRaised, "Alert raised", "Events", [Out(Event, "Alert event", "Monitoring event")]),
        new(FlowNodeKind.ExecutionEvent, "Execution event", "Events", [Out(Event, "Execution event", "Execution lifecycle event")]),
        new(FlowNodeKind.Condition, "Condition", "Logic", [In(), Out(True, "True", "Condition result"), Out(False, "False", "Condition result")]),
        new(FlowNodeKind.MapIncident, "Map fields", "Data", [In(), Out(Result, "Mapped fields", "Incident fields")]),
        new(FlowNodeKind.RunLibraryScript, "Run library script", "Scripts/Jobs", Outcomes("Script execution result")),
        new(FlowNodeKind.RunJob, "Run job", "Scripts/Jobs", Outcomes("Job execution result")),
        new(FlowNodeKind.RerunOriginalExecution, "Rerun original execution", "Scripts/Jobs", Outcomes("Linked rerun result")),
        new(FlowNodeKind.InvestigateWithAi, "Investigate failure", "AI", Outcomes("Investigation findings")),
        new(FlowNodeKind.CreateIncident, "RatelDesk: Create incident", "Integrations", [In()]),
        new(FlowNodeKind.ActionGroup, "Independent actions", "Integrations", [In()]),
        new(FlowNodeKind.Finish, "Finish", "Logic", [In()])
    ];

    public static FlowNodeDescriptorDto? Find(FlowNodeKind kind) => Nodes.SingleOrDefault(node => node.Kind == kind);
    public static FlowPortDescriptorDto? Port(FlowNodeKind kind, string id, FlowPortDirection direction) =>
        Find(kind)?.Ports.SingleOrDefault(port => port.Id == id && port.Direction == direction);
    public static bool IsTrigger(FlowNodeKind kind) => kind is FlowNodeKind.AlertRaised or FlowNodeKind.ExecutionEvent;
    public static bool IsTerminal(FlowNodeKind kind) => kind is FlowNodeKind.CreateIncident or FlowNodeKind.ActionGroup or FlowNodeKind.Finish;
    public static bool CanConnect(FlowNodeKind source, string sourcePort, FlowNodeKind target, string targetPort) =>
        Port(source, sourcePort, FlowPortDirection.Output) is { } output &&
        Port(target, targetPort, FlowPortDirection.Input) is { } input && output.PayloadSchema == input.PayloadSchema;
}
