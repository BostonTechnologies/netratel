namespace NetRatel.API.Security.M2M;

public sealed class M2MClientRegistration
{
    private readonly string _secret = string.Empty;
    private readonly object _secretGate = new();
    private string? _loadedSecret;
    public string Secret
    {
        get
        {
            if (string.IsNullOrEmpty(SecretFile)) return _secret;
            lock (_secretGate)
                return _loadedSecret ??= NetRatel.Shared.Connectivity.SecretFileReference.Resolve(_secret, SecretFile, "M2MClients:Secret");
        }
        init => _secret = value;
    }
    public string SecretFile { get; init; } = string.Empty;
    public string[] AllowedAudiences { get; init; } = Array.Empty<string>();
    public string[] AllowedScopes { get; init; } = Array.Empty<string>();
}
