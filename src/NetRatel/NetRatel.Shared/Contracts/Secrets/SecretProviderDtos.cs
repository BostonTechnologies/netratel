using System.Text.Json;

namespace NetRatel.Shared.Contracts.Secrets;

public enum SecretProviderKind
{
    Local,
    AzureKeyVault,
    OpenBao,
    Infisical
}

public enum SecretVersionMode
{
    LatestAtExecution,
    Pinned
}

public sealed record SecretReference(
    int SchemaVersion,
    Guid ConnectionId,
    string Locator,
    string? Field,
    SecretVersionMode VersionMode,
    string? Version)
{
    public const int CurrentSchemaVersion = 1;
}

public enum SecretWriteConcurrency
{
    CompareAndSwap,
    BestEffort
}

public enum SecretDeletionMode
{
    RemoveEntry,
    RecoverableEntry,
    SoftDeleteCurrentVersion
}

public sealed record SecretProviderCapabilities(
    SecretWriteConcurrency WriteConcurrency,
    SecretDeletionMode Deletion,
    bool StructuredFields);

public sealed record SecretProviderConfiguration(
    string Endpoint,
    string? DirectoryTenantId = null,
    string? ClientId = null,
    bool ManagedIdentity = false,
    string? Mount = null,
    string? BasePath = null,
    string? AuthenticationMount = null,
    string? ProjectId = null,
    string? Environment = null);

public sealed record SecretProviderTestResult(DateTimeOffset CheckedAtUtc, bool ListedMetadata, string? ErrorCode);

public sealed record SecretProviderConnection(
    Guid Id,
    SecretProviderKind Kind,
    string DisplayName,
    long Revision,
    long ScopeRevision,
    bool Enabled,
    bool IsDefault,
    SecretProviderConfiguration? Configuration,
    bool HasStoredAuthentication,
    SecretProviderCapabilities Capabilities,
    SecretProviderTestResult? LastTest);

public sealed class SaveSecretProviderRequest
{
    public required SecretProviderKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required SecretProviderConfiguration Configuration { get; init; }
    public long? ExpectedRevision { get; init; }

    // Null preserves the corresponding stored field. Authentication is write-only.
    public string? ClientSecret { get; init; }
    public string? RoleId { get; init; }
    public string? SecretId { get; init; }
}

public sealed record SelectDefaultSecretProviderRequest(Guid ExpectedDefaultId, long ExpectedConnectionRevision);

public sealed record SecretVaultPermissions(bool Browse, bool Use, bool Reveal, bool Manage, bool AdministerProviders,
    bool ManageCredentials, bool RunAs)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SubmitProtectedJobInputs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SaveProtectedJobDefaults { get; init; }
}

public sealed record EnableSecretProviderRequest(long ExpectedRevision);

public enum VaultRemovalKind
{
    Secret,
    DisableConnection,
    DeleteConnection,
    CredentialProfile
}

public sealed record VaultRemovalRequest(VaultRemovalKind Kind, Guid Id, long Revision, DeleteSecretEntryRequest? Secret = null);
public sealed record VaultRemovalPreview(VaultRemovalRequest Request, string ConfirmationToken, DateTimeOffset ExpiresAtUtc,
    string DisplayName, SecretDeletionMode? DeletionMode);
public sealed record ConfirmVaultRemovalRequest(VaultRemovalRequest Request, string ConfirmationToken);
public sealed record VaultRemovalResult(SecretDeletionResult? Secret);

public sealed record SecretVaultError(string Code, string Message);

public sealed record SecretBrowseQuery(string? Folder = null, string? Search = null, string? Cursor = null, int Limit = 50);
public sealed record SecretEntryMetadataQuery(string Locator, string? Version = null);

public sealed record SecretEntryMetadata(
    string Locator,
    string Name,
    bool IsFolder,
    string? Version = null,
    string? Revision = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? Description = null,
    bool? Enabled = null);

public sealed record SecretBrowsePage(SecretEntryMetadata[] Entries, string? NextCursor, bool Truncated, string? SearchScope);

public sealed record SecretFieldMetadata(string Name, string Type);

public sealed record SecretEntryDetails(SecretEntryMetadata Entry, SecretFieldMetadata[] Fields, string[] Versions,
    bool VersionsTruncated, string[] UsedBy);

public sealed class WriteSecretEntryRequest
{
    public string? Locator { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public bool? Enabled { get; init; }
    public string? ExpectedRevision { get; init; }
    public string? ExpectedVersion { get; init; }
    public string? Value { get; init; }
    public Dictionary<string, JsonElement>? Fields { get; init; }
    public string[]? RemoveFields { get; init; }
}

public sealed record DeleteSecretEntryRequest(string Locator, string ExpectedRevision, string ExpectedVersion);

public sealed record SecretDeletionResult(string Locator, string Version, SecretDeletionMode Mode, bool Pending);

public sealed class RevealedSecret(string version, string value)
{
    public string Version { get; } = version;
    public string Value { get; } = value;
}
