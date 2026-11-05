namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Authoring metadata only. Definition identifiers remain decimal strings across browser boundaries.</summary>
public sealed record FlowExecutionDefinitionOptionDto(FlowExecutionSourceKind SourceKind,
    FlowExecutionDefinitionDto Definition, FlowExecutionTargetDto? Target = null);
public sealed record FlowExecutionDefinitionsPageDto(IReadOnlyList<FlowExecutionDefinitionOptionDto> Items,
    string? NextAfterId = null);

/// <summary>Configured eligible targets; discovery is not proof of online presence or future execution authorization.</summary>
public sealed record FlowExecutionTargetsPageDto(IReadOnlyList<FlowExecutionTargetDto> Items, string? NextAfterId = null);
