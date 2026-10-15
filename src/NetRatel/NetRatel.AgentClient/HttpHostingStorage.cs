using System.Runtime.InteropServices;

namespace NetRatel.AgentClient;

/// <summary>One durable setup location shared by native tooling and the supplied container.</summary>
public static class HttpHostingStorage
{
    public static string Resolve(string? configured = null)
    {
        var path = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable("NETRATEL_SETUP_DIRECTORY") : configured;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (IsContainer)
                throw new AgentClientValidationException("No persistent setup mount is configured. On the deployment host run: docker compose -f compose.setup.yaml run --rm --no-deps mcp-http");
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRatel", "http-host");
        }
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || Path.GetPathRoot(path) == Path.GetFullPath(path))
            throw new AgentClientValidationException("Select a writable application-data directory; the filesystem root is not a setup directory.");
        path = Path.GetFullPath(path);
        ConfigurationFileStorage.EnsureSafePath(path);
        if (IsContainer && !IsMounted(path))
            throw new AgentClientValidationException("The selected directory is in the temporary container layer. Resume with: docker compose -f compose.setup.yaml run --rm --no-deps mcp-http");
        return path;
    }

    public static void ValidateWritable(string path)
    {
        path = Resolve(path);
        var probe = Path.Combine(path, ".netratel-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            ConfigurationFileStorage.WriteAtomic(probe, "storage check\n");
            File.Delete(probe);
        }
        catch (AgentClientValidationException)
        {
            throw new AgentClientValidationException("The setup location is read-only, externally managed or not writable by this user. Keep the managed source intact; use the supplied compose.setup.yaml persistent volume or an Advanced writable export directory.");
        }
    }

    public static bool UsesSetupVolume(string path) =>
        string.Equals(Path.GetFullPath(path), "/setup-output", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETRATEL_SETUP_VOLUME"));

    public static string VolumeName => Environment.GetEnvironmentVariable("NETRATEL_SETUP_VOLUME") ?? "netratel-http-host-setup";
    public static uint RuntimeGroup => OperatingSystem.IsWindows() ? 10001 : GetEffectiveGroupId();
    private static bool IsContainer => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsMounted(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/mountinfo")) return false;
        var selected = File.ReadLines("/proc/self/mountinfo").Select(line =>
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 6 || separator < 0 || separator + 1 >= fields.Length) return (Mount: "", Type: "");
            return (Mount: fields[4].Replace("\\040", " ", StringComparison.Ordinal), Type: fields[separator + 1]);
        }).Where(entry => path == entry.Mount || path.StartsWith(entry.Mount.TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Mount.Length).FirstOrDefault();
        // A Docker local volume may itself live on an overlay-backed daemon
        // filesystem (including Docker-in-Docker). Its separate supplied mount
        // still survives service recreation; the container root layer does not.
        return !string.IsNullOrEmpty(selected.Mount) && selected.Mount != "/" &&
            selected.Type is not ("tmpfs" or "ramfs" or "devtmpfs") &&
            (selected.Type != "overlay" || UsesSetupVolume(path) && selected.Mount == "/setup-output");
    }

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();
}
