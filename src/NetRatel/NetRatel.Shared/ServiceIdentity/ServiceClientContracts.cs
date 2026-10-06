namespace NetRatel.Shared.ServiceIdentity;

public sealed record ServiceClientCreateRequest(string Name, int TenantId, string PeerInstanceId,
    string PeerTenantId, string[] Scopes, string ResourceConstraintsJson = "{}", string? LinkId = null,
    string? AttemptId = null, string? GrantHash = null, string? DescriptorHash = null, string? DirectionId = null,
    long LinkRevision = 1, string? ClientId = null);
public sealed record ServiceClientMetadata(Guid Id, string Name, string ClientId, int TenantId,
    string PeerInstanceId, string PeerTenantId, string[] Scopes, string ResourceConstraintsJson,
    string Status, string Source, bool ReadOnly, long Revision, long CredentialRevision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, string? LinkId = null, string? DirectionId = null);
public sealed record ServiceClientReveal(ServiceClientMetadata Client, string ClientSecret, string Issuer,
    string TokenEndpoint, string Audience, string[] Scopes);
public sealed record ServiceClientRotateRequest(long ExpectedCredentialRevision);
public sealed record ServiceClientRevokeRequest(long ExpectedRevision);
public sealed record ServiceClientResourceChoice(string Id, string Name);
public sealed record ServiceClientDefinitionChoice(string Id, string Name, string? ResourceId);
public sealed record ServiceClientTenantAuthority(int TenantId, string Name, string[] Scopes,
    ServiceClientResourceChoice[] Resources, ServiceClientDefinitionChoice[] RequestDefinitions);
public sealed record ServiceClientManagementAuthority(bool CanManage, ServiceClientTenantAuthority[] Tenants,
    bool ReciprocalLinkEnabled = false);
public sealed record ServiceClientDeploymentMetadata(string ClientId, string Audience, string[] Scopes,
    string Source = "deployment", bool ReadOnly = true);
public sealed record ServicePublicSettingsUpdate(bool Enabled, string WebBaseUrl, string ApiBaseUrl,
    string Issuer, string Audience, long ExpectedRevision);
public sealed record ServicePublicSettingsResponse(bool Enabled, string WebBaseUrl, string ApiBaseUrl,
    string Issuer, string Audience, string InstanceId, string? GatewayBaseUrl, long Revision,
    string[] LockedFields, bool ReciprocalLinkEnabled);
