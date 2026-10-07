using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetRatel.Client.Service.Updates;

internal static class ProtectedUpdateState
{
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
                bool Trusted(string? sid) => sid is not null && (sid == current || sid is "S-1-5-18" or "S-1-5-32-544");
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
