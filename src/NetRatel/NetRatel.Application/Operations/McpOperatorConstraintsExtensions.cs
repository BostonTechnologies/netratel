namespace NetRatel.Application.Operations;

/// <summary>Shared interpretation of explicit policy directory grants.</summary>
public static class McpOperatorConstraintsExtensions
{
    /// <summary>Materialize explicit unlimited policy intent for existing operation validators.</summary>
    public static McpOperatorConstraints Effective(this McpOperatorConstraints value) => !value.Unrestricted ? value : new(
        ReadRoots: ["*"], WriteRoots: ["*"], AllowedShells: ["*"], WorkingDirectories: ["*"],
        MaxCommandDurationSeconds: int.MaxValue, MaxTerminalIdleSeconds: 3600, MaxTerminalLifetimeSeconds: 28800,
        MaxConcurrentTerminalSessions: int.MaxValue, MaxConcurrentCommands: int.MaxValue,
        MaxOutputBytes: int.MaxValue, MaxArtifactBytes: int.MaxValue, MaxScriptBytes: int.MaxValue,
        MaxJobTargetCount: int.MaxValue, MaxTaskTargetCount: int.MaxValue, MaxFanOut: int.MaxValue,
        MaxOnboardingCodeLifetimeSeconds: int.MaxValue, MaxOnboardingCodeUses: int.MaxValue,
        DestructiveOperationsAllowed: true, AllowActiveTenantDeletion: true)
    { Unrestricted = true, AllOperations = value.AllOperations, MaxConcurrentJobTargets = int.MaxValue };

    public static bool AllowsShell(this IReadOnlyList<string> allowed, string shell) =>
        allowed.Contains("*", StringComparer.Ordinal) || allowed.Contains(shell, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exact directory grants retain their existing semantics. An explicit '*'
    /// grants any directory; it does not disable request or client path validation.
    /// </summary>
    public static bool AllowsWorkingDirectory(this IReadOnlyList<string> allowed, string? directory) =>
        directory is { Length: > 0 and <= 4096 } && !directory.Any(char.IsControl) &&
        (allowed.Contains("*", StringComparer.Ordinal) || allowed.Contains(directory, StringComparer.Ordinal));
}
