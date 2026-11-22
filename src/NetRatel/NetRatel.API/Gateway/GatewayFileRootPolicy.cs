using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.API.Gateway;

internal static class GatewayFileRootPolicy
{
    public static IReadOnlyList<string> ReadConfiguredRoots(IConfiguration? configuration)
    {
        var configured = configuration?.GetSection("NetRatel:FileGateway:AllowedRoots").Get<string[]>() ?? [];
        if (configured.Length > 64)
            throw new InvalidOperationException("Configure at most 64 file gateway roots.");

        return configured.Select(value => RemoteFilePath.TryNormalizeDirectory(value, out var root)
            ? root
            : throw new InvalidOperationException("File gateway roots must be absolute native client directories."))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string[] AdmitPath(string path, IReadOnlyList<string> configured,
        IReadOnlyList<string>? approved = null)
    {
        if (!RemoteFilePath.TryNormalizeDirectory(path, out var candidate))
            return [];

        var matches = configured.Where(root => RemoteFilePath.IsWithin(candidate, root));
        if (approved is null)
            return matches.ToArray();

        // A reviewed operation can narrow the instance boundary, including
        // the independent source and destination scopes for a relocation.
        return matches.SelectMany(configuredRoot => approved
                .Where(approvedRoot => RemoteFilePath.IsWithin(candidate, approvedRoot))
                .Select(approvedRoot => RemoteFilePath.IsWithin(approvedRoot, configuredRoot) ? approvedRoot : configuredRoot))
            .Distinct(StringComparer.Ordinal).ToArray();
    }
}
