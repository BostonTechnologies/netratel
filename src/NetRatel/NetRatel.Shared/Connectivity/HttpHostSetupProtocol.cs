namespace NetRatel.Shared.Connectivity;

/// <summary>Administrator approval of a specific deployment generation. Never a personal credential or OAuth grant.</summary>
public static class HttpHostSetupProtocol
{
    public const int Version = 1;
    public const string Route = "/api/http-host-setup/v1";
    public const string AccountRoute = "/api/v2/account/integration-credentials/http-host-setup";
    public const int PollIntervalSeconds = 5;
    public static string NormalizeWebAddress(string value) => NetRatel.Shared.Client.ClientEndpointAddress.NormalizePublicWebBase(value);
}

public sealed record HttpHostSetupCapabilities(int ProtocolVersion, string PublicOrigin, string Resource,
    string VerificationUri, bool LocalCredentialMode, string? OidcAuthority,
    IReadOnlyList<string> RequiredGroups, IReadOnlyList<string> RequiredScopes)
{
    public bool AuthenticationModeKnown { get; init; } = true;
    public bool ExistingHostConfigured { get; init; }
    public string? ApiServiceAuthority { get; init; }
    public string ApiServiceAudience { get; init; } = "netratel.api";
    public IReadOnlyList<string> ExistingM2MCallerClientIds { get; init; } = [];
}
public sealed record HttpHostSetupStartRequest(string PublicOrigin, string Resource, string RegistrationFingerprint,
    string ServicePrincipal = "netratel-mcp-http");
public sealed record HttpHostSetupStartResponse(string RequestId, string UserCode, string DeviceCapability,
    string VerificationUri, string PublicOrigin, DateTimeOffset ExpiresAtUtc, int IntervalSeconds)
{
    public override string ToString() => $"HTTP host setup {RequestId} (code and capability redacted)";
}
public sealed record HttpHostSetupDeviceRequest(string DeviceCapability)
{
    public override string ToString() => "HTTP host setup device request (capability redacted)";
}
public sealed record HttpHostSetupCodeRequest(string UserCode)
{
    public override string ToString() => "HTTP host setup decision (code redacted)";
}
public sealed record HttpHostSetupReview(string RequestId, string UserCode, string PublicOrigin, string Resource,
    string ServicePrincipal, string RegistrationFingerprint, DateTimeOffset ExpiresAtUtc, string Status);
public sealed record HttpHostSetupPollResponse(string Status, int IntervalSeconds = HttpHostSetupProtocol.PollIntervalSeconds,
    string? ApprovalId = null, string? RegistrationFingerprint = null);
