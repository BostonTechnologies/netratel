namespace NetRatel.Infrastructure.Persistence;

public sealed class ScriptParameterDefinition
{
    public long Id { get; set; }
    public long ScriptId { get; set; }
    public ScriptDefinition Script { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public string? Description { get; set; }
    public string? OptionsJson { get; set; }
}
