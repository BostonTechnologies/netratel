using System.Globalization;

namespace NetRatel.Shared.Contracts.Secrets;

public enum ExecutionIdentityMode
{
    AgentServiceAccount,
    WindowsPassword,
    UnixPrivilegedLocal
}

public sealed record ExecutionIdentitySetting(ExecutionIdentityMode Mode, Guid? CredentialProfileId = null, bool OverrideClientDefault = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Mode switch
    {
        ExecutionIdentityMode.AgentServiceAccount => CredentialProfileId is null,
        ExecutionIdentityMode.WindowsPassword or ExecutionIdentityMode.UnixPrivilegedLocal => CredentialProfileId is { } id && id != Guid.Empty,
        _ => false
    };
}

public sealed record ClientExecutionDefaults(Guid? JobProfileId = null, Guid? TerminalProfileId = null);

public sealed record ExecutionAccountOption(Guid Id, string DisplayName, ExecutionIdentityMode Mode, Guid[] AgentIds);

public sealed record JobExecutionAccount(Guid ProfileId, ExecutionIdentityMode Mode, long MappingRevision);

public static class ExecutionAccountCapabilities
{
    public const string WindowsPassword = "windows-password-execution-v1";
    public const string UnixPrivilegedLocal = "unix-local-execution-v1";

    public static string For(ExecutionIdentityMode mode) => mode switch
    {
        ExecutionIdentityMode.WindowsPassword => WindowsPassword,
        ExecutionIdentityMode.UnixPrivilegedLocal => UnixPrivilegedLocal,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

public sealed record ObservedExecutionIdentity(string Account, string AccountId)
{
    public static ObservedExecutionIdentity? FromObservation(string? account, string? accountId) =>
        account is { Length: > 0 and <= 256 } && accountId is { Length: > 0 and <= 184 } &&
        !string.IsNullOrWhiteSpace(account) && !account.Any(char.IsControl) &&
        (IsWindowsAccountId(accountId) || IsUnixAccountId(accountId))
            ? new(account, accountId) : null;

    public static bool IsWindowsAccountId(string value)
    {
        if (!value.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        var parts = value[4..].Split('-');
        return parts.Length is >= 2 and <= 16 &&
            ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var authority) &&
            authority <= 0xFFFFFFFFFFFF && authority.ToString(CultureInfo.InvariantCulture) == parts[0] &&
            parts.Skip(1).All(part => uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var subAuthority) &&
                subAuthority.ToString(CultureInfo.InvariantCulture) == part);
    }

    public static bool IsUnixAccountId(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) &&
        uid != uint.MaxValue && uid.ToString(CultureInfo.InvariantCulture) == value;
}

public sealed record CredentialExecutionScope(Guid[] AgentIds, string? AccountId, bool Jobs, bool Terminals)
{
    public const int MaximumClients = 1000;
}

public sealed record CredentialProfile(
    Guid Id,
    string DisplayName,
    long Revision,
    long MappingRevision,
    ExecutionIdentityMode Mode,
    string? PublicUsername,
    SecretReference? UsernameReference,
    string? Domain,
    SecretReference? PasswordReference,
    CredentialExecutionScope Scope)
{
    public bool NeedsUsernameVersion() => PublicUsername is null && Scope.AccountId is null &&
        UsernameReference?.VersionMode != SecretVersionMode.Pinned;

    public bool Allows(Guid agentId, string? operatingSystem, bool forTerminal) =>
        (forTerminal ? Scope.Terminals : Scope.Jobs) && Scope.AgentIds.Contains(agentId) && SupportsPlatform(operatingSystem);

    public bool SupportsPlatform(string? operatingSystem) => Mode switch
    {
        ExecutionIdentityMode.WindowsPassword => HasPlatform(operatingSystem, "Windows") || HasPlatform(operatingSystem, "Win32NT"),
        ExecutionIdentityMode.UnixPrivilegedLocal => HasPlatform(operatingSystem, "Linux") || HasPlatform(operatingSystem, "Unix") ||
            HasPlatform(operatingSystem, "Darwin") || HasPlatform(operatingSystem, "macOS") || HasPlatform(operatingSystem, "OSX"),
        _ => false
    };

    private static bool HasPlatform(string? value, string name) => value is not null &&
        (value.Equals(name, StringComparison.OrdinalIgnoreCase) || value.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
}

public sealed record SaveCredentialProfileRequest(
    string DisplayName,
    ExecutionIdentityMode Mode,
    string? PublicUsername,
    SecretReference? UsernameReference,
    string? Domain,
    SecretReference? PasswordReference,
    CredentialExecutionScope Scope,
    long? ExpectedRevision);
