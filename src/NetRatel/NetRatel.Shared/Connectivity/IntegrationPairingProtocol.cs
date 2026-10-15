namespace NetRatel.Shared.Connectivity;

/// <summary>NetRatel credential pairing; this is not an OAuth device grant.</summary>
public static class IntegrationPairingProtocol
{
    public const int Version = 1;
    public const string Route = "/api/integration-pairing/v1";
    public const string AccountRoute = "/api/v2/account/integration-credentials/pairing";
    public const int PollIntervalSeconds = 5;
    public const string DurableAuthorityGuidance = "This connection needs current NetRatel role assignments for the selected access. Ask your administrator to assign those tenant or server permissions to your account, then try again.";
}

public enum PairingCredentialPurpose { Api = 0, HttpMcp = 1 }
public sealed record IntegrationPairingCapabilities(int ProtocolVersion, string PublicOrigin, bool PairingSupported,
    string VerificationUri, string StartEndpoint, string PollEndpoint, string CancelEndpoint,
    string AcknowledgeEndpoint, string HttpMcpResource)
{
    public IReadOnlyList<string> AuthenticationMethods { get; init; } = ["browser"];
    public IReadOnlyList<string> ConfigurationCapabilities { get; init; } = ["scoped-integration-credential", "explicit-tenant-grants"];
    public string HttpMcpAuthenticationMode { get; init; } = "local";
    public string? HttpMcpOidcAuthority { get; init; }
    public IReadOnlyList<string> HttpMcpRequiredScopes { get; init; } = [];
    public bool HttpMcpAuthenticationModeKnown { get; init; } = true;
}
public sealed record IntegrationPairingStartRequest(string Name, PairingCredentialPurpose Purpose,
    string? Resource = null, IReadOnlyList<string>? RequestedPermissions = null);
public sealed record IntegrationPairingStartResponse(string RequestId, string UserCode, string DeviceCapability,
    string VerificationUri, string PublicOrigin, DateTimeOffset ExpiresAtUtc, int IntervalSeconds)
{ public override string ToString() => $"Pairing request {RequestId} (code and capability redacted)"; }
public sealed record IntegrationPairingDeviceRequest(string DeviceCapability)
{ public override string ToString() => "Pairing device request (capability redacted)"; }
public sealed record IntegrationPairingAcknowledgeRequest(string DeviceCapability, string CredentialId)
{ public override string ToString() => $"Pairing acknowledgement {CredentialId} (capability redacted)"; }
public sealed record IntegrationPairingGrant(int TenantId, string Permission)
{
    public string? TenantName { get; init; }
    public string? PermissionLabel { get; init; }
}
public sealed record IntegrationPairingDelivery(string CredentialId, string Secret, string Name,
    PairingCredentialPurpose Purpose, string? Resource, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationPairingGrant> Grants, IReadOnlyList<string> InstancePermissions)
{ public override string ToString() => $"Pairing delivery {CredentialId} (secret redacted)"; }
public sealed record IntegrationPairingPollResponse(string Status, int IntervalSeconds = IntegrationPairingProtocol.PollIntervalSeconds,
    IntegrationPairingDelivery? Delivery = null);
public sealed record IntegrationPairingCodeRequest(string UserCode)
{ public override string ToString() => "Pairing code request (code redacted)"; }
public sealed record IntegrationPairingApprovalRequest(string UserCode, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationPairingGrant> Grants, IReadOnlyList<string>? InstancePermissions = null)
{ public override string ToString() => "Pairing approval (code redacted)"; }
public sealed record IntegrationPairingReview(string RequestId, string UserCode, string Name,
    PairingCredentialPurpose Purpose, string PublicOrigin, string? Resource,
    IReadOnlyList<string> RequestedPermissions, DateTimeOffset ExpiresAtUtc, string Status)
{ public override string ToString() => $"Pairing review {RequestId} {Status} (code redacted)"; }
