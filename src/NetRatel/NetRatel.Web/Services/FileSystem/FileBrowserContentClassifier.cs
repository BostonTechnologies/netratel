using System.Text;

namespace NetRatel.Web.Services.FileSystem;

public sealed record FileBrowserContentClassification(
    bool IsText,
    string ViewerKind,
    string Language,
    string ContentType);

/// <summary>
/// Classifies already-bounded file content for the remote file viewer.
/// This deliberately uses remote names only; it never interprets a remote path
/// through the operating system hosting NetRatel.Web.
/// </summary>
public sealed class FileBrowserContentClassifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public FileBrowserContentClassification Classify(string? fileName, string? reportedContentType, ReadOnlySpan<byte> sample)
    {
        var name = GetName(fileName);
        var language = ResolveLanguage(name);
        var contentType = NormalizeContentType(reportedContentType, language);

        if (TryGetPreviewImageContentType(name, out var imageContentType))
        {
            return new(false, "image", "plaintext", imageContentType);
        }

        if (IsKnownBinary(name, contentType) || !LooksLikeText(sample))
        {
            return new(false, "binary", "plaintext", contentType);
        }

        return new(true, "text", language, contentType);
    }

    public static string ResolveLanguage(string? fileName) => GetName(fileName) switch
    {
        "dockerfile" => "dockerfile",
        "makefile" => "makefile",
        "hosts" or ".gitignore" or ".editorconfig" => "plaintext",
        _ => GetExtension(GetName(fileName)) switch
        {
            ".txt" or ".log" => "plaintext",
            ".md" => "markdown",
            ".json" or ".jsonc" => "json",
            ".xml" => "xml",
            ".yaml" or ".yml" => "yaml",
            ".toml" => "ini",
            ".ini" or ".cfg" or ".conf" or ".properties" or ".env" => "plaintext",
            ".config" => "xml",
            ".service" or ".timer" => "ini",
            ".sh" => "shell",
            ".ps1" => "powershell",
            ".bat" or ".cmd" => "bat",
            ".cs" => "csharp",
            ".razor" or ".html" => "html",
            ".css" => "css",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".sql" => "sql",
            ".py" => "python",
            _ => "plaintext"
        }
    };

    public static bool TryGetPreviewImageContentType(string? fileName, out string contentType)
    {
        contentType = GetExtension(GetName(fileName)) switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(contentType);
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(sample);
            var controlCharacters = 0;
            foreach (var value in sample)
            {
                if (value < 0x09 || (value > 0x0d && value < 0x20))
                {
                    controlCharacters++;
                }
            }

            return controlCharacters * 20 <= sample.Length;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsKnownBinary(string fileName, string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
        GetExtension(fileName) is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".pdf" or ".zip" or ".gz" or ".7z" or ".exe" or ".dll";

    private static string NormalizeContentType(string? reportedContentType, string language)
    {
        if (!string.IsNullOrWhiteSpace(reportedContentType) &&
            !string.Equals(reportedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return reportedContentType;
        }

        return language switch
        {
            "json" => "application/json",
            "xml" or "html" => "application/xml",
            "yaml" => "application/yaml",
            _ => "text/plain"
        };
    }

    private static string GetName(string? fileName)
    {
        var value = fileName ?? string.Empty;
        var index = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return value[(index + 1)..].ToLowerInvariant();
    }

    private static string GetExtension(string fileName)
    {
        var index = fileName.LastIndexOf('.');
        return index <= 0 ? string.Empty : fileName[index..];
    }
}
