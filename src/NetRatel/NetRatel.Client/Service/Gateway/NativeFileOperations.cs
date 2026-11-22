using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Native, no-copy moves and bounded deletion. No Web filesystem is involved.</summary>
internal static class NativeFileOperations
{
    internal static void CreateDirectory(string path)
    {
        var success = OperatingSystem.IsWindows() ? CreateDirectoryWindows(path, IntPtr.Zero) :
            OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? MakeDirectory(path, 511) == 0 : throw new NotSupportedException();
        if (success) return;
        var error = Marshal.GetLastPInvokeError();
        throw new NativeFileOperationException(OperatingSystem.IsWindows()
            ? error is 80 or 183 ? "path_already_exists" : error == 5 ? "access_denied" : "io_failure"
            : error == 17 ? "path_already_exists" : error is 1 or 13 ? "access_denied" : "io_failure");
    }

    internal static FileStream OpenForRead(string path, int bufferSize)
    {
        if (!OperatingSystem.IsLinux())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

        // Nonblocking open avoids waiting on special files. Validate the actual
        // opened object before exposing its contents, even if a path changed.
        const int nonBlockingCloseOnExec = 0x00800 | 0x80000;
        var descriptor = OpenLinux(path, nonBlockingCloseOnExec);
        if (descriptor < 0)
        {
            throw new NativeFileOperationException(Marshal.GetLastPInvokeError() switch
            {
                2 => "file_not_found",
                1 or 13 => "access_denied",
                _ => "io_failure"
            });
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            const int emptyPath = 0x1000;
            const uint fileTypeAndLinks = 0x0001 | 0x0004;
            const int regularFileType = 0x8000;
            var target = File.ResolveLinkTarget($"/proc/self/fd/{descriptor}", returnFinalTarget: false);
            if (target is null || !string.Equals(target.FullName, path, StringComparison.Ordinal) ||
                AgentFileProtectedPaths.IsProtected(target.FullName) ||
                Statx(descriptor, "", emptyPath, fileTypeAndLinks, out var info) != 0 ||
                (info.Fields & fileTypeAndLinks) != fileTypeAndLinks ||
                (info.Mode & 0xf000) != regularFileType || info.LinkCount != 1)
                throw new NativeFileOperationException("protected_path");

            return new FileStream(handle, FileAccess.Read, bufferSize);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsProtectedWindowsPath(string path)
    {
        var buffer = new StringBuilder(4096);
        var size = GetLongPathName(path, buffer, buffer.Capacity);
        return size == 0 || size >= buffer.Capacity || AgentFileProtectedPaths.IsProtected(buffer.ToString());
    }

    internal static string ObservedVersion(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The remote file no longer exists.", path);
        return $"v1:{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}";
    }

    internal static void Move(string source, string destination)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, StringComparison.Ordinal) ||
            destination.StartsWith(Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar, comparison))
            throw new NativeFileOperationException("invalid_destination");
        var caseRename = OperatingSystem.IsWindows() && string.Equals(source, destination, comparison);
        if (!caseRename && (File.Exists(destination) || Directory.Exists(destination)))
            throw new NativeFileOperationException("destination_already_exists");

        // File.Move may silently copy/delete across Unix volumes. Native rename
        // with exclusive destination semantics refuses EXDEV before any effects.
        int error;
        if (OperatingSystem.IsWindows())
        {
            if (MoveFileEx(source, destination, 0)) return;
            error = Marshal.GetLastPInvokeError();
            throw new NativeFileOperationException(error switch
            {
                17 => "cross_volume_unsupported", 80 or 183 => "destination_already_exists",
                5 => "access_denied", _ => "io_failure"
            });
        }
        if (OperatingSystem.IsLinux())
        {
            if (RenameAt2(-100, source, -100, destination, 1) == 0) return;
            error = Marshal.GetLastPInvokeError();
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (RenameExclusive(source, destination, 4) == 0) return;
            error = Marshal.GetLastPInvokeError();
        }
        else throw new NativeFileOperationException("capability_unavailable");
        throw new NativeFileOperationException(error switch
        {
            18 => "cross_volume_unsupported", 17 => "destination_already_exists",
            1 or 13 => "access_denied", 38 or 95 => "capability_unavailable", _ => "io_failure"
        });
    }

    internal static void DeleteTree(string path, CancellationToken cancellationToken, Action<long> progress,
        Func<string, bool>? isAllowed = null)
    {
        const long maximumEntries = 100_000;
        const int maximumDepth = 128;
        long affected = 0;
        var volumeRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var drive in DriveInfo.GetDrives()) volumeRoots.Add(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName));
        var stack = new Stack<(string Path, IEnumerator<string> Entries)>();
        try
        {
            CheckLink(path);
            if (isAllowed?.Invoke(path) == false)
                throw new NativeFileOperationException("protected_path");
            stack.Push((path, Directory.EnumerateFileSystemEntries(path).GetEnumerator()));
            while (stack.TryPeek(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (affected >= maximumEntries) throw new NativeFileOperationException("deletion_limit", affected);
                CheckLink(current.Path);
                if (current.Entries.MoveNext())
                {
                    var child = current.Entries.Current;
                    CheckLink(child);
                    if (isAllowed?.Invoke(child) == false || volumeRoots.Contains(Path.TrimEndingDirectorySeparator(child)))
                        throw new NativeFileOperationException("protected_path", affected);
                    if (Directory.Exists(child))
                    {
                        if (stack.Count >= maximumDepth) throw new NativeFileOperationException("deletion_limit", affected);
                        stack.Push((child, Directory.EnumerateFileSystemEntries(child).GetEnumerator()));
                    }
                    else
                    {
                        File.Delete(child);
                        progress(++affected);
                    }
                }
                else
                {
                    stack.Pop().Entries.Dispose();
                    Directory.Delete(current.Path, false);
                    progress(++affected);
                }
            }
        }
        finally
        {
            while (stack.TryPop(out var item)) item.Entries.Dispose();
        }
    }

    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NativeFileOperationException("protected_path");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Fields;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(28)] internal ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileStatus status);

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory(string path, uint mode);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectory, string source, int newDirectory, string destination, uint flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameExclusive(string source, string destination, uint flags);
}

internal sealed class NativeFileOperationException(string code, long affectedEntries = 0) : IOException(code)
{
    public string Code { get; } = code;
    public long AffectedEntries { get; } = affectedEntries;
}
