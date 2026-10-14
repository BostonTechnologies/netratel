using System.Text;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Shared.Connectivity;

/// <summary>Explicit, bounded secret-file support. Error messages never contain file contents.</summary>
public static class SecretFileReference
{
    /// <summary>Canonical keys win within a provider; a higher provider replaces the lower secret alternative.</summary>
    public static (string Inline, string File) Select(IConfiguration configuration, string inlineKey, string fileKey,
        params string[] fileAliases)
    {
        var inline = string.Empty;
        var file = string.Empty;
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                var hasInline = provider.TryGet(inlineKey, out var inlineValue);
                var hasFile = provider.TryGet(fileKey, out var fileValue);
                if (!hasFile)
                    foreach (var alias in fileAliases)
                        if (provider.TryGet(alias, out fileValue)) { hasFile = true; break; }
                if (hasInline) inline = inlineValue ?? string.Empty;
                if (hasFile) file = fileValue ?? string.Empty;
                if (hasInline && !hasFile) file = string.Empty;
                if (hasFile && !hasInline) inline = string.Empty;
            }
        }
        else
        {
            inline = configuration[inlineKey] ?? string.Empty;
            file = configuration[fileKey] ?? fileAliases.Select(alias => configuration[alias]).FirstOrDefault(value => value is not null) ?? string.Empty;
        }
        return (inline, file);
    }

    public static string Resolve(string? inline, string? path, string setting)
    {
        if (!string.IsNullOrEmpty(inline) && !string.IsNullOrEmpty(path))
            throw new InvalidOperationException($"{setting} must select either an inline secret or a secret file.");
        return string.IsNullOrEmpty(path) ? inline ?? string.Empty : Read(path, setting);
    }

    public static string Read(string path, string setting, int maximumBytes = 16_384)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{setting} secret file must use an absolute path.");
        try
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"{setting} secret file cannot traverse symbolic links.");
            }
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException($"{setting} secret file must not be writable by other users.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 || stream.Length > maximumBytes)
                throw new InvalidOperationException($"{setting} secret file is empty or exceeds the size limit.");
            var buffer = new byte[maximumBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > maximumBytes)
                throw new InvalidOperationException($"{setting} secret file exceeds the size limit.");
            var value = new UTF8Encoding(false, true).GetString(buffer, 0, length).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new InvalidOperationException($"{setting} secret file must contain one non-empty value.");
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            throw new InvalidOperationException($"{setting} secret file could not be read securely.");
        }
    }
}
