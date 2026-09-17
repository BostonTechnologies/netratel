using System.Collections.Generic;
using NetRatel.Shared;

namespace NetRatel.Shared.Contracts.Jobs;

public sealed record JobScheduleDto(
    bool Enabled,
    string TimeZoneId,
    string TimeOfDay,
    IReadOnlyList<string> DaysOfWeek
);

public record JobDto(
    ulong Id,
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    string ClientIdentity,
    long CreatedAtMicros,
    long UpdatedAtMicros,
    string? TenantDisplayName = null,
    string? ClientDisplayName = null,
    ClientEnvironment Environment = ClientEnvironment.None,
    int ExpectedRuntimeSeconds = 1800,
    int GraceSeconds = 0,
    int HardTimeoutSeconds = 1800,
    JobScheduleDto? Schedule = null,
    Guid? AgentId = null
);

public record JobParamDto(
    ulong Id,
    ulong JobId,
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    string? OptionsJson
);

public enum JobStepTypeDto { RunCommand = 0, LibraryScript = 1, HttpAction = 2 }

public record JobStepDto(
    ulong Id,
    ulong JobId,
    int Ordinal,
    JobStepTypeDto Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool Enabled
);
public record HttpActionConfigDto(
    string Method,
    string Url,
    bool IncludeAuthHeader,
    string? AuthHeader,
    bool IncludeJsonPayload,
    string? JsonPayload,
    bool IncludeBody,
    string? Body);

public record CreateJobRequest(
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    string ClientIdentity,
    int? ExpectedRuntimeSeconds = null,
    int? GraceSeconds = null,
    int? HardTimeoutSeconds = null,
    JobScheduleDto? Schedule = null,
    Guid? AgentId = null
);

public record UpdateJobRequest(
    string? Name,
    string? FolderPath,
    string? Description,
    int? TenantId,
    string? ClientIdentity,
    int? ExpectedRuntimeSeconds = null,
    int? GraceSeconds = null,
    int? HardTimeoutSeconds = null,
    JobScheduleDto? Schedule = null,
    Guid? AgentId = null
);

public record AddJobParamRequest(
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    string? OptionsJson
);

public record UpdateJobParamRequest(
    string? Name,
    string? Type,
    bool? Required,
    string? Default,
    string? Description,
    string? OptionsJson
);

public record AddJobStepRequest(
    int? Ordinal,
    JobStepTypeDto Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled
);

public record UpdateJobStepRequest(
    JobStepTypeDto? Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled
);

public record ReorderJobStepRequest(int NewOrdinal);

public sealed record JobWithDetailsDto(
    JobDto Job,
    IReadOnlyList<JobStepDto> Steps,
    IReadOnlyList<JobParamDto> Params
);
