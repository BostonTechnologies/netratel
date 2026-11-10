using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Jobs;

/// <summary>Bounded directory page shared by CLI and both MCP transports.</summary>
public sealed record JobTargetDirectoryRequest(string? Search = null, int Page = 1, int PageSize = 50);

public sealed record JobTargetIdentitiesRequest(IReadOnlyList<Guid> AgentIds);

public sealed record ExecutionGroupDeleteRequest(long ExpectedRevision);

[JsonSerializable(typeof(JobTargetDirectoryRequest))]
[JsonSerializable(typeof(JobTargetIdentitiesRequest))]
[JsonSerializable(typeof(ExecutionGroupDeleteRequest))]
public partial class JobToolingJsonContext : JsonSerializerContext;
