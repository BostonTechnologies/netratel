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
        ConfigurationFileStorage.WriteAtomic(requested, ConfigurationFileStorage.Hash(Path.Combine(output, "compose.komodo.yaml")).ToLowerInvariant() + "\n");
    }
}
