using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.SystemPairing.Network;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed class SecretProviderNetworkPolicy(IConfiguration configuration, IHostEnvironment hosting)
{
    public SecretProviderConfiguration Normalize(SecretProviderKind kind, SecretProviderConfiguration settings) => kind switch
    {
        SecretProviderKind.AzureKeyVault => NormalizeAzure(settings),
        SecretProviderKind.OpenBao => new(ValidateOrigin(settings.Endpoint).AbsoluteUri,
            Mount: Segment(settings.Mount), BasePath: BasePath(settings.BasePath), AuthenticationMount: Segment(settings.AuthenticationMount)),
        SecretProviderKind.Infisical => new(ValidateOrigin(settings.Endpoint).AbsoluteUri,
            ClientId: RequiredText(settings.ClientId, 128, "Universal Auth client ID"), ProjectId: Segment(settings.ProjectId),
            Environment: Segment(settings.Environment), BasePath: BasePath(settings.BasePath)),
        _ => throw Invalid("Select a configurable provider kind.")
    };

    public Uri ValidateOrigin(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || uri.Host.Length == 0 ||
            !PairingEndpointPolicy.IsAllowed(uri, PrivateOriginAllowed(uri)) || (uri.Scheme == "http" && !PrivateHttpOriginAllowed(uri)))
            throw Invalid("Supply an approved HTTPS origin without a path, credentials, query or fragment.");
        return uri;
    }

    public bool PrivateOriginAllowed(Uri origin) => IsListed("SecretProviders:PrivateOrigins", origin) || PrivateHttpOriginAllowed(origin);

    public void RequireProtectedAuthenticationStorage()
    {
        if (!hosting.IsDevelopment() && string.IsNullOrWhiteSpace(configuration["DataProtection:WrappingCertificatePath"]))
            throw new SecretProviderException(SecretProviderFailure.KeyUnavailable, "Enroll an API key-ring wrapping certificate before saving provider authentication.");
    }

    public bool ManagedIdentityAllowed(string? clientId) => clientId is not null && configuration
        .GetSection("SecretProviders:AzureManagedIdentityClientIds").GetChildren()
        .Any(setting => string.Equals(setting.Value, clientId, StringComparison.OrdinalIgnoreCase));

    private bool PrivateHttpOriginAllowed(Uri origin) => hosting.IsDevelopment() &&
        origin.Scheme == "http" && IsListed("SecretProviders:DevelopmentHttpOrigins", origin);

    private bool IsListed(string configurationKey, Uri origin) => configuration.GetSection(configurationKey).GetChildren()
        .Any(setting => Uri.TryCreate(setting.Value, UriKind.Absolute, out var allowed) && allowed.IsAbsoluteUri &&
            allowed.AbsolutePath == "/" && allowed.Query.Length == 0 && allowed.Fragment.Length == 0 && allowed.UserInfo.Length == 0 &&
            Uri.Compare(allowed, origin, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);

    private SecretProviderConfiguration NormalizeAzure(SecretProviderConfiguration settings)
    {
        const string vaultDomain = ".vault.azure.net";
        var input = RequiredText(settings.Endpoint, 256, "Vault name or URI").ToLowerInvariant();
        if (!input.Contains(':'))
            input = $"https://{input}{vaultDomain}";
        var vault = ValidateOrigin(input);
        if (vault.Scheme != "https" || !vault.IsDefaultPort || !vault.IdnHost.EndsWith(vaultDomain, StringComparison.Ordinal))
            throw Invalid("Select a public-cloud Azure Key Vault HTTPS origin.");
        var name = vault.IdnHost[..^vaultDomain.Length];
        if (name.Length is < 3 or > 24 || !char.IsAsciiLetter(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]) ||
            name.Contains("--", StringComparison.Ordinal) || name.Any(c => c != '-' && !char.IsAsciiLetterOrDigit(c)))
            throw Invalid("The Azure Key Vault name is invalid.");

        var client = GuidText(settings.ClientId, "Application client ID");
        if (settings.ManagedIdentity)
        {
            if (!ManagedIdentityAllowed(client))
                throw Invalid("The API deployment has not enabled this Azure managed identity.");
            return new(vault.AbsoluteUri, ClientId: client, ManagedIdentity: true);
        }
        return new(vault.AbsoluteUri, DirectoryTenantId: GuidText(settings.DirectoryTenantId, "Entra directory tenant ID"),
            ClientId: client);
    }

    private static string BasePath(string? value) => Path(value?.Trim('/'), allowRoot: true);

    private static string GuidText(string? value, string field)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw Invalid($"{field} must be a non-empty UUID.");
        return id.ToString();
    }
}
