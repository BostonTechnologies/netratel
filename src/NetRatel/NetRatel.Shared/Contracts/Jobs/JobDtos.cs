using System.Collections.Generic;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Shared.Contracts.Jobs;

public sealed record JobScheduleDto(
    bool Enabled,
    string TimeZoneId,
    string TimeOfDay,
    IReadOnlyList<string> DaysOfWeek
);

public record JobDto(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    string ClientIdentity,
    long CreatedAtMicros,
    long UpdatedAtMicros,
    string? TenantDisplayName = null,
    string? ClientDisplayName = null,
    ClientEnvironment? Environment = null,
    int ExpectedRuntimeSeconds = 1800,
    int GraceSeconds = 0,
    int HardTimeoutSeconds = 1800,
    JobScheduleDto? Schedule = null,
    Guid? AgentId = null,
    string? StepSummary = null
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobTargetDefinition? Target { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobExecutionPolicy? ExecutionPolicy { get; init; }
}


public record JobParamDto(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong JobId,
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    string? OptionsJson
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasProtectedDefault { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasProviderDefault { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SecretReference? DefaultSecretReference { get; init; }
}

public enum JobStepTypeDto { RunCommand = 0, LibraryScript = 1, HttpAction = 2 }

public record JobStepDto(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong JobId,
    int Ordinal,
    JobStepTypeDto Type,
    string? Runner,
    string? Command,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? ScriptId,
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
    Guid? AgentId = null,
    ClientEnvironment? Environment = null
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobTargetDefinition? Target { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobExecutionPolicy? ExecutionPolicy { get; init; }
}


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
    Guid? AgentId = null,
    ClientEnvironment? Environment = null
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobTargetDefinition? Target { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public JobExecutionPolicy? ExecutionPolicy { get; init; }
}


public record AddJobParamRequest(
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    string? OptionsJson
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SecretReference? DefaultSecretReference { get; init; }
}

public record UpdateJobParamRequest(
    string? Name,
    string? Type,
    bool? Required,
    string? Default,
    string? Description,
    string? OptionsJson
)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ClearProtectedDefault { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SecretReference? DefaultSecretReference { get; init; }
}

public record AddJobStepRequest(
    int? Ordinal,
    JobStepTypeDto Type,
    string? Runner,
    string? Command,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled
);

public record UpdateJobStepRequest(
    JobStepTypeDto? Type,
    string? Runner,
    string? Command,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled
);

public record ReorderJobStepRequest(int NewOrdinal);

public sealed record JobWithDetailsDto(
    JobDto Job,
    IReadOnlyList<JobStepDto> Steps,
    IReadOnlyList<JobParamDto> Params
);
