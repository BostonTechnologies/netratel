using System;
using System.Collections.Generic;

namespace NetRatel.Web.Models.Requests;

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
    string? JobName
);

public record UpdateRequestDto(
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
