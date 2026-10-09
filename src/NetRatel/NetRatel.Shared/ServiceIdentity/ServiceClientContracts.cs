namespace NetRatel.Shared.ServiceIdentity;
public sealed record ServiceClientCreateRequest(string Name, int TenantId, string PeerInstanceId,
    string PeerTenantId, string[] Scopes, string ResourceConstraintsJson, string LinkId, string GrantHash,
    long LinkRevision = 1, string? ClientId = null);
