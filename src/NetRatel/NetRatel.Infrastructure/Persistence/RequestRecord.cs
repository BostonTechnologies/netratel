namespace NetRatel.Infrastructure.Persistence;

public sealed class RequestRecord
{
    public int Id { get; set; }
    public string SourceSystem { get; set; } = "WebUI";
    public string TargetClientIdentity { get; set; } = string.Empty;
    public int? TargetTenantId { get; set; }
    public Guid? TargetAgentId { get; set; }
    public string? JobDefinitionId { get; set; }
    public string? ExecutionId { get; set; }
    public string Status { get; set; } = "New";
    public string? ResultMessage { get; set; }
    public string? ResultData { get; set; }
    public string? JobInputs { get; set; }
    public List<string> Logs { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
