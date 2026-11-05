namespace NetRatel.Shared.Contracts.Flows;

/// <summary>UI template command, separate from the frozen schema-one creation contract.</summary>
public sealed record FlowCreateFromTemplateRequest(string Name, string TemplateId);
