namespace NetRatel.Web.Configuration;

/// <summary>
/// OIDC settings for the optional machine-token-to-browser-session bridge.
/// This bridge is distinct from interactive OIDC, native-agent, and M2M trust paths.
/// </summary>
public sealed class MachineTokenOptions
{
    public const string SectionName = "Authentication:MachineToken";

    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string[] RequiredGroups { get; set; } = [];
    public string[] SessionRoles { get; set; } = [];
    public string[] AllowedSigningAlgorithms { get; set; } = ["RS256", "ES256"];
}
