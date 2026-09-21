using System.Net.Http.Json;

namespace NetRatel.Web.Services.Authentication;

/// <summary>
/// Calls the local-account lifecycle API. Sensitive values returned by these calls are deliberately
/// represented only in the interactive component state; this client does not log or persist them.
/// </summary>
public interface ILocalAccountApiService
{
    Task ActivateAsync(LocalAccountActivationRequest request, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(LocalPasswordChangeRequest request, CancellationToken cancellationToken = default);
    Task<AuthenticatorSetup> BeginTwoFactorSetupAsync(string currentPassword, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default);
    Task DisableTwoFactorAsync(LocalTwoFactorDisableRequest request, CancellationToken cancellationToken = default);
}

public sealed class LocalAccountApiService(IHttpClientFactory clients) : ILocalAccountApiService
{
    public async Task ActivateAsync(LocalAccountActivationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await clients.CreateClient("SystemApiNoAuth")
            .PostAsJsonAsync("/api/v2/local-auth/activate", request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ChangePasswordAsync(LocalPasswordChangeRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await AuthenticatedClient.PostAsJsonAsync("/api/v2/local-auth/change-password", request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AuthenticatorSetup> BeginTwoFactorSetupAsync(string currentPassword, CancellationToken cancellationToken = default)
    {
        using var response = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v2/local-auth/two-factor/setup", new CurrentPasswordRequest(currentPassword), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthenticatorSetup>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The authenticator setup response was missing.");
    }

    public async Task<IReadOnlyList<string>> EnableTwoFactorAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await AuthenticatedClient.PostAsJsonAsync(
            "/api/v2/local-auth/two-factor/enable", new TwoFactorCodeRequest(code), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RecoveryCodesResponse>(cancellationToken).ConfigureAwait(false);
        return payload?.RecoveryCodes ?? [];
    }

    public async Task DisableTwoFactorAsync(LocalTwoFactorDisableRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await AuthenticatedClient.PostAsJsonAsync("/api/v2/local-auth/two-factor/disable", request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient AuthenticatedClient => clients.CreateClient("OrchestratorApi");

    private sealed record CurrentPasswordRequest(string CurrentPassword);
    private sealed record TwoFactorCodeRequest(string Code);
    private sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
}

public sealed record LocalAccountActivationRequest(string Email, string ActivationToken, string NewPassword);
public sealed record LocalPasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record LocalTwoFactorDisableRequest(string CurrentPassword, string Code);
public sealed record AuthenticatorSetup(string SharedKey, string AuthenticatorUri);
