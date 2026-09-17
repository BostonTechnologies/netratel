namespace NetRatel.Infrastructure.Persistence;

public sealed class ScriptDefinition
{
    public long Id { get; set; }
    public long SourceRevision { get; set; } = 1;
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public string Description { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ManifestJson { get; set; }
    public string ScriptType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<ScriptParameterDefinition> Parameters { get; set; } = [];
}
