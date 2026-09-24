using Microsoft.Extensions.Configuration;

namespace NetRatel.API.Bootstrap;

/// <summary>Resolves the API bearer settings shared by startup validation and JWT authentication.</summary>
public sealed record OidcApiConfiguration(
    string? Authority,
    string? Audience,
    string[] Audiences,
    string[] ValidIssuers,
    string RoleClaimType,
    string NameClaimType)
{
    public static OidcApiConfiguration Resolve(IConfiguration configuration)
    {
        var oidc = configuration.GetSection("Authentication:Oidc");
        if (!oidc.Exists()) oidc = configuration.GetSection("Authentication:Azure");

        var authority = oidc["Authority"];
        var audience = oidc["Audience"] ?? oidc["ClientId"];
        var audiences = oidc.GetSection("Audiences").Get<string[]>() ?? [];
        var issuers = oidc.GetSection("ValidIssuers").Get<string[]>() ?? [];
        if (!oidc.Exists())
        {
            var azureAd = configuration.GetSection("AzureAd");
            var tenantId = azureAd["TenantId"];
            var clientId = azureAd["ClientId"] ?? azureAd["Audience"];
            authority = string.IsNullOrWhiteSpace(tenantId)
                ? authority
                : $"https://login.microsoftonline.com/{tenantId}/v2.0";
            audience = clientId;
            audiences = string.IsNullOrWhiteSpace(clientId)
                ? []
                : [clientId, azureAd["AppIdUri"] ?? $"api://{clientId}"];
            issuers = string.IsNullOrWhiteSpace(tenantId)
                ? []
                : [$"https://login.microsoftonline.com/{tenantId}/v2.0", $"https://sts.windows.net/{tenantId}/"];
        }

        return new OidcApiConfiguration(authority, audience, audiences, issuers,
            oidc["RoleClaimType"] ?? "roles", oidc["NameClaimType"] ?? "preferred_username");
    }

    public void ValidateActive()
    {
        if (IsPlaceholder(Authority) || !Uri.TryCreate(Authority, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Active OIDC mode requires a real absolute Authentication:Oidc:Authority (or complete legacy Azure settings). Set Authentication:Mode=Local to ignore unused OIDC examples.");
        }

        if (Audiences.Length == 0 ? IsPlaceholder(Audience) : Audiences.Any(IsPlaceholder))
        {
            throw new InvalidOperationException("Active OIDC mode requires Authentication:Oidc:Audience, Audiences, or a compatible ClientId. Set Authentication:Mode=Local for a local-account installation.");
        }
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("replace-at-deployment", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase);
}
