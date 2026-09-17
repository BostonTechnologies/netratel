using System;
using NetRatel.Shared;

namespace NetRatel.API.Models.ClientTaskModels;

public sealed record TaskResponse(
    int Id,
    string RequestId,
    string ClientIdentity,
    int? TenantId,
    ClientEnvironment Environment,
    string TaskType,
    string Status,
    string? StatusMessage,
    string? ReturnData,
    int? ExitCode,
    DateTime? Created,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ClientDisplayName = null,
    string? ClientHostName = null,
    string? ClientName = null
);
