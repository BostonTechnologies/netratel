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
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? JobRunId,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? JobId,
    string? JobName,
    int? TargetTenantId = null,
    Guid? TargetAgentId = null
)
{
    public IReadOnlyList<string> ExternalReferences { get; init; } = [];
}

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
