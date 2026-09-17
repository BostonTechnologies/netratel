namespace NetRatel.Application.Jobs;

public enum JobStepKind
{
    RunCommand = 0,
    LibraryScript = 1,
    HttpAction = 2
}

public sealed record JobDefinitionInfo(
    ulong Id,
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    string ClientIdentity,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? OptionsJson,
    Guid? AgentId = null);

public sealed record JobParameterInfo(
    ulong Id,
    ulong JobId,
    string Name,
    string Type,
    bool Required,
    string? DefaultValue,
    string? Description,
    string? OptionsJson);

public sealed record JobStepInfo(
    ulong Id,
    ulong JobId,
    int Ordinal,
    JobStepKind Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool Enabled);

public sealed record JobDefinitionDetails(
    JobDefinitionInfo Job,
    IReadOnlyList<JobParameterInfo> Params,
    IReadOnlyList<JobStepInfo> Steps);

public sealed record CreateJobDefinitionCommand(
    string Name,
    string FolderPath,
    string? Description,
    int? TenantId,
    string ClientIdentity,
    string? OptionsJson = null,
    Guid? AgentId = null);

public sealed record UpdateJobDefinitionCommand(
    ulong JobId,
    string? Name,
    string? FolderPath,
    string? Description,
    int? TenantId,
    string? ClientIdentity,
    string? OptionsJson = null,
    Guid? AgentId = null);

public sealed record AddJobParameterCommand(
    ulong JobId,
    string Name,
    string Type,
    bool Required,
    string? DefaultValue,
    string? Description,
    string? OptionsJson);

public sealed record UpdateJobParameterCommand(
    ulong ParamId,
    string? Name,
    string? Type,
    bool? Required,
    string? DefaultValue,
    string? Description,
    string? OptionsJson);

public sealed record AddJobStepCommand(
    ulong JobId,
    int? Ordinal,
    JobStepKind Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled);

public sealed record UpdateJobStepCommand(
    ulong StepId,
    JobStepKind? Type,
    string? Runner,
    string? Command,
    ulong? ScriptId,
    string? PayloadJson,
    bool? Enabled);

public interface IJobDefinitionService
{
    Task<IReadOnlyList<JobDefinitionInfo>> ListAsync(CancellationToken ct = default);
    Task<JobDefinitionInfo?> GetAsync(ulong jobId, CancellationToken ct = default);
    Task<JobDefinitionDetails?> GetDetailsAsync(ulong jobId, CancellationToken ct = default);
    Task<JobDefinitionInfo> CreateAsync(CreateJobDefinitionCommand command, CancellationToken ct = default);
    Task<JobDefinitionInfo?> UpdateAsync(UpdateJobDefinitionCommand command, CancellationToken ct = default);
    Task<JobDefinitionInfo?> DeleteAsync(ulong jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobParameterInfo>> ListParamsAsync(ulong jobId, CancellationToken ct = default);
    Task<JobParameterInfo?> AddParamAsync(AddJobParameterCommand command, CancellationToken ct = default);
    Task<JobParameterInfo?> UpdateParamAsync(UpdateJobParameterCommand command, CancellationToken ct = default);
    Task<bool> DeleteParamAsync(ulong paramId, CancellationToken ct = default);
    Task<IReadOnlyList<JobStepInfo>> ListStepsAsync(ulong jobId, CancellationToken ct = default);
    Task<JobStepInfo?> AddStepAsync(AddJobStepCommand command, CancellationToken ct = default);
    Task<JobStepInfo?> UpdateStepAsync(UpdateJobStepCommand command, CancellationToken ct = default);
    Task<JobStepInfo?> ReorderStepAsync(ulong stepId, int newOrdinal, CancellationToken ct = default);
    Task<bool> DeleteStepAsync(ulong stepId, CancellationToken ct = default);
}
