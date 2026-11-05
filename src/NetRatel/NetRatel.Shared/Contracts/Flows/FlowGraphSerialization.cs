using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>
/// Schema one retains its historical Web JSON bytes. Schema two has one canonical graph representation
/// across PostgreSQL JSONB, runtime admission, pure preview and recorded viewer verification.
/// </summary>
public static class FlowGraphSerialization
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize(FlowGraphDto graph) => graph.SchemaVersion == FlowGraphV2Limits.SchemaVersion
        ? FlowJsonCanonical.Serialize(graph) : JsonSerializer.Serialize(graph, Json);
    public static string Hash(FlowGraphDto graph) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(graph))));
}
