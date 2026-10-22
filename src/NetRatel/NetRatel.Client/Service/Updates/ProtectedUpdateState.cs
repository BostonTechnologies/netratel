using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace NetRatel.Client.Service.Updates;

internal static class ProtectedUpdateState
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static readonly Lazy<string?> WindowsServiceSid = new(ResolveWindowsServiceSid);

    internal static void AtomicWriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsProtectedFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        for (FileSystemInfo? entry = new FileInfo(path); entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (OperatingSystem.IsWindows())
            {
                FileSystemSecurity acl = entry is FileInfo windowsFile ? windowsFile.GetAccessControl() : ((DirectoryInfo)entry).GetAccessControl();
                var current = WindowsIdentity.GetCurrent().User?.Value;
                bool Trusted(string? sid) => IsTrustedWindowsSid(sid, current, WindowsServiceSid.Value);
                if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))?.Value)) return false;
                var writes = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (entry is FileInfo) writes |= FileSystemRights.Write;
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow &&
                        !(entry is DirectoryInfo && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) &&
                        (rule.FileSystemRights & writes) != 0 &&
                        !Trusted(rule.IdentityReference.Value)) return false;
            }
            else if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(entry.FullName);
                var stickyAncestor = entry is DirectoryInfo && (mode & UnixFileMode.StickyBit) != 0;
                if (!stickyAncestor && (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
                if (statx(-100, entry.FullName, 0x100, 0x8, out var owner) != 0 ||
                    (owner.Mask & 0x8) == 0 || owner.UserId != 0 && owner.UserId != geteuid()) return false;
            }
            else return false;
        }
        return true;
    }

    // Match the native updater's allowlist. Windows protects ordinary ancestors
    // with TrustedInstaller, and historical installations may retain the product's
    // service SID. Neither identity makes unrelated users or service SIDs trusted.
    internal static bool IsTrustedWindowsSid(string? sid, string? currentSid, string? serviceSid) =>
        sid is not null && (sid == currentSid || sid == serviceSid ||
            sid is "S-1-5-18" or "S-1-5-32-544" or TrustedInstallerSid);

    private static string? ResolveWindowsServiceSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return new NTAccount("NT SERVICE", "NetRatel.Client").Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            // A console-only installation has no registered product service.
            return null;
        }
    }

    [DllImport("libc")] private static extern uint geteuid();
    // statx has a fixed architecture-independent 256-byte ABI, unlike legacy stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, out Statx value);
}
