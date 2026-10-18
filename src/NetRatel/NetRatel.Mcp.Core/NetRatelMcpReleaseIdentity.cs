using System.Reflection;

namespace NetRatel.Mcp.Core;

/// <summary>Public build metadata of the MCP host, with no paths or API topology.</summary>
public sealed record NetRatelMcpReleaseIdentity(
    string? ServiceName,
    string? ProductVersion,
    string? InformationalVersion,
    string? AssemblyVersion,
    string? SourceRevision)
{
    public static NetRatelMcpReleaseIdentity ReadAssemblyMetadata(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var productVersion = metadata.FirstOrDefault(attribute => attribute.Key == "ProductVersion")?.Value;
        var sourceRevision = metadata.FirstOrDefault(attribute => attribute.Key == "SourceRevisionId")?.Value;
        // Directory.Build.targets stamps the reviewed public source revision.
        // A local/private Git suffix is not evidence of that public source.
        sourceRevision = IsSourceRevision(sourceRevision) ? sourceRevision : null;
        var separator = informationalVersion?.IndexOf('+') ?? -1;

        return new NetRatelMcpReleaseIdentity(
            assembly.GetName().Name,
            !string.IsNullOrWhiteSpace(productVersion) ? productVersion
                : separator >= 0 ? informationalVersion![..separator] : informationalVersion,
            informationalVersion,
            assembly.GetName().Version?.ToString(),
            sourceRevision?.ToLowerInvariant());
    }

    private static bool IsSourceRevision(string? value)
        => value is { Length: 40 } && value.All(char.IsAsciiHexDigit);
}
