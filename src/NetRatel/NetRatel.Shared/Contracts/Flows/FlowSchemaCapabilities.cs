namespace NetRatel.Shared.Contracts.Flows;

public sealed record FlowSchemaCapabilitiesDto(IReadOnlyList<int> SupportedDraftSchemas, IReadOnlyList<int> ExecutableSchemas);

/// <summary>Authoring support is explicit and separate from executable publication.</summary>
public static class FlowSchemaCapabilities
{
    public static FlowSchemaCapabilitiesDto Current { get; } = new(Array.AsReadOnly(new[] { 1, 2 }), Array.AsReadOnly(new[] { 1 }));
}
