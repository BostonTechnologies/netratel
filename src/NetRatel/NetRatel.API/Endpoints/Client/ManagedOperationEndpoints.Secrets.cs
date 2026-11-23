using System.Text.Json;
using NetRatel.Application.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static partial class ManagedOperationEndpoints
{
    private static async Task<IResult> VaultMetadataAsync(ManagedOperationDescriptor descriptor, int tenant, JsonElement request,
        ITenantSecretService vault, CancellationToken ct)
    {
        try
        {
            return descriptor.Operation switch
            {
                "connections" => Results.Ok(await vault.ListConnectionsAsync(tenant, includeAdministration: false, ct)),
                "browse" => Results.Ok(await vault.BrowseAsync(tenant, Id(request, "connectionId"), Data<SecretBrowseQuery>(request), ct)),
                "entry" => Results.Ok(await ReadVaultEntryAsync(vault, tenant, Id(request, "connectionId"), Data<SecretEntryMetadataQuery>(request), ct)),
                "profiles" => Results.Ok(await vault.ListProfilesAsync(tenant, ct)),
                _ => throw new InvalidOperationException("Unmapped managed vault operation.")
            };
        }
        catch (SecretProviderException error) { return SecretVaultEndpoints.ProviderProblem(error); }
    }

    private static Task<SecretEntryDetails> ReadVaultEntryAsync(ITenantSecretService vault, int tenant, Guid connection,
        SecretEntryMetadataQuery query, CancellationToken ct) => vault.GetMetadataAsync(tenant, connection, query.Locator, query.Version, ct);
}
