namespace NetRatel.API.Services.Orchestration;

public static class NetRatelCatalogMetadataProjection
{
    public const string MixedScriptType = "Mixed";

    public static string? BuildClientShortId(string? clientIdentity, int length = 12)
    {
        if (string.IsNullOrWhiteSpace(clientIdentity))
        {
            return null;
        }

        var value = clientIdentity.Trim();
        return value.Length <= length ? value : value[..length];
    }

    public static string? BuildClientDisplayName(string? hostName, string? clientName, string? clientShortId)
        => FirstNonEmpty(hostName, clientName, clientShortId);

    public static string? ResolveScriptType(IEnumerable<string?> scriptTypes)
    {
        var distinct = scriptTypes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => MixedScriptType
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
