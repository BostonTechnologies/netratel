namespace NetRatel.Infrastructure.Persistence;

public sealed class JobDefinition
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public string? Description { get; set; }
    public int? TenantId { get; set; }
    public Guid? AgentId { get; set; }
    // Retained only to display historical definitions that predate Agent targeting.
    public string ClientIdentity { get; set; } = string.Empty;
    public string? OptionsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<JobParameterDefinition> Parameters { get; set; } = [];
    public List<JobStepDefinition> Steps { get; set; } = [];
}
