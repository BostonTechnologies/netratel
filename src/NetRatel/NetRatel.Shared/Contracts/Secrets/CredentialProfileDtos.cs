using System.Globalization;

namespace NetRatel.Shared.Contracts.Secrets;

public enum ExecutionIdentityMode
{
    AgentServiceAccount,
    WindowsPassword,
    UnixPrivilegedLocal
}

public sealed record ExecutionIdentitySetting(ExecutionIdentityMode Mode, Guid? CredentialProfileId = null);

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

public sealed record CredentialExecutionScope(Guid[] AgentIds, string AccountId, bool Jobs, bool Terminals);

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
    CredentialExecutionScope Scope);

public sealed record SaveCredentialProfileRequest(
    string DisplayName,
    ExecutionIdentityMode Mode,
    string? PublicUsername,
    SecretReference? UsernameReference,
    string? Domain,
    SecretReference? PasswordReference,
    CredentialExecutionScope Scope,
    long? ExpectedRevision);
