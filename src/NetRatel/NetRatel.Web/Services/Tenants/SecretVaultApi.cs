using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Web.Services.Tenants;

public sealed class SecretVaultApi(IHttpClientFactory factory)
{
    public Task<SecretVaultPermissions> PermissionsAsync(int tenant, CancellationToken ct) => GetAsync<SecretVaultPermissions>(Root(tenant) + "/permissions", ct);
    public Task<SecretProviderConnection[]> ConnectionsAsync(int tenant, CancellationToken ct) => GetAsync<SecretProviderConnection[]>(Root(tenant) + "/connections", ct);
    public Task<CredentialProfile[]> ProfilesAsync(int tenant, CancellationToken ct) => GetAsync<CredentialProfile[]>(Root(tenant) + "/profiles", ct);

    public Task<SecretProviderConnection> SaveConnectionAsync(int tenant, Guid? id, SaveSecretProviderRequest draft, CancellationToken ct) =>
        id is { } connection ? SendAsync<SecretProviderConnection>(HttpMethod.Put, Connection(tenant, connection), draft, ct)
            : SendAsync<SecretProviderConnection>(HttpMethod.Post, Root(tenant) + "/connections", draft, ct);

    public Task<SecretProviderConnection> DefaultAsync(int tenant, Guid id, SelectDefaultSecretProviderRequest request, CancellationToken ct) =>
        SendAsync<SecretProviderConnection>(HttpMethod.Put, Connection(tenant, id) + "/default", request, ct);

    public Task<SecretProviderConnection> EnableAsync(int tenant, Guid id, long expectedRevision, CancellationToken ct) =>
        SendAsync<SecretProviderConnection>(HttpMethod.Post, Connection(tenant, id) + "/enable", new EnableSecretProviderRequest(expectedRevision), ct);

    public Task<SecretProviderTestResult> TestAsync(int tenant, Guid id, long expectedRevision, CancellationToken ct) =>
        SendAsync<SecretProviderTestResult>(HttpMethod.Post, Connection(tenant, id) + "/test", new EnableSecretProviderRequest(expectedRevision), ct);

    public Task<SecretBrowsePage> BrowseAsync(int tenant, Guid id, string? folder, string? search, string? cursor, CancellationToken ct) =>
        GetAsync<SecretBrowsePage>(QueryHelpers.AddQueryString(Connection(tenant, id) + "/entries", new Dictionary<string, string?>
            { ["folder"] = folder, ["search"] = search, ["cursor"] = cursor, ["limit"] = "25" }), ct);

    public Task<SecretEntryDetails> EntryAsync(int tenant, Guid id, string locator, string? version, CancellationToken ct) =>
        GetAsync<SecretEntryDetails>(QueryHelpers.AddQueryString(Connection(tenant, id) + "/entry", new Dictionary<string, string?>
            { ["locator"] = locator, ["version"] = version }), ct);

    public Task<SecretEntryMetadata> WriteAsync(int tenant, Guid id, WriteSecretEntryRequest entry, CancellationToken ct) =>
        SendAsync<SecretEntryMetadata>(HttpMethod.Put, Connection(tenant, id) + "/entry", entry, ct);

    public Task<RevealedSecret> RevealAsync(int tenant, SecretReference reference, CancellationToken ct) =>
        SendAsync<RevealedSecret>(HttpMethod.Post, Root(tenant) + "/reveal", reference, ct);

    public Task<CredentialProfile> SaveProfileAsync(int tenant, Guid? id, SaveCredentialProfileRequest draft, CancellationToken ct) =>
        id is { } profile ? SendAsync<CredentialProfile>(HttpMethod.Put, Root(tenant) + $"/profiles/{profile:D}", draft, ct)
            : SendAsync<CredentialProfile>(HttpMethod.Post, Root(tenant) + "/profiles", draft, ct);

    public Task<VaultRemovalPreview> PreviewRemovalAsync(int tenant, VaultRemovalRequest request, CancellationToken ct) =>
        SendAsync<VaultRemovalPreview>(HttpMethod.Post, Root(tenant) + "/removals/preview", request, ct);

    public Task<VaultRemovalResult> ConfirmRemovalAsync(int tenant, VaultRemovalPreview preview, CancellationToken ct) =>
        SendAsync<VaultRemovalResult>(HttpMethod.Post, Root(tenant) + "/removals/confirm",
            new ConfirmVaultRemovalRequest(preview.Request, preview.ConfirmationToken), ct);

    private static string Root(int tenant) => $"/api/v1/tenants/{tenant}/secret-vault";
    private static string Connection(int tenant, Guid id) => Root(tenant) + $"/connections/{id:D}";

    private Task<T> GetAsync<T>(string route, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, route, null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? data, CancellationToken ct)
    {
        using var http = factory.CreateClient("OrchestratorApi");
        using var message = new HttpRequestMessage(method, route);
        if (data is not null)
        {
            message.Content = JsonContent.Create(data);
            message.Headers.Add("X-NetRatel-Account-Request", "1");
        }
        using var response = await http.SendAsync(message, ct);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new SecretVaultUiException("The vault returned an empty response.");

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SecretVaultUiException("Your account cannot perform this operation in this tenant.");
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<SecretVaultError>(ct);
                if (error is { Code.Length: > 0, Message.Length: > 0 and <= 512 })
                    throw new SecretVaultUiException(error.Message);
            }
            catch (JsonException) { throw new SecretVaultUiException("The vault returned an invalid error response."); }
        }
        throw new SecretVaultUiException("The vault request could not be completed. Refresh its current state and try again.");
    }
}

public sealed class SecretVaultUiException(string message) : Exception(message);
