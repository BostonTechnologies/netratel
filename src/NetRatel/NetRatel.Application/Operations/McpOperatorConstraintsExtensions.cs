namespace NetRatel.Application.Operations;

/// <summary>Shared interpretation of explicit policy directory grants.</summary>
public static class McpOperatorConstraintsExtensions
{
    /// <summary>
    /// Exact directory grants retain their existing semantics. An explicit '*'
    /// grants any directory; it does not disable request or client path validation.
    /// </summary>
    public static bool AllowsWorkingDirectory(this IReadOnlyList<string> allowed, string? directory) =>
        directory is { Length: > 0 and <= 4096 } && !directory.Any(char.IsControl) &&
        (allowed.Contains("*", StringComparer.Ordinal) || allowed.Contains(directory, StringComparer.Ordinal));
}
