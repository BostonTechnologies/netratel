using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Flows;

/// <summary>
/// Server-owned subscription selection captured transactionally with a source transition. Reading this DTO
/// does not grant execution authority. Admission verifies the exact immutable version, source row and hashes.
/// </summary>
public sealed record FlowExecutionSubscriptionDeliveryDto(Guid DeliveryId, int TenantId, Guid FlowVersionId,
    Guid TriggerNodeId, Guid SourceEventId, DateTimeOffset ActiveSinceUtc, Guid AdmissionKey,
    string SourcePayloadHash, string ConfigurationHash);

public static class FlowExecutionSubscriptionLimits
{
    public const string OutboxType = "Flow.ExecutionSubscription.V1";
    public const int MaximumIntentBytes = 2048;
    public const int MaximumBatch = 32;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlowExecutionSubscriptionDeliveryDto))]
public partial class FlowExecutionSubscriptionJsonContext : JsonSerializerContext { }
