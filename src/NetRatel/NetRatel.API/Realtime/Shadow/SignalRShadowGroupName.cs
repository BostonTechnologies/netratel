using System.Security.Cryptography;
using System.Text;
using NetRatel.Application.Fanout;

namespace NetRatel.API.Realtime.Shadow;

internal static class SignalRShadowGroupName
{
    internal const int MaximumLength = 256;
    private const string Prefix = "akka-shadow:v1:tenant:";

    public static bool TryCreate(ShadowFanoutTarget target, out string groupName)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid)
        {
            groupName = string.Empty;
            return false;
        }

        var tenant = HashIdentifier("tenant", target.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        groupName = target.Scope switch
        {
            ShadowFanoutTargetScope.Tenant => $"{Prefix}{tenant}",
            ShadowFanoutTargetScope.Client =>
                $"{Prefix}{tenant}:client:{HashIdentifier("client", target.ClientId!)}",
            ShadowFanoutTargetScope.Terminal =>
                $"{Prefix}{tenant}:client:{HashIdentifier("client", target.ClientId!)}:terminal:{HashIdentifier("terminal", target.SessionId!)}",
            ShadowFanoutTargetScope.RemoteSupport =>
                $"{Prefix}{tenant}:client:{HashIdentifier("client", target.ClientId!)}:remote-support:{HashIdentifier("remote-support", target.SessionId!)}",
            ShadowFanoutTargetScope.FileBrowser =>
                $"{Prefix}{tenant}:client:{HashIdentifier("client", target.ClientId!)}:file-browser:{HashIdentifier("file-browser", target.RequestId!)}",
            ShadowFanoutTargetScope.Job =>
                $"{Prefix}{tenant}:job:{HashIdentifier("job", target.JobId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
            ShadowFanoutTargetScope.Command =>
                $"{Prefix}{tenant}:command:{HashIdentifier("command", target.CommandId!)}",
            _ => string.Empty
        };

        if (groupName.Length is 0 or > MaximumLength)
        {
            groupName = string.Empty;
            return false;
        }

        return true;
    }

    public static bool IsDerivedGroupName(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName) || groupName.Length > MaximumLength)
        {
            return false;
        }

        var components = groupName.Split(':');
        if (components.Length < 4 || components[0] != "akka-shadow" || components[1] != "v1" ||
            components[2] != "tenant" || !IsHash(components[3]))
        {
            return false;
        }

        return components.Length switch
        {
            4 => true,
            6 => components[4] is "client" or "job" or "command" && IsHash(components[5]),
            8 => components[4] == "client" && IsHash(components[5]) &&
                components[6] is "terminal" or "remote-support" or "file-browser" &&
                IsHash(components[7]),
            _ => false
        };
    }

    private static string HashIdentifier(string domain, string value)
    {
        // Length-prefix both components so the hash input is canonical and
        // cannot be made ambiguous by delimiters inside a future identifier.
        var canonical = $"{domain.Length}:{domain}:{value.Length}:{value}";
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), digest);
        return Convert.ToHexString(digest[..12]).ToLowerInvariant();
    }

    private static bool IsHash(string value) =>
        value.Length == 24 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
