using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.Jobs;

public enum JobTargetSource { ExecutionGroup = 1, ExplicitNodes = 2, NodeFilter = 3, RuntimeInput = 4 }
public enum JobNodeFailurePolicy { StopRemaining = 0, ContinueRemaining = 1 }
public enum JobRuntimeTargetKind { ClientId = 1, ClientIds = 2, Hostname = 3, Hostnames = 4, Tag = 5, Tags = 6 }

/// <summary>The limit applies to complete node workflows, independently of native step deadlines.</summary>
public sealed record JobExecutionPolicy(int ThreadCount = 1,
    JobNodeFailurePolicy NodeFailurePolicy = JobNodeFailurePolicy.StopRemaining)
{
    public void Validate()
    {
        if (ThreadCount is < 1 or > 100 || !Enum.IsDefined(NodeFailurePolicy))
            throw new ArgumentException("Thread Count must be between 1 and 100 and the node failure policy must be supported.");
    }
}

public sealed record JobRuntimeTargetDefinition(JobRuntimeTargetKind Kind, string Name,
    IReadOnlyList<string>? DefaultValues = null, bool AllOf = false);

/// <summary>Typed literal data. Values must never be parsed as executable selector expressions.</summary>
public sealed record JobRuntimeTargetInput(JobRuntimeTargetKind Kind, IReadOnlyList<string> Values, bool AllOf = false);

/// <summary>An exclusive, versioned target source. Its presence in an update replaces the previous target.</summary>
public sealed record JobTargetDefinition
{
    public int Version { get; init; } = 1;
    public JobTargetSource Source { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ExecutionGroupId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<Guid>? AgentIds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Filter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JobRuntimeTargetDefinition? RuntimeInput { get; init; }

    public static JobTargetDefinition FromLegacy(Guid agentId)
        => new() { Source = JobTargetSource.ExplicitNodes, AgentIds = [agentId] };

    public void Validate()
    {
        if (Version != 1 || !Enum.IsDefined(Source))
            throw new ArgumentException("The job target version or source is unsupported.");
        var fields = (ExecutionGroupId.HasValue ? 1 : 0) + (AgentIds is not null ? 1 : 0) +
            (Filter is not null ? 1 : 0) + (RuntimeInput is not null ? 1 : 0);
        if (fields != 1 || (Source switch
            {
                JobTargetSource.ExecutionGroup => ExecutionGroupId is null || ExecutionGroupId == Guid.Empty,
                JobTargetSource.ExplicitNodes => AgentIds is null || AgentIds.Count is < 1 or > 10_000 || AgentIds.Contains(Guid.Empty),
                JobTargetSource.NodeFilter => string.IsNullOrWhiteSpace(Filter) || Filter.Length > 4096,
                JobTargetSource.RuntimeInput => RuntimeInput is null || !Enum.IsDefined(RuntimeInput.Kind) ||
                    string.IsNullOrWhiteSpace(RuntimeInput.Name) || RuntimeInput.Name.Length > 128 ||
                    RuntimeInput.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
                    RuntimeInput.DefaultValues?.Count > 10_000 ||
                    RuntimeInput.DefaultValues?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024) == true ||
                    RuntimeInput.AllOf && RuntimeInput.Kind is not (JobRuntimeTargetKind.Tag or JobRuntimeTargetKind.Tags),
                _ => true
            }))
            throw new ArgumentException("Exactly one complete job target source is required; inactive target fields are not allowed.");
    }
}

public sealed record JobTargetNode(Guid AgentId, string Hostname, string? DisplayName, IReadOnlyList<string> Tags)
{
    public string? Name { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Architecture { get; init; }
    public ClientEnvironment? Environment { get; init; }
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; } = true;
}

/// <summary>Only binding origin belongs in the public plan; values remain in protected execution inputs.</summary>
public sealed record JobTargetBindingProvenance(string Name, string Source);

/// <summary>The admitted identity set. Construction copies, deduplicates and deterministically orders targets.</summary>
public sealed record JobResolvedTargetPlan
{
    public int Version { get; }
    public int TenantId { get; }
    public ulong JobId { get; }
    public string DefinitionDigest { get; }
    public JobTargetDefinition Target { get; }
    public long? GroupRevision { get; }
    public IReadOnlyList<JobTargetBindingProvenance> Bindings { get; }
    public IReadOnlyList<JobTargetNode> Nodes { get; }
    public JobExecutionPolicy ExecutionPolicy { get; }

    [JsonConstructor]
    public JobResolvedTargetPlan(int version, int tenantId, ulong jobId, string definitionDigest,
        JobTargetDefinition target, long? groupRevision, IReadOnlyList<JobTargetBindingProvenance> bindings,
        IReadOnlyList<JobTargetNode> nodes, JobExecutionPolicy executionPolicy)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(nodes);
        if (string.IsNullOrWhiteSpace(definitionDigest) || bindings.Any(binding => binding is null) ||
            nodes.Any(node => node is null || node.Tags is null || node.Tags.Any(tag => tag is null)))
            throw new ArgumentException("The resolved job target plan contains missing material.");
        Version = version;
        TenantId = tenantId;
        JobId = jobId;
        DefinitionDigest = definitionDigest;
        Target = target with
        {
            AgentIds = target.AgentIds is null ? null : Array.AsReadOnly(target.AgentIds.Distinct().Order().ToArray()),
            RuntimeInput = target.RuntimeInput is null ? null : target.RuntimeInput with
            {
                DefaultValues = target.RuntimeInput.DefaultValues is null ? null : Array.AsReadOnly(target.RuntimeInput.DefaultValues.ToArray())
            }
        };
        GroupRevision = groupRevision;
        Bindings = Array.AsReadOnly(bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal)
            .ThenBy(binding => binding.Source, StringComparer.Ordinal).ToArray());
        var frozenNodes = nodes.Select(node => node with
            { Tags = Array.AsReadOnly(node.Tags.Order(StringComparer.OrdinalIgnoreCase).ToArray()) }).ToArray();
        foreach (var duplicate in frozenNodes.GroupBy(node => node.AgentId))
            if (duplicate.Skip(1).Any(node => !SameSnapshot(duplicate.First(), node)))
                throw new ArgumentException("Duplicate target identities have conflicting presentation snapshots.");
        Nodes = Array.AsReadOnly(frozenNodes.GroupBy(node => node.AgentId).Select(group => group.First())
            .OrderBy(node => node.Hostname, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.AgentId)
            .ToArray());
        ExecutionPolicy = executionPolicy;
        Validate();
    }

    private static bool SameSnapshot(JobTargetNode left, JobTargetNode right)
        => left.Hostname == right.Hostname && left.DisplayName == right.DisplayName && left.Name == right.Name &&
           left.OperatingSystem == right.OperatingSystem && left.Architecture == right.Architecture &&
           left.Environment == right.Environment && left.IsOnline == right.IsOnline && left.IsEnabled == right.IsEnabled &&
           left.Tags.SequenceEqual(right.Tags, StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        Target.Validate();
        ExecutionPolicy.Validate();
        if (Version != 1 || TenantId <= 0 || JobId == 0 || DefinitionDigest.Length != 64 ||
            DefinitionDigest.Any(character => !Uri.IsHexDigit(character)) || Nodes.Count is < 1 or > 10_000 ||
            Nodes.Any(node => node.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(node.Hostname)) || GroupRevision is < 1 ||
            Bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Name) || string.IsNullOrWhiteSpace(binding.Source)))
            throw new ArgumentException("The resolved job target plan is incomplete or invalid.");
    }

    /// <summary>Canonical schema 1 hash; independent of caller JSON options, presentation order, and dictionary ordering.</summary>
    public string ComputeDigest()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteNumber("tenantId", TenantId);
            writer.WriteNumber("jobId", JobId);
            writer.WriteString("definitionDigest", DefinitionDigest.ToUpperInvariant());
            writer.WritePropertyName("target");
            WriteTarget(writer, Target);
            if (GroupRevision is { } revision) writer.WriteNumber("groupRevision", revision);
            writer.WritePropertyName("bindings"); writer.WriteStartArray();
            foreach (var binding in Bindings)
            {
                writer.WriteStartObject(); writer.WriteString("name", binding.Name); writer.WriteString("source", binding.Source); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("nodes"); writer.WriteStartArray();
            foreach (var node in Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("agentId", node.AgentId.ToString("D"));
                writer.WriteString("hostname", node.Hostname); writer.WriteString("displayName", node.DisplayName);
                writer.WriteString("name", node.Name); writer.WriteString("operatingSystem", node.OperatingSystem);
                writer.WriteString("architecture", node.Architecture);
                if (node.Environment is { } environment) writer.WriteNumber("environment", (int)environment);
                writer.WriteBoolean("isOnline", node.IsOnline); writer.WriteBoolean("isEnabled", node.IsEnabled);
                writer.WritePropertyName("tags"); writer.WriteStartArray();
                foreach (var tag in node.Tags) writer.WriteStringValue(tag);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("threadCount", ExecutionPolicy.ThreadCount);
            writer.WriteNumber("nodeFailurePolicy", (int)ExecutionPolicy.NodeFailurePolicy);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteTarget(Utf8JsonWriter writer, JobTargetDefinition target)
    {
        writer.WriteStartObject(); writer.WriteNumber("version", target.Version); writer.WriteNumber("source", (int)target.Source);
        if (target.ExecutionGroupId is { } groupId) writer.WriteString("executionGroupId", groupId.ToString("D"));
        if (target.AgentIds is { } agentIds)
        {
            writer.WritePropertyName("agentIds"); writer.WriteStartArray();
            foreach (var agentId in agentIds) writer.WriteStringValue(agentId.ToString("D"));
            writer.WriteEndArray();
        }
        if (target.Filter is { } filter) writer.WriteString("filter", filter);
        if (target.RuntimeInput is { } runtime)
        {
            writer.WritePropertyName("runtimeInput"); writer.WriteStartObject();
            writer.WriteNumber("kind", (int)runtime.Kind); writer.WriteString("name", runtime.Name); writer.WriteBoolean("allOf", runtime.AllOf);
            if (runtime.DefaultValues is { } values)
            {
                writer.WritePropertyName("defaultValues"); writer.WriteStartArray();
                foreach (var value in values) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}

public enum JobRunNodeState
{
    NeverStarted = 0, Claimed = 1, Running = 2, Succeeded = 3, Failed = 4,
    Skipped = 5, Cancelled = 6, TimedOut = 7, Unknown = 8
}

public static class JobRunNodeReasons
{
    public const string StoppedAfterNodeFailure = nameof(StoppedAfterNodeFailure);
    public const string StoppedAfterUnknownExecution = nameof(StoppedAfterUnknownExecution);
    public const string ParentCancelled = nameof(ParentCancelled);
}

public sealed record JobRunNodeCountsDto(int Queued, int Running, int Succeeded, int Failed,
    int Skipped, int Cancelled, int TimedOut, int Unknown);

public sealed record JobRunNodeDto(ulong ParentRunId, Guid AgentId, int Ordinal, ulong? NativeRunId,
    JobRunNodeState State, string Hostname, string? DisplayName, string? Reason,
    long? StartedAtMicros, long? CompletedAtMicros)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public ClientEnvironment? Environment { get; init; }
    public int CurrentStepOrdinal { get; init; }
}

public sealed record PagedJobRunNodesDto(IReadOnlyList<JobRunNodeDto> Items, int TotalCount);

[JsonSerializable(typeof(JobTargetDefinition))]
[JsonSerializable(typeof(JobExecutionPolicy))]
[JsonSerializable(typeof(JobRuntimeTargetInput))]
[JsonSerializable(typeof(JobResolvedTargetPlan))]
[JsonSerializable(typeof(JobTargetNode))]
[JsonSerializable(typeof(JobRunNodeDto))]
[JsonSerializable(typeof(PagedJobRunNodesDto))]
public partial class JobTargetJsonContext : JsonSerializerContext;
