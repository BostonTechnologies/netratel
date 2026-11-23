using System.Text;

namespace NetRatel.Shared.Contracts.Jobs;

public static class ProtectedJobParameterRules
{
    public const string Capability = "protected-job-parameters-v1";
    public static TimeSpan DispatchLifetime { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan MaximumClockSkew { get; } = TimeSpan.FromSeconds(30);

    public static bool AreValid(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.Count is < 1 or > 32) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = 64 * 1024;
        foreach (var (name, value) in parameters)
        {
            if (!IsName(name) || !names.Add(name) || value is null || value.Length > remaining) return false;
            remaining -= Encoding.UTF8.GetByteCount(value);
            if (remaining < 0) return false;
        }
        return true;
    }

    public static bool IsName(string name) => name is { Length: > 0 and <= 128 } &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
