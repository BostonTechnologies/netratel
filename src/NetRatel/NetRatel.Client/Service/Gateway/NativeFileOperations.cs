using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Native, no-copy moves and bounded deletion. No Web filesystem is involved.</summary>
internal static class NativeFileOperations
{
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

    internal static void DeleteTree(string path, CancellationToken cancellationToken, Action<long> progress)
    {
        const long maximumEntries = 100_000;
        const int maximumDepth = 128;
        long affected = 0;
        var stack = new Stack<(string Path, IEnumerator<string> Entries)>();
        try
        {
            CheckLink(path);
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

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
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
