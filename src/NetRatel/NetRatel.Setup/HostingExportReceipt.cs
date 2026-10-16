using NetRatel.AgentClient;

namespace NetRatel.Setup;

/// <summary>Bind a launcher's one-use export request to the document explicitly saved this run.</summary>
internal static class HostingExportReceipt
{
    public static void WriteIfRequested(string outputDirectory)
    {
        var requested = Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXPORT_RECEIPT");
        if (string.IsNullOrEmpty(requested)) return;
        var output = Path.GetFullPath(outputDirectory);
        if (!Path.IsPathFullyQualified(requested) || Path.GetDirectoryName(Path.GetFullPath(requested)) != output ||
            !Path.GetFileName(requested).StartsWith(".export-", StringComparison.Ordinal))
            throw new AgentClientValidationException("The setup export receipt must be a private one-use file inside the selected setup volume.");
        var document = Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXPORT_DOCUMENT") ?? "compose.komodo.yaml";
        if (document is not ("compose.komodo.yaml" or "compose.http-host.yaml"))
            throw new AgentClientValidationException("The requested export document is unsupported. Rerun the host launcher for the reviewed deployment manager.");
        var content = ConfigurationFileStorage.ReadText(Path.Combine(output, document), 262144, secret: true);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        ConfigurationFileStorage.WriteAtomic(requested, digest + "\n");
    }
}
