using System.Text.Json.Serialization;

namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelIngestRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string RequestTaskId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? AutomationBindingId { get; set; }
    public string? NetRatelRequestDefinitionId { get; set; }
    public string? NetRatelJobDefinitionId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string? CallbackUrl { get; set; }
    public int? ExpectedRuntimeSeconds { get; set; }
    public int? GraceSeconds { get; set; }
    public int? HardTimeoutSeconds { get; set; }
}

public sealed class NetRatelIngestResponse
{
    public string? RequestId { get; set; }
    public string? RunId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public string Status { get; set; } = "Accepted";
    public string? Message { get; set; }
}

public sealed class NetRatelIngestProblem
{
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? NetRatelJobDefinitionId { get; set; }
    public string? TargetClientIdentity { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class NetRatelCatalogInputDefinitionDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public string? HelpText { get; set; }
    public string? OptionsJson { get; set; }
    public int Order { get; set; }
}

public sealed class NetRatelCatalogJobDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? Description { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public string? ClientHostName { get; set; }
    public string? ClientName { get; set; }
    public string? ClientShortId { get; set; }
    public string? ScriptType { get; set; }
    public int ExpectedRuntimeSeconds { get; set; } = 1800;
    public int GraceSeconds { get; set; }
    public int HardTimeoutSeconds { get; set; } = 1800;
    public IReadOnlyList<NetRatelCatalogInputDefinitionDto> Inputs { get; set; } = [];
}

public sealed class NetRatelCatalogRequestDefinitionDto
{
    public string RequestDefinitionId { get; set; } = string.Empty;
    public string RequestDefinitionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string FolderPath { get; set; } = "/";
    public string? NetRatelJobDefinitionId { get; set; }
    public string? NetRatelJobDefinitionName { get; set; }
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public string? ClientHostName { get; set; }
    public string? ClientName { get; set; }
    public string? ClientShortId { get; set; }
    public string? ScriptType { get; set; }
    public int ExpectedRuntimeSeconds { get; set; } = 1800;
    public int GraceSeconds { get; set; }
    public int HardTimeoutSeconds { get; set; } = 1800;
    public IReadOnlyList<NetRatelCatalogInputDefinitionDto> Inputs { get; set; } = [];
}

public sealed class NetRatelCatalogTenantDto
{
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class CreateNetRatelCatalogRequestDefinitionRequest
{
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public string? Description { get; set; }
    public int? TenantId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
}

public sealed class SyncNetRatelCatalogRequestDefinitionInputsRequest
{
    public IReadOnlyList<NetRatelCatalogInputDefinitionDto> Inputs { get; set; } = [];
}
