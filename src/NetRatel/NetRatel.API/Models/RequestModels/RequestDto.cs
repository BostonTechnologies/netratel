using System;
using System.Collections.Generic;

namespace NetRatel.API.Models.RequestModels;

public record RequestDto(
    int Id,
    string SourceSystem,
    string? TargetClientIdentity,
    string? JobDefinitionId,
    string? ExecutionId,
    string Status,
    string? ResultMessage,
    string? ResultData,
    string? JobInputs,
    IReadOnlyList<string> Logs,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    ulong? JobRunId,
    ulong? JobId,
    string? JobName,
    int? TargetTenantId = null,
    Guid? TargetAgentId = null
);

public record UpdateRequestRequest(
    string? SourceSystem,
    string? TargetClientIdentity,
    string? JobDefinitionId,
    string? ExecutionId,
    string? Status,
    string? ResultMessage,
    string? ResultData,
    string? JobInputs,
    IReadOnlyList<string>? Logs
);
