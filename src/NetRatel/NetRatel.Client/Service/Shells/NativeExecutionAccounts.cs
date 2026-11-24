using System;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Client.Service.Shells;

internal static class NativeExecutionAccounts
{
    public static string[] Capabilities => OperatingSystem.IsWindows() && WindowsExecutionAccount.IsAvailable
        ? [ExecutionAccountCapabilities.WindowsPassword]
        : UnixExecutionAccount.IsAvailable ? [ExecutionAccountCapabilities.UnixPrivilegedLocal] : [];

    public static string? Capability(RunAsCredential.Types.Mode mode) => mode switch
    {
        RunAsCredential.Types.Mode.WindowsPassword => ExecutionAccountCapabilities.WindowsPassword,
        RunAsCredential.Types.Mode.UnixPrivilegedLocal => ExecutionAccountCapabilities.UnixPrivilegedLocal,
        _ => null
    };

    public static bool IsValid(RunAsCredential credential) =>
        Guid.TryParse(credential.ProfileId, out var profile) && profile != Guid.Empty &&
        !string.IsNullOrWhiteSpace(credential.Username) && credential.Username.Length <= 256 &&
        credential.Domain.Length <= 255 && !ContainsControl(credential.Username) && !ContainsControl(credential.Domain) &&
        (credential.Mode switch
        {
            RunAsCredential.Types.Mode.WindowsPassword =>
                credential.Password.Length is > 0 and <= 1024 && !credential.Password.Contains('\0') &&
                (credential.ExpectedAccountId.Length == 0 || IsValidWindowsSid(credential.ExpectedAccountId)),
            RunAsCredential.Types.Mode.UnixPrivilegedLocal =>
                credential.Domain.Length == 0 && credential.Password.Length == 0 &&
                (credential.ExpectedAccountId.Length == 0 || ObservedExecutionIdentity.IsUnixAccountId(credential.ExpectedAccountId)),
            _ => false
        });

    private static bool IsValidWindowsSid(string sid) => sid.Length <= 184 && ObservedExecutionIdentity.IsWindowsAccountId(sid);

    private static bool ContainsControl(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }

    public static void Clear(RunAsCredential? credential)
    {
        if (credential is null) return;
        credential.Username = credential.Domain = credential.Password = "";
    }
}
