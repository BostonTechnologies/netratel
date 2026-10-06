using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.ServiceIdentity;

/// <summary>Managed tenant service identities are independent from human login and legacy deployment M2M.</summary>
public sealed class ServiceIdentityOptions
{
    public const string SectionName = "ServiceIdentity";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "netratel.services";
    public string ApiBaseUrl { get; set; } = "";
    public string WebBaseUrl { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool AllowPrivateHttp { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; } = 300;
    public int ClockSkewSeconds { get; set; } = 15;
    public int CredentialMaximumAgeDays { get; set; } = 90;
    public int ManualRotationOverlapSeconds { get; set; } = 600;
    public int TerminalControlRecoverySeconds { get; set; } = 3600;
}

public sealed class ServiceIdentityOptionsValidator : IValidateOptions<ServiceIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceIdentityOptions value)
    {
        if (!value.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        foreach (var (field, address) in new[] { ("Issuer", value.Issuer), ("ApiBaseUrl", value.ApiBaseUrl), ("WebBaseUrl", value.WebBaseUrl) })
        {
            if (string.IsNullOrEmpty(address)) continue; // Derived from validated configured API identity.
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || address.Trim() != address || address.Any(char.IsControl) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Scheme != "https" && !(value.AllowPrivateHttp && uri.Scheme == "http"))
                errors.Add($"ServiceIdentity:{field} requires a configured HTTPS URL; private HTTP requires explicit opt-in.");
        }
        if (value.InstanceId.Length != 0 && (!Guid.TryParseExact(value.InstanceId, "D", out var id) || id.ToString("D") != value.InstanceId))
            errors.Add("ServiceIdentity:InstanceId must be a canonical installation GUID.");
        if (string.IsNullOrWhiteSpace(value.Audience) || value.Audience.Length > 256 || value.Audience.Any(char.IsControl)) errors.Add("ServiceIdentity:Audience is required.");
        if (value.AccessTokenLifetimeSeconds is < 60 or > 900 || value.ClockSkewSeconds is < 0 or > 60 ||
            value.CredentialMaximumAgeDays is < 1 or > 365 || value.ManualRotationOverlapSeconds is < 60 or > 3600 ||
            value.TerminalControlRecoverySeconds is < 60 or > 86400) errors.Add("Service identity lifetime or overlap exceeds supported bounds.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public static class ServiceIdentityScopes
{
    public const string Verify = "bostec.service-link.verify";
    public const string Control = "bostec.service-link.control";
    public const string OrchestrationRead = "netratel.orchestration.read";
    public const string OrchestrationInvoke = "netratel.orchestration.invoke";
    public static readonly string[] Business = [OrchestrationRead, OrchestrationInvoke];
    public static readonly string[] All = [.. Business, Verify, Control];
}

public static class ServiceIdentityClaims
{
    public const string Purpose = "netratel_service";
    public const string PrincipalId = "service_principal_id";
    public const string CredentialRevision = "credential_revision";
    public const string GrantRevision = "service_grant_revision";
    public const string TenantId = "tenant_id";
    public const string PeerInstanceId = "peer_instance_id";
    public const string PeerTenantId = "peer_tenant_id";
    public const string LinkId = "link_id";
    public const string LinkRevision = "link_revision";
    public const string AttemptId = "attempt_id";
    public const string GrantHash = "grant_hash";
    public const string DirectionId = "direction_id";
}
