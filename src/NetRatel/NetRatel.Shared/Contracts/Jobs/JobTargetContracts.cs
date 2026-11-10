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
    /// <summary>Presentation ordering must not turn an operator label into an asserted hostname.</summary>
    public static string SortKey(string? hostname, string? name, string? displayName)
        => !string.IsNullOrWhiteSpace(hostname) ? hostname : !string.IsNullOrWhiteSpace(name) ? name : displayName ?? string.Empty;

    public string? Name { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Architecture { get; init; }
    public ClientEnvironment? Environment { get; init; }
    public bool IsOnline { get; init; }
    public bool IsEnabled { get; init; } = true;
}

/// <summary>Only binding origin belongs in the public plan; values remain in protected execution inputs.</summary>
public sealed record JobTargetBindingProvenance(string Name, string Source);

/// <summary>Binds one physical HA worker to the complete, previously approved worker pool.</summary>
public sealed record JobHaGroupSelection(string ApprovedPlanDigest, IReadOnlyList<JobTargetNode> Candidates);

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<Guid>? InvocationAgentIds { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JobHaGroupSelection? HaGroupSelection { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GroupName { get; }
    [JsonIgnore] public bool NeedsHaWorkerSelection => Version == 2 && Target.Source == JobTargetSource.ExecutionGroup && HaGroupSelection is null;
    [JsonIgnore] public int ExecutionTargetCount => NeedsHaWorkerSelection ? 1 : Nodes.Count;
    [JsonIgnore] public string AdmissionPlanDigest => HaGroupSelection?.ApprovedPlanDigest ?? ComputeDigest();

    public JobResolvedTargetPlan(int version, int tenantId, ulong jobId, string definitionDigest,
        JobTargetDefinition target, long? groupRevision, IReadOnlyList<JobTargetBindingProvenance> bindings,
        IReadOnlyList<JobTargetNode> nodes, JobExecutionPolicy executionPolicy)
        : this(version, tenantId, jobId, definitionDigest, target, groupRevision, bindings, nodes, executionPolicy, null, null, null) { }

    [JsonConstructor]
    public JobResolvedTargetPlan(int version, int tenantId, ulong jobId, string definitionDigest,
        JobTargetDefinition target, long? groupRevision, IReadOnlyList<JobTargetBindingProvenance> bindings,
        IReadOnlyList<JobTargetNode> nodes, JobExecutionPolicy executionPolicy,
        IReadOnlyList<Guid>? invocationAgentIds, JobHaGroupSelection? haGroupSelection, string? groupName)
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
        Nodes = FreezeNodes(nodes);
        ExecutionPolicy = executionPolicy;
        InvocationAgentIds = invocationAgentIds is null ? null : Array.AsReadOnly(invocationAgentIds.Distinct().Order().ToArray());
        HaGroupSelection = haGroupSelection is null ? null : new(haGroupSelection.ApprovedPlanDigest, FreezeNodes(haGroupSelection.Candidates));
        GroupName = groupName;
        Validate();
    }

    private static IReadOnlyList<JobTargetNode> FreezeNodes(IReadOnlyList<JobTargetNode> nodes)
    {
        if (nodes is null || nodes.Any(node => node is null || node.Tags is null || node.Tags.Any(tag => tag is null)))
            throw new ArgumentException("The resolved job target plan contains missing nodes.");
        var frozenNodes = nodes.Select(node => node with
            { Tags = Array.AsReadOnly(node.Tags.Order(StringComparer.OrdinalIgnoreCase).ThenBy(tag => tag, StringComparer.Ordinal).ToArray()) }).ToArray();
        foreach (var duplicate in frozenNodes.GroupBy(node => node.AgentId))
            if (duplicate.Skip(1).Any(node => !SameSnapshot(duplicate.First(), node)))
                throw new ArgumentException("Duplicate target identities have conflicting presentation snapshots.");
        return Array.AsReadOnly(frozenNodes.GroupBy(node => node.AgentId).Select(group => group.First())
            .OrderBy(node => JobTargetNode.SortKey(node.Hostname, node.Name, node.DisplayName), StringComparer.OrdinalIgnoreCase).ThenBy(node => node.AgentId)
            .ToArray());
    }

    private static bool SameSnapshot(JobTargetNode left, JobTargetNode right)
        => left.Hostname == right.Hostname && left.DisplayName == right.DisplayName && left.Name == right.Name &&
           left.OperatingSystem == right.OperatingSystem && left.Architecture == right.Architecture &&
           left.Environment == right.Environment && left.IsOnline == right.IsOnline && left.IsEnabled == right.IsEnabled &&
           left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal);

    public void Validate()
    {
        Target.Validate();
        ExecutionPolicy.Validate();
        if (Version is not (1 or 2) || TenantId <= 0 || JobId == 0 || DefinitionDigest.Length != 64 ||
            DefinitionDigest.Any(character => !Uri.IsHexDigit(character)) || Nodes.Count is < 1 or > 10_000 ||
            Nodes.Any(node => node.AgentId == Guid.Empty || node.Hostname is null) || GroupRevision is < 1 ||
            (Target.Source == JobTargetSource.ExecutionGroup) != GroupRevision.HasValue ||
            Version == 1 && (InvocationAgentIds is not null || HaGroupSelection is not null || GroupName is not null) ||
            Target.Source == JobTargetSource.ExplicitNodes && !Target.AgentIds!.ToHashSet().IsSupersetOf(Nodes.Select(node => node.AgentId)) ||
            Target.Source == JobTargetSource.ExplicitNodes && InvocationAgentIds is null && !Target.AgentIds!.ToHashSet().SetEquals(Nodes.Select(node => node.AgentId)) ||
            GroupName?.Length > 200 || GroupName is not null && Target.Source != JobTargetSource.ExecutionGroup ||
            Bindings.Any(binding => string.IsNullOrWhiteSpace(binding.Name) || string.IsNullOrWhiteSpace(binding.Source)))
            throw new ArgumentException("The resolved job target plan is incomplete or invalid.");
        if (InvocationAgentIds is { } selected && (selected.Count is < 1 or > 10_000 || selected.Contains(Guid.Empty) ||
            !selected.ToHashSet().SetEquals((HaGroupSelection?.Candidates ?? Nodes).Select(node => node.AgentId))))
            throw new ArgumentException("The invocation selection differs from its complete target set.");
        if (HaGroupSelection is { } selection)
        {
            if (Version != 2 || Target.Source != JobTargetSource.ExecutionGroup || Nodes.Count != 1 ||
                !selection.Candidates.Any(candidate => candidate.AgentId == Nodes[0].AgentId && SameSnapshot(candidate, Nodes[0])) ||
                ReconstructHaPool().ComputeDigest() != selection.ApprovedPlanDigest)
                throw new ArgumentException("The HA worker does not match its approved availability pool.");
        }
    }

    private JobResolvedTargetPlan ReconstructHaPool() => new(Version, TenantId, JobId, DefinitionDigest, Target,
        GroupRevision, Bindings, HaGroupSelection!.Candidates, ExecutionPolicy, InvocationAgentIds, null, GroupName);

    public JobResolvedTargetPlan SelectHaWorker(Guid agentId)
    {
        if (!NeedsHaWorkerSelection) throw new InvalidOperationException("Only an unselected HA pool can select a worker.");
        var worker = Nodes.SingleOrDefault(node => node.AgentId == agentId)
            ?? throw new ArgumentException("The HA worker is outside the approved pool.");
        return new(Version, TenantId, JobId, DefinitionDigest, Target, GroupRevision, Bindings, [worker], ExecutionPolicy,
            InvocationAgentIds, new(ComputeDigest(), Nodes), GroupName);
    }

    public bool MatchesAdmissionPlan(JobResolvedTargetPlan approved)
        => ComputeDigest() == approved.ComputeDigest() || HaGroupSelection is not null &&
            approved.HaGroupSelection is null && HaGroupSelection.ApprovedPlanDigest == approved.ComputeDigest();

    /// <summary>Schema 1 bytes are retained. Schema 2 binds invocation choices and a selected HA worker.</summary>
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
            writer.WritePropertyName("nodes"); WriteNodes(writer, Nodes);
            writer.WriteNumber("threadCount", ExecutionPolicy.ThreadCount);
            writer.WriteNumber("nodeFailurePolicy", (int)ExecutionPolicy.NodeFailurePolicy);
            if (Version == 2)
            {
                if (GroupName is not null) writer.WriteString("groupName", GroupName);
                if (InvocationAgentIds is { } selected)
                {
                    writer.WritePropertyName("invocationAgentIds"); writer.WriteStartArray();
                    foreach (var id in selected) writer.WriteStringValue(id.ToString("D"));
                    writer.WriteEndArray();
                }
                if (HaGroupSelection is { } selection)
                {
                    writer.WritePropertyName("haGroupSelection"); writer.WriteStartObject();
                    writer.WriteString("approvedPlanDigest", selection.ApprovedPlanDigest);
                    writer.WritePropertyName("candidates"); WriteNodes(writer, selection.Candidates);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private void WriteNodes(Utf8JsonWriter writer, IReadOnlyList<JobTargetNode> nodes)
    {
        writer.WriteStartArray();
        foreach (var node in nodes)
        {
            writer.WriteStartObject(); writer.WriteString("agentId", node.AgentId.ToString("D"));
            writer.WriteString("hostname", node.Hostname); writer.WriteString("displayName", node.DisplayName);
            writer.WriteString("name", node.Name); writer.WriteString("operatingSystem", node.OperatingSystem);
            writer.WriteString("architecture", node.Architecture);
            if (node.Environment is { } environment) writer.WriteNumber("environment", (int)environment);
            // HA availability is checked against committed leases at admission. A transient hint
            // changing between preview and start must not prevent choosing a healthy survivor.
            if (Version == 1 || Target.Source != JobTargetSource.ExecutionGroup) writer.WriteBoolean("isOnline", node.IsOnline);
            writer.WriteBoolean("isEnabled", node.IsEnabled);
            writer.WritePropertyName("tags"); writer.WriteStartArray();
            foreach (var tag in node.Tags) writer.WriteStringValue(tag);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeTerminalResultHash { get; init; }
}

public sealed record PagedJobRunNodesDto(IReadOnlyList<JobRunNodeDto> Items, int TotalCount);

[JsonSerializable(typeof(JobLaunchPreviewDto))]
[JsonSerializable(typeof(JobTargetDefinition))]
[JsonSerializable(typeof(JobLaunchPreviewDto))]
[JsonSerializable(typeof(JobExecutionPolicy))]
[JsonSerializable(typeof(JobRuntimeTargetInput))]
[JsonSerializable(typeof(JobResolvedTargetPlan))]
[JsonSerializable(typeof(JobTargetNode))]
[JsonSerializable(typeof(JobRunNodeDto))]
[JsonSerializable(typeof(PagedJobRunNodesDto))]
public partial class JobTargetJsonContext : JsonSerializerContext;
