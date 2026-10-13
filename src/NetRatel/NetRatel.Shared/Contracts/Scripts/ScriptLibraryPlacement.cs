using NetRatel.Shared.Contracts.Execution;

namespace NetRatel.Shared.Contracts.Scripts;

/// <summary>Logical library paths shared by the editor and script service; these are not filesystem paths.</summary>
public static class ScriptLibraryPlacement
{
    public const int MaximumNameLength = 200;
    public const int MaximumFolderLength = 512;
    private const string InvalidCharacters = "<>:\"|?*";

    public static string NormalizeName(string? name)
    {
        if (name?.Any(char.IsControl) == true) throw new ArgumentException("Name cannot contain control characters.");
        var value = name?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new ArgumentException("Enter a script name.");
        if (value.Length > MaximumNameLength) throw new ArgumentException($"Name must be {MaximumNameLength} characters or fewer.");
        ValidateSegment(value, "Name");
        return value;
    }

    public static string NormalizeFolder(string? path)
    {
        if (path?.Any(char.IsControl) == true) throw new ArgumentException("Folder cannot contain control characters.");
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var segments = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim()).ToArray();
        foreach (var segment in segments) ValidateSegment(segment, "Folder");
        var normalized = segments.Length == 0 ? "/" : "/" + string.Join('/', segments) + "/";
        if (normalized.Length > MaximumFolderLength) throw new ArgumentException($"Folder must be {MaximumFolderLength} characters or fewer.");
        return normalized;
    }

    public static IReadOnlyList<string> FolderChain(string? path)
    {
        var folder = NormalizeFolder(path);
        var result = new List<string> { "/" };
        var current = "/";
        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += segment + "/";
            result.Add(current);
        }
        return result;
    }

    private static void ValidateSegment(string value, string field)
    {
        if (value.Length == 0 || value is "." or "..") throw new ArgumentException($"{field} cannot contain empty or traversal segments.");
        if (value.Any(character => char.IsControl(character) || character is '/' or '\\' || InvalidCharacters.Contains(character)))
            throw new ArgumentException($"{field} contains an invalid character.");
    }
}

public sealed record LibraryScriptType(ScriptType Value, string Label, string Language, string Extension);

public static class ScriptLibraryTypes
{
    public static IReadOnlyList<LibraryScriptType> Options { get; } = [
        new(ScriptType.PowerShell, "PowerShell", "powershell", ".ps1"),
        new(ScriptType.Bash, "Bash", "shell", ".sh"),
        new(ScriptType.Python, "Python", "python", ".py"),
        new(ScriptType.JavaScript, "JavaScript", "javascript", ".js"),
        new(ScriptType.TypeScript, "TypeScript", "typescript", ".ts"),
        new(ScriptType.Sql, "SQL", "sql", ".sql"),
        new(ScriptType.Json, "JSON", "json", ".json")
    ];

    public static LibraryScriptType? Find(string? storedType) =>
        Options.FirstOrDefault(option => string.Equals(option.Value.ToString(), storedType, StringComparison.OrdinalIgnoreCase)
            || option.Value == ScriptType.Bash && string.Equals(storedType, "shell", StringComparison.OrdinalIgnoreCase));
}
