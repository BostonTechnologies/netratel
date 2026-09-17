// .Client/Services/FileSystemService.cs
using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NetRatel.Client.Services
{
    public class FileSystemService
    {
        public sealed record BrowseEntry(
            string ParentPath,
            string Name,
            string FullPath,
            bool IsDirectory,
            long SizeBytes);

        public IEnumerable<BrowseEntry> EnumerateEntries(string path)
        {
            var directoryInfo = new DirectoryInfo(path);
            if (!directoryInfo.Exists)
            {
                throw new DirectoryNotFoundException(path);
            }

            foreach (var dir in directoryInfo.EnumerateDirectories())
            {
                yield return new BrowseEntry(path, dir.Name, dir.FullName, true, 0);
            }

            foreach (var file in directoryInfo.EnumerateFiles())
            {
                long fileSize = -1;
                try { fileSize = file.Length; } catch { }
                yield return new BrowseEntry(path, file.Name, file.FullName, false, fileSize);
            }
        }

        public async Task<byte[]> ReadFileBytesAsync(string path)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("File not found.", path);
            }

            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }

        public async Task WriteFileBytesAsync(string path, byte[] content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, content).ConfigureAwait(false);
        }

        public static string DecodeUtf8(byte[] content) => Encoding.UTF8.GetString(content);
        public static byte[] EncodeUtf8(string content) => Encoding.UTF8.GetBytes(content ?? string.Empty);

        /// <summary>
        /// Validates whether the specified path is a valid and safe file system path.
        /// </summary>
        /// <remarks>This method performs basic validation to ensure the path is well-formed and does not
        /// contain  potentially unsafe components, such as directory traversal sequences (".."). It also checks  for
        /// operating system-specific path requirements, such as valid drive letter formats on Windows  or root-based
        /// paths on Unix-like systems. <para> Note that this method does not verify the existence of the path or its
        /// accessibility. It is  intended for preliminary validation and may not account for all edge cases or
        /// platform-specific  nuances. </para></remarks>
        /// <param name="path">The file system path to validate. This can be an absolute or relative path, depending on the operating
        /// system.</param>
        /// <returns><see langword="true"/> if the specified path is valid and adheres to basic safety checks;  otherwise, <see
        /// langword="false"/>.</returns>
        public bool IsValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            // 1. Disallow ".." components to prevent traversal
            if (path.Contains("..")) return false;

            // 2. Check for valid drive letter format (Windows specific example)
            //    Adapt for Linux if needed (e.g., must start with '/')
            //    This assumes the client OS is known or paths are formatted accordingly.
            //    A more robust check might involve getting valid drive letters from the system.
            if (OperatingSystem.IsWindows()) // Use OperatingSystem class in .NET 5+
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[a-zA-Z]:\\"))
                {
                    // Allow root path like "C:\"
                    if (path.Length != 3 || path[1] != ':' || path[2] != '\\')
                    {
                        // Check if it's a subdirectory under a valid root
                        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[a-zA-Z]:\\.+"))
                        {
                            return false; // Doesn't match C:\ or C:\sub\folder...
                        }
                    }
                }
                // Optional: Check against actual system drives?
                // DriveInfo.GetDrives().Any(d => path.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) // Basic Unix-like check
            {
                if (!path.StartsWith("/")) return false;
                // Add more Linux/Mac specific checks if needed (e.g., disallow certain system paths?)
            }
            else
            {
                // Unknown OS - perhaps disallow? Or use generic checks?
                LogManager.WriteLog("[FS Validate] Unknown OS, path validation might be incomplete.");
            }


            // 3. Check for invalid path characters (less critical if DirectoryInfo/FileInfo handle it, but good practice)
            // char[] invalidChars = Path.GetInvalidPathChars(); // Might be too strict?
            // if (path.Any(c => invalidChars.Contains(c))) return false;


            // Add more checks as needed (e.g., blacklist specific sensitive directories?)

            return true; // Passed basic checks
        }

        /// <summary>
        /// Resolves a policy-constrained gateway path on the machine that owns
        /// the filesystem. Every existing component is checked for a link or
        /// reparse target, and the resolved path must remain beneath a
        /// resolved approved root. The API cannot safely perform this check on
        /// behalf of a remote agent.
        /// </summary>
        public bool TryResolveGatewayPath(
            string path,
            IReadOnlyList<string> allowedRoots,
            bool requireExisting,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || allowedRoots is null || allowedRoots.Count is 0 or > 64)
            {
                return false;
            }

            foreach (var root in allowedRoots)
            {
                if (TryResolveGatewayPathUnderRoot(path, root, requireExisting, out resolvedPath))
                {
                    return true;
                }
            }

            resolvedPath = string.Empty;
            return false;
        }

        private static bool TryResolveGatewayPathUnderRoot(
            string path,
            string root,
            bool requireExisting,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(root) || root.Length > 4096 || path.Length > 4096)
            {
                return false;
            }

            string requestedPath;
            string configuredRoot;
            try
            {
                requestedPath = Path.GetFullPath(path);
                configuredRoot = Path.GetFullPath(root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!IsPathWithin(requestedPath, configuredRoot) ||
                !TryResolveExistingPath(configuredRoot, out var resolvedRoot) ||
                !Directory.Exists(resolvedRoot))
            {
                return false;
            }

            var relative = Path.GetRelativePath(configuredRoot, requestedPath);
            if (relative == ".")
            {
                resolvedPath = resolvedRoot;
                return true;
            }

            var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = resolvedRoot;
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component is "." or ".." || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return false;
                }

                var candidate = Path.Combine(current, component);
                var exists = Directory.Exists(candidate) || File.Exists(candidate);
                if (!exists)
                {
                    if (requireExisting)
                    {
                        return false;
                    }

                    for (var remaining = index + 1; remaining < components.Length; remaining++)
                    {
                        if (components[remaining] is "." or ".." || components[remaining].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        {
                            return false;
                        }

                        candidate = Path.Combine(candidate, components[remaining]);
                    }

                    resolvedPath = candidate;
                    return IsPathWithin(resolvedPath, resolvedRoot);
                }

                if (!TryResolveExistingPath(candidate, out current) || !IsPathWithin(current, resolvedRoot))
                {
                    return false;
                }

                if (index < components.Length - 1 && !Directory.Exists(current))
                {
                    return false;
                }
            }

            resolvedPath = current;
            return !requireExisting || File.Exists(resolvedPath) || Directory.Exists(resolvedPath);
        }

        private static bool TryResolveExistingPath(string path, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            FileSystemInfo? current = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : File.Exists(path)
                    ? new FileInfo(path)
                    : null;
            if (current is null)
            {
                return false;
            }

            for (var remainingLinks = 32; remainingLinks > 0; remainingLinks--)
            {
                if (current.LinkTarget is null)
                {
                    try
                    {
                        resolvedPath = Path.GetFullPath(current.FullName);
                        return true;
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return false;
                    }
                }

                current = current.ResolveLinkTarget(returnFinalTarget: true);
                if (current is null)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsPathWithin(string path, string root)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(path, root, comparison))
            {
                return true;
            }

            var separator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            return path.StartsWith(separator, comparison);
        }

    }
}
