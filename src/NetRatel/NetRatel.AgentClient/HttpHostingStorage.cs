using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Keep host-side group-readable runtime exports behind a private owner boundary.</summary>
    public static void ValidateHostBoundary(string path)
    {
        ConfigurationFileStorage.EnsureSafePath(path);
        if (OperatingSystem.IsWindows())
            throw new AgentClientValidationException("Native Windows host generation is unavailable because this tool cannot verify a private Windows ACL boundary. On the deployment host run: docker compose -f compose.setup.yaml run --rm --no-deps mcp-http setup --mode host. Personal client setup remains supported.");
        var suppliedVolume = UsesValidatedSetupVolume(path);
        if (suppliedVolume && (RuntimeGroup != 10001 || GetEffectiveUserId() != 10001))
            throw new AgentClientValidationException("Use the supplied setup container identity (UID/GID 10001) for its protected runtime volume.");
        if (!Directory.Exists(path)) return; // Atomic storage creates new directories as owner-only.
        var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (suppliedVolume) allowed |= UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        if ((File.GetUnixFileMode(path) & ~allowed) != 0 || DirectoryOwner(path) != GetEffectiveUserId())
            throw new AgentClientValidationException("Host setup requires an owner-only output directory owned by this user. Use the per-user default, a new private Advanced directory, or the supplied compose.setup.yaml volume; existing permissions were not changed.");
    }

    private static uint DirectoryOwner(string path)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsLinux()) { start.ArgumentList.Add("--format=%u"); start.ArgumentList.Add("--"); }
            else { start.ArgumentList.Add("-f"); start.ArgumentList.Add("%u"); }
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(2000)) { process.Kill(entireProcessTree: true); throw new IOException(); }
            if (process.ExitCode != 0 || !uint.TryParse(process.StandardOutput.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var owner)) throw new IOException();
            return owner;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AgentClientValidationException("The private output directory ownership could not be verified. Use the supplied compose.setup.yaml volume or restore the system stat utility before retrying.");
        }
    }

    public static bool UsesSetupVolume(string path) =>
        string.Equals(Path.GetFullPath(path), "/setup-output", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETRATEL_SETUP_VOLUME"));

    public static bool UsesValidatedSetupVolume(string path) =>
        IsContainer && UsesSetupVolume(path) && IsMounted(path);

    public static bool UsesClientVolume(string path) =>
        string.Equals(Path.GetFullPath(path), "/var/lib/netratel/clients", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETRATEL_CLIENT_VOLUME"));

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
            (selected.Type != "overlay" || UsesSetupVolume(selected.Mount) || UsesClientVolume(selected.Mount));
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();
}
