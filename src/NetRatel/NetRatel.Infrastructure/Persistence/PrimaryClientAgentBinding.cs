using NetRatel.Application.Agents;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Durable server-owned provenance record for a primary-client to gateway-agent
/// cutover. Agent and primary identity assignment is one-to-one per tenant.
/// </summary>
public sealed class PrimaryClientAgentBinding
{
    public Guid Id { get; set; }

    public int TenantId { get; set; }

    public Guid? AgentId { get; set; }

    public Guid? EnrollmentCodeId { get; set; }

    public string PrimaryClientIdentity { get; set; } = string.Empty;

    public PrimaryClientAgentBindingStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? BoundAtUtc { get; set; }

    public string? BoundBy { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? RevokedBy { get; set; }

    public string BindingSource { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public uint Version { get; set; } = 1;

    public Agent? Agent { get; set; }

    public EnrollmentCode? EnrollmentCode { get; set; }
}
