namespace NetRatel.Shared.Connectivity;

/// <summary>Credential setup and editing limits advertised by the account authority endpoint.</summary>
public sealed record IntegrationCredentialLimits(int TenantScopes, int PermissionsPerScope, int TenantGrants, int RequestBytes)
{
    public const int MaximumTenantScopes = 16;
    public const int MaximumAccountRequestBytes = 256 * 1024;
}
