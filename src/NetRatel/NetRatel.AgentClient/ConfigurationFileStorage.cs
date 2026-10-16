using System.Security.Cryptography;
using System.Text;

namespace NetRatel.AgentClient;

/// <summary>Bounded, non-symlink configuration IO for native and headless/container setup.</summary>
public static class ConfigurationFileStorage
{
    public static string ReadText(string path, int maximumBytes = 65_536, bool secret = false)
    {
        try
        {
            EnsureSafePath(path);
            if (!File.Exists(path)) throw new AgentClientValidationException("The selected configuration or secret file does not exist.");
            if (!OperatingSystem.IsWindows() && secret &&
                (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new AgentClientValidationException("Secret files must not be writable by other users; use an owner-controlled file or read-only secret mount.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw new AgentClientValidationException("The selected configuration or secret file exceeds the allowed size.");
            var buffer = new byte[maximumBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            if (read > maximumBytes) throw new AgentClientValidationException("The selected configuration or secret file exceeds the allowed size.");
            var offset = read >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(buffer, offset, read - offset);
        }
        catch (AgentClientValidationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            throw new AgentClientValidationException("The selected configuration or secret file cannot be read. Check its path, ownership and permissions.");
        }
    }

    public static string Hash(string path) => Hash(path, 65_536);
    public static string Hash(string path, int maximumBytes) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadText(path, maximumBytes))));

    public static bool IsWritable(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            EnsureSafePath(path);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return false;
            // Opening without a write detects a read-only mount without changing its contents.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void WriteAtomic(string path, string content, bool replace = false, string? expectedHash = null, int maximumBytes = 65_536)
    {
        var fullPath = Path.GetFullPath(path);
        string? temporaryPath = null;
        string? lockPath = null;
        var ownsLock = false;
        try
        {
            EnsureSafePath(fullPath);
            var directory = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            EnsureSafePath(directory);
            lockPath = fullPath + ".lock";
            EnsureSafePath(lockPath);
            using var writeLock = CreatePrivate(lockPath);
            ownsLock = true;
            var existed = File.Exists(fullPath);
            if (existed && !replace) throw new AgentClientValidationException("Configuration already exists. Review and explicitly confirm replacement.");
            if (existed && !IsWritable(fullPath))
                throw new AgentClientValidationException("The selected configuration output is read-only or externally managed. Export a separate reviewed file/overlay and apply it through the deployment owner.");
            if (existed && !OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(fullPath) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new AgentClientValidationException("Existing configuration is writable by another user. Correct ownership and permissions before replacement.");
            var before = existed ? Hash(fullPath, maximumBytes) : string.Empty;
            if (expectedHash is not null && !string.Equals(expectedHash, before, StringComparison.Ordinal))
                throw new AgentClientValidationException("Configuration changed since it was reviewed. Reload before saving.");
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = CreatePrivate(temporaryPath))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            EnsureSafePath(fullPath);
            if (existed != File.Exists(fullPath) || existed && !string.Equals(before, Hash(fullPath, maximumBytes), StringComparison.Ordinal))
                throw new AgentClientValidationException("Configuration changed while saving. Reload before saving.");
            File.Move(temporaryPath, fullPath, overwrite: existed);
            temporaryPath = null;
        }
        catch (AgentClientValidationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AgentClientValidationException("Configuration cannot be saved. Check writable output, ownership, and whether another setup process is saving.");
        }
        finally
        {
            if (temporaryPath is not null) File.Delete(temporaryPath);
            // Only delete a lock that this invocation successfully created.
            if (ownsLock && lockPath is not null) File.Delete(lockPath);
        }
    }

    private static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public static void EnsureSafePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AgentClientValidationException("Configuration and secret paths must not traverse symbolic links.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
}
