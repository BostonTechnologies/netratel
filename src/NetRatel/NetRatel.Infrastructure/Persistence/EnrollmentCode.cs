namespace NetRatel.Infrastructure.Persistence;

public sealed class EnrollmentCode
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset ValidToUtc { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public string? Notes { get; set; }
    public Guid? DevelopmentMcpTargetAgentId { get; set; }
    public string? DevelopmentMcpMarker { get; set; }
}
