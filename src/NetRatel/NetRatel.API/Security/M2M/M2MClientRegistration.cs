namespace NetRatel.API.Security.M2M;

public sealed class M2MClientRegistration
{
    public string Secret { get; init; } = string.Empty;
    public string[] AllowedAudiences { get; init; } = Array.Empty<string>();
    public string[] AllowedScopes { get; init; } = Array.Empty<string>();
}
