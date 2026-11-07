using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Client.Service.Gateway;

internal sealed record LinuxMountEntry(string Device, string Root, string Mount, string[] Options,
    string[] OptionalFields, string FileSystem, string Source, string[] SuperOptions);
internal sealed record ClassifiedLinuxMount(LinuxMountEntry Mount, TelemetryDiskMetadataDto Metadata);

/// <summary>Reads this process's namespace; mount IDs are deliberately never used as capacity identity.</summary>
internal static class LinuxMountInventory
{
    private static readonly HashSet<string> Pseudo = new(StringComparer.Ordinal)
    { "proc", "sysfs", "devpts", "devtmpfs", "cgroup", "cgroup2", "securityfs", "debugfs", "tracefs", "pstore", "mqueue", "hugetlbfs", "fusectl", "configfs", "autofs", "binfmt_misc" };

    public static IReadOnlyList<ClassifiedLinuxMount> Read(out bool truncated)
    {
        var entries = new List<LinuxMountEntry>(); var bytes = 0; truncated = false;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            bytes += line.Length;
            if (entries.Count >= 4096 || bytes > 1024 * 1024) { truncated = true; break; }
            if (TryParse(line, out var entry)) entries.Add(entry!);
            else truncated = true;
        }
        return Classify(entries);
    }

    internal static bool TryParse(string line, out LinuxMountEntry? entry)
    {
        entry = null;
        if (line.Length > 8192) return false;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var separator = Array.IndexOf(fields, "-");
        if (separator < 6 || fields.Length != separator + 4 || !int.TryParse(fields[0], out _) ||
            !int.TryParse(fields[1], out _) || fields[2].Split(':') is not [var major, var minor] ||
            !uint.TryParse(major, out _) || !uint.TryParse(minor, out _)) return false;
        var root = Unescape(fields[3]); var rawMount = Unescape(fields[4]);
        var mount = GatewayTelemetrySnapshotCollector.NormalizeDiskScope(rawMount);
        var source = Unescape(fields[separator + 2]); var fs = fields[separator + 1];
        if (mount.Length is 0 or > 128 || mount != rawMount || mount.Any(char.IsControl) || root.Length is 0 or > 256 || source.Length is 0 or > 256 || fs.Length is 0 or > 32 ||
            root.Any(char.IsControl) || source.Any(char.IsControl) || fs.Any(char.IsControl)) return false;
        entry = new(fields[2], root, mount, fields[5].Split(',').Select(Unescape).ToArray(),
            fields[6..separator], fs, source, fields[separator + 3].Split(',').Select(Unescape).ToArray());
        return true;
    }

    internal static IReadOnlyList<ClassifiedLinuxMount> Classify(IEnumerable<LinuxMountEntry> entries, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var mounts = entries.Select(mount =>
        {
            var readOnly = mount.Options.Contains("ro", StringComparer.Ordinal) || mount.SuperOptions.Contains("ro", StringComparer.Ordinal);
            string? reason = Pseudo.Contains(mount.FileSystem) ? "pseudo-filesystem" :
                readOnly && mount.FileSystem is "squashfs" or "erofs" ? "package-image" : null;
            return new ClassifiedLinuxMount(mount, new(mount.FileSystem, mount.Source, mount.Root, Capacity(mount), readOnly, reason));
        }).OrderBy(item => item.Mount.Mount.Length).ThenBy(item => item.Mount.Mount, StringComparer.Ordinal).ToArray();
        // Resolve representatives against the complete set, so enumeration and mount path lengths cannot change backing identity.
        var representatives = mounts.Where(item => item.Metadata.DefaultExclusionReason is null)
            .GroupBy(item => item.Metadata.CapacityKey).ToDictionary(group => group.Key, group => group.First());
        var result = new List<ClassifiedLinuxMount>();
        foreach (var item in mounts)
        {
            var mount = item.Mount; var metadata = item.Metadata;
            if (metadata.DefaultExclusionReason is null && representatives[metadata.CapacityKey] is { } representative &&
                representative.Mount.Mount != mount.Mount && mount.Mount != "/")
                metadata = metadata with { DefaultExclusionReason = "duplicate-capacity" };
            // An overlay reports upper-filesystem capacity. A visible upperdir plus the longest authoritative containing mount
            // proves its backing; a container's root overlay remains independently meaningful.
            if (metadata.DefaultExclusionReason is null && mount.FileSystem == "overlay" && mount.Mount != "/")
            {
                var upper = mount.SuperOptions.FirstOrDefault(option => option.StartsWith("upperdir=", StringComparison.Ordinal))?[9..];
                var backing = upper is null ? null : mounts.Where(other => other.Metadata.DefaultExclusionReason is null &&
                    other.Mount.FileSystem != "overlay" && Contains(other.Mount.Mount, upper))
                    .OrderByDescending(other => other.Mount.Mount.Length).FirstOrDefault();
                if (backing is not null && directoryExists(upper!))
                    metadata = metadata with { DefaultExclusionReason = "duplicate-capacity", CapacityKey = backing.Metadata.CapacityKey };
            }
            result.Add(new(mount, metadata));
        }
        return result.OrderBy(item => item.Metadata.DefaultExclusionReason is not null)
            .ThenBy(item => item.Mount.Mount, StringComparer.Ordinal).ToArray();
    }

    private static bool Contains(string mount, string path) => path == mount ||
        path.StartsWith(mount == "/" ? mount : mount + "/", StringComparison.Ordinal);
    private static string Capacity(LinuxMountEntry mount) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\0', mount.Device, mount.FileSystem, mount.Source, mount.Root))));
    private static string Unescape(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 3 < value.Length && value[index + 1] is >= '0' and <= '7' &&
                value[index + 2] is >= '0' and <= '7' && value[index + 3] is >= '0' and <= '7')
            { decoded.Append((char)((value[index + 1] - '0') * 64 + (value[index + 2] - '0') * 8 + value[index + 3] - '0')); index += 3; }
            else decoded.Append(value[index]);
        }
        return decoded.ToString();
    }
}
