using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.API.Models.RequestModels;

public sealed class SubmitRequestRequest
{
    public string? SourceSystem { get; set; }
    public int? TenantId { get; set; }
    public Guid? AgentId { get; set; }
    /// <summary>Historical compatibility input; no longer an execution authority key.</summary>
    public string? TargetClientIdentity { get; set; }
    public string? JobDefinitionId { get; set; }
    public string? JobInputsJson { get; set; }
    public string? TaskType { get; set; }
    public int? ScriptId { get; set; }

    // Captures retired input aliases without advertising them in OpenAPI or responses.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? CompatibilityFields { get; set; }

    public string? ResolveJobDefinitionId()
        => JobDefinitionId ?? ReadLegacyString("rundeckJobDefinitionId");

    private string? ReadLegacyString(string name)
        => CompatibilityFields is not null
           && CompatibilityFields.TryGetValue(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
