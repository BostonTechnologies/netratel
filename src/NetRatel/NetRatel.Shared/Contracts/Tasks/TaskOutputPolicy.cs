using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Tasks;

/// <summary>
/// Server-owned dispatch classification. It contains names and revision evidence,
/// never resolved secret values. Binding is unique to this execution.
/// </summary>
public sealed record TaskOutputPolicy(
    string Binding,
    string[] SecretParameters,
    string[] SecretReferenceParameters,
    string[] EnvironmentReferences,
    long? ScriptId = null,
    long? ScriptRevision = null,
    string? ScriptContentHash = null,
    bool WithholdOutput = false)
{
    [JsonIgnore]
    public bool HasSecrets => WithholdOutput || SecretParameters.Length > 0 ||
        SecretReferenceParameters.Length > 0 || EnvironmentReferences.Length > 0;

    public static TaskOutputPolicy Create(IEnumerable<(string Name, string Type)> parameters,
        long? scriptId = null, long? scriptRevision = null, string? scriptContentHash = null,
        IEnumerable<string>? environmentReferences = null, bool withholdOutput = false)
    {
        var snapshot = parameters.ToArray();
        return new(Guid.NewGuid().ToString("N"),
            snapshot.Where(x => x.Type.Equals("secret", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            snapshot.Where(x => x.Type.Equals("secret_reference", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            (environmentReferences ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            scriptId, scriptRevision, scriptContentHash, withholdOutput);
    }
}
