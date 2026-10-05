namespace NetRatel.API.Security.M2M;

public sealed class M2MOptions
{
    public required string Authority { get; init; }
    public required string Audience { get; init; }
    public string[] AllowedCallerClientIds { get; init; } = Array.Empty<string>();
    public int AccessTokenLifetimeMinutes { get; init; } = 10;
}

public sealed class DownstreamApiOptions
{
    public required string BaseUrl { get; init; }
    public required string Audience { get; init; }
    public required string Scope { get; init; }
    public string? Authority { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ProfileIdentity { get; init; }
    public long ProfileRevision { get; init; }
    public long CredentialRevision { get; init; }
    public string? PeerInstanceId { get; init; }
    public string? PeerTenantId { get; init; }
    public string? LocalTenantId { get; init; }
    public bool ClientSecretPost { get; init; }
    /// <summary>Managed targets check current link/credential authority even when a cached token is usable.</summary>
    public Func<CancellationToken, Task<bool>>? CurrentAuthority { get; init; }
}
