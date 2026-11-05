namespace NetRatel.Shared.Contracts.Flows;

/// <summary>Opaque target-local names accepted by native execution, never resolved secret values.</summary>
public static class FlowExecutionInputNames
{
    public const int MaximumEnvironmentReferenceNameLength = 128;

    public static bool IsEnvironmentReferenceName(string? reference) =>
        reference is { Length: > 0 and <= MaximumEnvironmentReferenceNameLength } &&
        (char.IsAsciiLetter(reference[0]) || reference[0] == '_') &&
        reference.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
