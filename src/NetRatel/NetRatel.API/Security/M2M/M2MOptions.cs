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
}
