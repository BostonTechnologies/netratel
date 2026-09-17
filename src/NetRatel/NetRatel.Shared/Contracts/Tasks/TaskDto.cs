using System;
using NetRatel.Shared;

namespace NetRatel.Shared.Contracts.Tasks;

public sealed record TaskDto(
    int Id,
    string RequestId,
    string ClientIdentity,
    int? TenantId,
    ClientEnvironment Environment,
    string TaskType,
    string Status,
    string? StatusMessage,
    string? ReturnData,
    DateTimeOffset Created,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    string? ClientDisplayName = null,
    string? ClientHostName = null,
    string? ClientName = null,
    Guid? AgentId = null
);

public sealed record TaskHistoryItemDto(
    int Id,
    string RequestId,
    int? TenantId,
    string TaskType,
    string Status,
    string? StatusMessage,
    DateTimeOffset Created,
    DateTimeOffset? CompletedAt,
    string? ClientDisplayName,
    string? ClientHostName,
    string? ClientName,
    Guid? AgentId);

public sealed record TaskHistoryPageDto(
    IReadOnlyList<TaskHistoryItemDto> Items,
    int Total,
    int Page,
    int PageSize);
