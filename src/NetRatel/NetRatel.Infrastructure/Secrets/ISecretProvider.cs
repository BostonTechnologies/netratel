using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Infrastructure.Secrets;

internal interface ISecretProvider
{
    SecretProviderKind Kind { get; }
    SecretProviderCapabilities Capabilities { get; }
    string ValidateLocator(string locator);
    void ValidateVersion(string version);
    async Task<bool> TestMetadataAsync(SecretProviderContext context, CancellationToken ct)
    {
        _ = await BrowseAsync(context, new(Limit: 1), ct);
        return true;
    }
    Task<SecretBrowsePage> BrowseAsync(SecretProviderContext context, SecretBrowseQuery query, CancellationToken ct);
    Task<SecretEntryDetails> GetMetadataAsync(SecretProviderContext context, string locator, string? version, CancellationToken ct);
    Task<SecretEntryMetadata> WriteAsync(SecretProviderContext context, WriteSecretEntryRequest request, CancellationToken ct);
    Task<SecretDeletionResult> DeleteAsync(SecretProviderContext context, DeleteSecretEntryRequest request, CancellationToken ct);
    Task<ProviderSecretValues> ResolveAsync(SecretProviderContext context, string locator, string? version,
        string?[] fields, CancellationToken ct);
}

internal sealed class SecretProviderContext(int tenantId, Guid connectionId, long authenticationRevision,
    SecretProviderConfiguration? configuration, Func<ProviderAuthentication?> readAuthentication)
{
    public int TenantId { get; } = tenantId;
    public Guid ConnectionId { get; } = connectionId;
    public long AuthenticationRevision { get; } = authenticationRevision;
    public SecretProviderConfiguration Configuration => configuration ?? throw new InvalidOperationException("The connection uses Local storage.");
    public ProviderAuthentication? ReadAuthentication() => readAuthentication();
}

internal sealed class ProviderAuthentication
{
    public string? ClientSecret { get; init; }
    public string? RoleId { get; init; }
    public string? SecretId { get; init; }
}

internal sealed class ProviderSecretValues(string version, string[] values)
{
    public string Version { get; } = version;
    public string[] Values { get; } = values;
}
