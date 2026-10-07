namespace NetRatel.Infrastructure.Persistence;

public sealed class RatelDeskConnectorRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public long RowVersion { get; set; }
    public string OwnerPrincipalId { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = string.Empty;
    public string? ProtectedCredential { get; set; }
    public long CredentialRevision { get; set; }
    public string? AuthenticationJson { get; set; }
    public string? ReadinessJson { get; set; }
}
