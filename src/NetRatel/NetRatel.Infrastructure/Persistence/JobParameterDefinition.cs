namespace NetRatel.Infrastructure.Persistence;

public sealed class JobParameterDefinition
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public JobDefinition Job { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public string? Description { get; set; }
    public string? OptionsJson { get; set; }
}
