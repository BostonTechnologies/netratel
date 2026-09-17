namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Authority labels emitted by the presence gateway. The legacy label is accepted
/// during the Dev rollout so an upgraded client can connect before the API is
/// switched to the normal <c>Akka</c> authority mode.
/// </summary>
public static class GatewayAuthority
{
    public const string Akka = "akka";
    public const string LegacyDevCanary = "akka-dev-canary";

    public static bool IsAkka(string? authority) =>
        string.Equals(authority, Akka, System.StringComparison.Ordinal) ||
        string.Equals(authority, LegacyDevCanary, System.StringComparison.Ordinal);

    /// <summary>
    /// Preserves the authority fence for custom values while allowing the two
    /// equivalent Akka labels during the one-way Dev label transition.
    /// </summary>
    public static bool MatchesRequired(string? reportedAuthority, string? requiredAuthority) =>
        IsAkka(requiredAuthority)
            ? IsAkka(reportedAuthority)
            : string.Equals(reportedAuthority, requiredAuthority, System.StringComparison.Ordinal);
}
