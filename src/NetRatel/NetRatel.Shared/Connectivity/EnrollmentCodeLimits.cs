namespace NetRatel.Shared.Connectivity;

/// <summary>Application enrollment contract, shared by issuer validation and MCP discovery.</summary>
public static class EnrollmentCodeLimits
{
    public const int MinimumValidityMinutes = 5;
    public const int MaximumValidityMinutes = 72460;
    public const int MaximumUses = int.MaxValue;
}
