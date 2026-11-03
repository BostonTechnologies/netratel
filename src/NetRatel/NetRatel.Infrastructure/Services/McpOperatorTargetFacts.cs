using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Missing facts stay unknown. Persisted invalid facts never become an absent profile.</summary>
public sealed record McpOperatorTargetFacts(McpOperatorTargetClassification? Classification,
    IReadOnlySet<string>? Tags, bool Invalid, string Digest)
{
    public static McpOperatorTargetFacts Read(int tenantId, Guid agentId, McpOperatorTargetProfileRecord? profile)
    {
        var source = profile is null ? $"{tenantId}:{agentId:D}:unprofiled" :
            $"{tenantId}:{agentId:D}:{profile.Version}:{(short)profile.Classification}:{profile.TagsJson}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (profile is null) return new(null, null, false, digest);
        try
        {
            var tags = JsonSerializer.Deserialize(profile.TagsJson, McpOperatorJsonContext.Default.StringArray);
            if (Enum.IsDefined(profile.Classification) && tags is not null && !tags.Any(string.IsNullOrWhiteSpace))
                return new(profile.Classification, tags.ToHashSet(StringComparer.Ordinal), false, digest);
        }
        catch (JsonException)
        {
            return new(null, null, true, digest);
        }
        return new(null, null, true, digest);
    }
}
