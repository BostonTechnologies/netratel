using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using NetRatel.Application.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class AzureKeyVaultSecretProvider(SecretProviderHttp http, SecretProviderNetworkPolicy network, TimeProvider clock)
    : ISecretProvider, IDisposable
{
    private const int VersionLimit = 128;
    private const string DescriptionTag = "netratel-description";
    private readonly HttpClient _entraHttp = http.CreateOriginBoundClient(AzureAuthorityHosts.AzurePublicCloud);
    private readonly MemoryCache _credentials = new(new MemoryCacheOptions { SizeLimit = 256 });

    public SecretProviderKind Kind => SecretProviderKind.AzureKeyVault;
    public SecretProviderCapabilities Capabilities { get; } = new(SecretWriteConcurrency.BestEffort, SecretDeletionMode.RecoverableEntry, false);

    public string ValidateLocator(string locator)
    {
        if (locator is not { Length: > 0 and <= 127 } || locator.Any(c => c != '-' && !char.IsAsciiLetterOrDigit(c)))
            throw Invalid("Azure Key Vault secret names contain letters, digits and hyphens.");
        return locator.ToLowerInvariant();
    }

    public void ValidateVersion(string version)
    {
        if (version is not { Length: 32 } || version.Any(c => !char.IsAsciiHexDigit(c)))
            throw Invalid("Supply an Azure Key Vault version identifier.");
    }

    public Task<SecretBrowsePage> BrowseAsync(SecretProviderContext context, SecretBrowseQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(query.Folder))
            throw Invalid("Azure Key Vault has a single secret folder.");
        return WithClientAsync(context, async (client, token) =>
        {
            await using var pages = client.GetPropertiesOfSecretsAsync(token).AsPages(query.Cursor, Math.Min(query.Limit, 25))
                .GetAsyncEnumerator(token);
            if (!await pages.MoveNextAsync())
                return new SecretBrowsePage([], null, false, "Current Azure page");
            var page = pages.Current;
            var entries = page.Values.Where(value => query.Search is null || value.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
                .Select(Map).ToArray();
            return new SecretBrowsePage(entries, page.ContinuationToken, false, "Current Azure page");
        }, ct);
    }

    public Task<SecretEntryDetails> GetMetadataAsync(SecretProviderContext context, string locator, string? version, CancellationToken ct) =>
        WithClientAsync(context, async (client, token) =>
        {
            var properties = await ReadVersionsAsync(client, locator, token);
            if (properties.Length == 0)
                throw version is null ? Missing() : MissingVersion();
            if (version is not null && !properties.Any(p => string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase) && IsAvailable(p)))
                throw MissingVersion();
            return new SecretEntryDetails(Map(properties[0]), [], properties.Where(IsAvailable).Select(p => p.Version!).ToArray(), false, []);
        }, ct);

    public Task<SecretEntryMetadata> WriteAsync(SecretProviderContext context, WriteSecretEntryRequest request, CancellationToken ct)
    {
        CheckScalarWrite(request);
        if (request.Locator is null || request.Name is not null)
            throw Invalid("Specify the Azure Key Vault secret name.");
        var locator = ValidateLocator(request.Locator);
        var description = OptionalText(request.Description, 256, "Description");
        var creating = request.ExpectedVersion is null && request.ExpectedRevision is null;
        if (creating && request.Value is null || !creating && request.Value is null && request.Description is null && request.Enabled is null)
            throw Invalid("Supply a value for a new secret or a value or metadata change for an edit.");

        return WithClientAsync(context, async (client, token) =>
        {
            var current = (await ReadVersionsAsync(client, locator, token)).FirstOrDefault();
            if (creating && current is not null)
                throw Conflict();
            if (!creating)
                RequireExpected(Map(current ?? throw Missing()), request.ExpectedRevision, request.ExpectedVersion);
            if (request.Value is not null)
            {
                var secret = new KeyVaultSecret(locator, request.Value);
                CopyProperties(current, secret.Properties);
                ApplyMetadata(secret.Properties, request, description);
                var saved = await client.SetSecretAsync(secret, token);
                return Map(saved.Value.Properties);
            }

            ApplyMetadata(current!, request, description);
            var updated = await client.UpdateSecretPropertiesAsync(current!, token);
            return Map(updated.Value);
        }, ct);
    }

    public Task<SecretDeletionResult> DeleteAsync(SecretProviderContext context, DeleteSecretEntryRequest request, CancellationToken ct) =>
        WithClientAsync(context, async (client, token) =>
        {
            var locator = ValidateLocator(request.Locator);
            var current = (await ReadVersionsAsync(client, locator, token)).FirstOrDefault() ?? throw Missing();
            RequireExpected(Map(current), request.ExpectedRevision, request.ExpectedVersion);
            var operation = await client.StartDeleteSecretAsync(locator, token);
            return new SecretDeletionResult(locator, request.ExpectedVersion, Capabilities.Deletion, !operation.HasCompleted);
        }, ct);

    public Task<ProviderSecretValues> ResolveAsync(SecretProviderContext context, string locator, string? version,
        string?[] fields, CancellationToken ct) => WithClientAsync(context, async (client, token) =>
        {
            var result = await client.GetSecretAsync(ValidateLocator(locator), version, token);
            var secret = result.Value;
            if (!IsAvailable(secret.Properties) || secret.Properties.Version is not { } resolved ||
                version is not null && !string.Equals(version, resolved, StringComparison.OrdinalIgnoreCase))
                throw MissingVersion();
            return new ProviderSecretValues(resolved, Enumerable.Repeat(secret.Value, fields.Length).ToArray());
        }, ct, missingVersion: true);

    private async Task<T> WithClientAsync<T>(SecretProviderContext context, Func<SecretClient, CancellationToken, Task<T>> operate,
        CancellationToken ct, bool missingVersion = false)
    {
        var vault = network.ValidateOrigin(context.Configuration.Endpoint);
        using var transport = http.CreateOriginBoundClient(vault);
        var options = new SecretClientOptions { Transport = new HttpClientTransport(transport) };
        options.Retry.MaxRetries = 1;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(8);
        ConfigureDiagnostics(options);
        var client = new SecretClient(vault, Credential(context), options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await operate(client, timeout.Token); }
        catch (AuthenticationFailedException) { throw SecretProviderHttp.Failure(401); }
        catch (RequestFailedException error) when (missingVersion && error.Status == 404) { throw MissingVersion(); }
        catch (RequestFailedException error) { throw SecretProviderHttp.Failure(error.Status); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
    }

    private TokenCredential Credential(SecretProviderContext context)
    {
        var config = context.Configuration;
        if (config.ManagedIdentity && !network.ManagedIdentityAllowed(config.ClientId))
            throw new SecretProviderException(SecretProviderFailure.Authentication, "The API deployment no longer enables this Azure managed identity.");
        return _credentials.GetOrCreate((context.TenantId, context.ConnectionId, context.AuthenticationRevision), entry =>
        {
            entry.Size = 1;
            entry.SlidingExpiration = TimeSpan.FromMinutes(15);
            if (config.ManagedIdentity)
                return (TokenCredential)new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config.ClientId!));

            var authentication = context.ReadAuthentication() ?? throw Unavailable();
            var options = new ClientSecretCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzurePublicCloud, Transport = new HttpClientTransport(_entraHttp) };
            options.Retry.MaxRetries = 1;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(8);
            ConfigureDiagnostics(options);
            return new ClientSecretCredential(config.DirectoryTenantId, config.ClientId, authentication.ClientSecret, options);
        })!;
    }

    private static void ConfigureDiagnostics(ClientOptions options)
    {
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
    }

    private static async Task<SecretProperties[]> ReadVersionsAsync(SecretClient client, string name, CancellationToken ct)
    {
        var properties = new List<SecretProperties>();
        try
        {
            await foreach (var version in client.GetPropertiesOfSecretVersionsAsync(name, ct).WithCancellation(ct))
            {
                if (properties.Count == VersionLimit)
                    throw new SecretProviderException(SecretProviderFailure.Unavailable,
                        "The Azure entry exceeds the bounded version lookup. Use Azure Key Vault to review its history.");
                properties.Add(version);
            }
        }
        catch (RequestFailedException error) when (error.Status == 404) { return []; }
        return properties.OrderByDescending(value => value.CreatedOn).ToArray();
    }

    private SecretEntryMetadata Map(SecretProperties properties)
    {
        properties.Tags.TryGetValue(DescriptionTag, out var description);
        var locator = ValidateLocator(properties.Name);
        var revisionInput = $"{properties.Version}\n{properties.UpdatedOn:O}\n{properties.Enabled}\n{description}";
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput)));
        return new(locator, properties.Name, false, properties.Version, revision, properties.UpdatedOn, description, IsAvailable(properties));
    }

    private bool IsAvailable(SecretProperties properties) => properties.Enabled is not false &&
        (properties.NotBefore is not { } start || start <= clock.GetUtcNow()) &&
        (properties.ExpiresOn is not { } expiration || expiration > clock.GetUtcNow());

    private static void CopyProperties(SecretProperties? source, SecretProperties destination)
    {
        if (source is null)
            return;
        destination.Enabled = source.Enabled;
        destination.ExpiresOn = source.ExpiresOn;
        destination.NotBefore = source.NotBefore;
        destination.ContentType = source.ContentType;
        foreach (var tag in source.Tags)
            destination.Tags.Add(tag.Key, tag.Value);
    }

    private static void ApplyMetadata(SecretProperties properties, WriteSecretEntryRequest request, string? description)
    {
        if (request.Enabled is not null)
            properties.Enabled = request.Enabled;
        if (request.Description is not null)
        {
            if (description is null)
                properties.Tags.Remove(DescriptionTag);
            else
                properties.Tags[DescriptionTag] = description;
        }
    }

    public void Dispose()
    {
        _entraHttp.Dispose();
        _credentials.Dispose();
    }
}
