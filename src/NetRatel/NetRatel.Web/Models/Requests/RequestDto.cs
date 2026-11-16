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
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? JobRunId,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? JobId,
    string? JobName
)
{
    public int? TargetTenantId { get; init; }
    public IReadOnlyList<string> ExternalReferences { get; init; } = [];
}

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
