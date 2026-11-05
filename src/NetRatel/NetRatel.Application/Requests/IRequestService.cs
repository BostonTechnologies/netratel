namespace NetRatel.Application.Requests;

public sealed record RequestInfo(
    int Id,
    string SourceSystem,
    string TargetClientIdentity,
    string? JobDefinitionId,
    string? ExecutionId,
    string Status,
    string? ResultMessage,
    string? ResultData,
    string? JobInputs,
    IReadOnlyList<string> Logs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int? TargetTenantId = null,
    Guid? TargetAgentId = null);

public sealed record CreateRequestCommand(
    string SourceSystem,
    string TargetClientIdentity,
    string? JobDefinitionId,
    string? JobInputsJson,
    int? TargetTenantId = null,
    Guid? TargetAgentId = null);

public sealed record UpdateRequestCommand(
    int RequestId,
    string? SourceSystem,
    string? TargetClientIdentity,
    string? JobDefinitionId,
    string? ExecutionId,
    string? Status,
    string? ResultMessage,
    string? ResultData,
    string? JobInputs,
    IReadOnlyList<string>? Logs);

/// <summary>Protected internal execution material; never serialize this from a public request endpoint.</summary>
public sealed record RequestExecutionMaterial(int RequestId, string SourceSystem, ulong JobDefinitionId,
    int TenantId, Guid AgentId, string? InputsJson);

public interface IRequestService
{
    Task<RequestExecutionMaterial?> GetExecutionMaterialAsync(int requestId, CancellationToken ct = default)
        => Task.FromResult<RequestExecutionMaterial?>(null);
    Task<IReadOnlyList<RequestInfo>> ListAsync(CancellationToken ct = default);
    Task<RequestInfo?> GetAsync(int requestId, CancellationToken ct = default);
    Task<RequestInfo> CreateAsync(CreateRequestCommand command, CancellationToken ct = default);
    Task<RequestInfo?> UpdateAsync(UpdateRequestCommand command, CancellationToken ct = default);
}
