using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Infrastructure.Persistence;

public sealed class SecretProviderConnectionRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; }
    public SecretProviderKind Kind { get; set; }
    public required string DisplayName { get; set; }
    public long Revision { get; set; }
    public long ScopeRevision { get; set; }
    public long AuthenticationRevision { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public required string ConfigurationJson { get; set; }
    public string? ProtectedAuthentication { get; set; }
    public string? LastTestJson { get; set; }
}

public sealed class CredentialProfileRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; }
    public required string DisplayName { get; set; }
    public long Revision { get; set; }
    public long MappingRevision { get; set; }
    public required string MappingJson { get; set; }
    public Guid? UsernameConnectionId { get; set; }
    public string? UsernameLocator { get; set; }
    public Guid? PasswordConnectionId { get; set; }
    public string? PasswordLocator { get; set; }
}
