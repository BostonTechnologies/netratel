namespace NetRatel.Infrastructure.Persistence;

public sealed class JobStepDefinition
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public JobDefinition Job { get; set; } = null!;
    public int Ordinal { get; set; }
    public int Type { get; set; }
    public string? Runner { get; set; }
    public string? Command { get; set; }
    public long? ScriptId { get; set; }
    public string? PayloadJson { get; set; }
    public bool Enabled { get; set; } = true;
}
