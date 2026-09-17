namespace NetRatel.API.Application.Requests.Contracts;

public record QueueRequestDto(Guid RequestId, string Tenant, string TypeKey, Dictionary<string, object?> Inputs, string CallbackUrl);
public record OrchestratorEventDto(Guid RequestId, string Status, string? Message, int? ProgressPercent, string? PayloadJson);
