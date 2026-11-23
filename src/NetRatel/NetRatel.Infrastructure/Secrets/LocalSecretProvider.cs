using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class LocalSecretProvider(SecretService local) : ISecretProvider
{
    public SecretProviderKind Kind => SecretProviderKind.Local;
    public SecretProviderCapabilities Capabilities { get; } = new(SecretWriteConcurrency.CompareAndSwap, SecretDeletionMode.RemoveEntry, false);
    public string ValidateLocator(string locator) => SecretService.ValidateLocalLocator(locator);
    public void ValidateVersion(string version) => SecretProviderValidation.PositiveInt64(version);

    public Task<SecretBrowsePage> BrowseAsync(SecretProviderContext context, SecretBrowseQuery query, CancellationToken ct) =>
        local.BrowseLocalAsync(context.TenantId, query, ct);

    public Task<SecretEntryDetails> GetMetadataAsync(SecretProviderContext context, string locator, string? version, CancellationToken ct) =>
        local.GetLocalMetadataAsync(context.TenantId, locator, version, ct);

    public Task<SecretEntryMetadata> WriteAsync(SecretProviderContext context, WriteSecretEntryRequest request, CancellationToken ct) =>
        local.WriteLocalAsync(context.TenantId, request, ct);

    public Task<SecretDeletionResult> DeleteAsync(SecretProviderContext context, DeleteSecretEntryRequest request, CancellationToken ct) =>
        local.DeleteLocalAsync(context.TenantId, request, ct);

    public async Task<ProviderSecretValues> ResolveAsync(SecretProviderContext context, string locator, string? version,
        string?[] fields, CancellationToken ct)
    {
        var resolved = await local.ResolveLocalAsync(context.TenantId, locator, version, ct);
        return new(resolved.Version, Enumerable.Repeat(resolved.Value, fields.Length).ToArray());
    }
}
