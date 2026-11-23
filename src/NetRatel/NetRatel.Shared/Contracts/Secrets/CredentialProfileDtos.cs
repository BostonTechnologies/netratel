namespace NetRatel.Shared.Contracts.Secrets;

public enum ExecutionIdentityMode
{
    AgentServiceAccount,
    WindowsPassword,
    UnixPrivilegedLocal
}

public sealed record ExecutionIdentitySetting(ExecutionIdentityMode Mode, Guid? CredentialProfileId = null);

public sealed record ObservedExecutionIdentity(string Account, string AccountId);

public sealed record CredentialExecutionScope(Guid[] AgentIds, string AccountId, bool Jobs, bool Terminals);

public sealed record CredentialProfile(
    Guid Id,
    string DisplayName,
    long Revision,
    long MappingRevision,
    ExecutionIdentityMode Mode,
    string? PublicUsername,
    SecretReference? UsernameReference,
    string? Domain,
    SecretReference? PasswordReference,
    CredentialExecutionScope Scope);

public sealed record SaveCredentialProfileRequest(
    string DisplayName,
    ExecutionIdentityMode Mode,
    string? PublicUsername,
    SecretReference? UsernameReference,
    string? Domain,
    SecretReference? PasswordReference,
    CredentialExecutionScope Scope,
    long? ExpectedRevision);
