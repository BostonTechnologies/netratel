using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Application.Secrets;

public interface ITenantSecretService
{
    Task<SecretProviderConnection[]> ListConnectionsAsync(int tenantId, bool includeAdministration, CancellationToken ct);
    Task<SecretProviderConnection> SaveConnectionAsync(int tenantId, Guid? connectionId, SaveSecretProviderRequest request, CancellationToken ct);
    Task<SecretProviderConnection> SetDefaultAsync(int tenantId, Guid connectionId, SelectDefaultSecretProviderRequest request, CancellationToken ct);
    Task<SecretProviderConnection> SetConnectionEnabledAsync(int tenantId, Guid connectionId, long expectedRevision, bool enabled, CancellationToken ct);
    Task DeleteConnectionAsync(int tenantId, Guid connectionId, long expectedRevision, CancellationToken ct);
    Task<SecretProviderTestResult> TestConnectionAsync(int tenantId, Guid connectionId, long expectedRevision, CancellationToken ct);
    Task<SecretBrowsePage> BrowseAsync(int tenantId, Guid connectionId, SecretBrowseQuery query, CancellationToken ct);
    Task<SecretEntryDetails> GetMetadataAsync(int tenantId, Guid connectionId, string locator, string? exactVersion, CancellationToken ct);
    Task<SecretEntryMetadata> WriteAsync(int tenantId, Guid connectionId, WriteSecretEntryRequest request, CancellationToken ct);
    Task<SecretDeletionResult> DeleteAsync(int tenantId, Guid connectionId, long expectedConnectionRevision, DeleteSecretEntryRequest request, CancellationToken ct);
    Task<RevealedSecret> RevealAsync(int tenantId, SecretReference reference, CancellationToken ct);
    Task<IReadOnlyList<ResolvedSecret>> ResolveForExecutionAsync(int tenantId, IReadOnlyList<SecretReference> references, CancellationToken ct);
    Task<CredentialProfile[]> ListProfilesAsync(int tenantId, CancellationToken ct);
    Task<CredentialProfile> GetProfileAsync(int tenantId, Guid profileId, CancellationToken ct);
    Task<CredentialProfile> SaveProfileAsync(int tenantId, Guid? profileId, SaveCredentialProfileRequest request, CancellationToken ct);
    Task DeleteProfileAsync(int tenantId, Guid profileId, long expectedRevision, CancellationToken ct);
}

// Server-side execution material. It is never a read model or an actor message.
public sealed class ResolvedSecret(SecretReference exactReference, long scopeRevision, string value)
{
    public SecretReference ExactReference { get; } = exactReference;
    public long ScopeRevision { get; } = scopeRevision;
    public string Value { get; } = value;
}

public enum SecretProviderFailure
{
    InvalidRequest,
    NotFound,
    Conflict,
    InUse,
    Disabled,
    Authentication,
    Forbidden,
    VersionUnavailable,
    Throttled,
    Unavailable,
    KeyUnavailable
}

public sealed class SecretProviderException(SecretProviderFailure reason, string safeMessage) : Exception(safeMessage)
{
    public SecretProviderFailure Reason { get; } = reason;
}
