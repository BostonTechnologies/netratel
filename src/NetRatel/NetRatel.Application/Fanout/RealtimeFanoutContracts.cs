namespace NetRatel.Application.Fanout;

public enum RealtimeFanoutCategory
{
    Presence = 0,
    Telemetry = 1,
    Command = 2,
    Job = 3,
    Terminal = 4,
    RemoteSupport = 5,
    FileBrowser = 6
}

public enum RealtimeFanoutTargetScope
{
    Tenant = 0,
    Client = 1,
    Terminal = 2,
    RemoteSupport = 3,
    FileBrowser = 4,
    Job = 5,
    Command = 6
}

public enum RealtimeFanoutEventType
{
    Updated = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Expired = 4,
    Snapshot = 5
}

public enum RealtimeFanoutStatus
{
    Unknown = 0,
    Pending = 1,
    Active = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Expired = 6
}

/// <summary>
/// Identifies exactly one authorized SignalR group. Identifier text is
/// bounded and is hashed before it becomes part of a server-side group name.
/// </summary>
public sealed record RealtimeFanoutTarget(
    int TenantId,
    RealtimeFanoutTargetScope Scope,
    string? ClientId = null,
    string? SessionId = null,
    string? RequestId = null,
    ulong? JobId = null,
    string? CommandId = null)
{
    public bool IsValid =>
        TenantId > 0 &&
        (Scope switch
        {
            RealtimeFanoutTargetScope.Tenant => HasNoEntityIdentifiers(),
            RealtimeFanoutTargetScope.Client =>
                IsSafeIdentifier(ClientId) && HasNoIdentifiersExceptClient(),
            RealtimeFanoutTargetScope.Terminal or RealtimeFanoutTargetScope.RemoteSupport =>
                IsSafeIdentifier(ClientId) && IsSafeIdentifier(SessionId) &&
                RequestId is null && JobId is null && CommandId is null,
            RealtimeFanoutTargetScope.FileBrowser =>
                IsSafeIdentifier(ClientId) && IsSafeIdentifier(RequestId) &&
                SessionId is null && JobId is null && CommandId is null,
            RealtimeFanoutTargetScope.Job =>
                JobId > 0 && ClientId is null && SessionId is null &&
                RequestId is null && CommandId is null,
            RealtimeFanoutTargetScope.Command =>
                IsSafeIdentifier(CommandId) && ClientId is null && SessionId is null &&
                RequestId is null && JobId is null,
            _ => false
        });

    private bool HasNoEntityIdentifiers() =>
        ClientId is null && SessionId is null && RequestId is null && JobId is null && CommandId is null;

    private bool HasNoIdentifiersExceptClient() =>
        SessionId is null && RequestId is null && JobId is null && CommandId is null;

    internal static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '@'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Fixed scalar diagnostics only. It cannot carry logs, paths, errors,
/// terminal data, signalling bodies, media, or file content.
/// </summary>
public sealed record RealtimeFanoutDiagnosticSummary(
    ulong ObservedCount = 0,
    ulong DroppedCount = 0,
    ulong InvalidCount = 0,
    ulong RetainedCount = 0,
    ulong RepresentedBytes = 0,
    int QueueDepth = 0,
    ulong ActiveCount = 0,
    ulong CompletedCount = 0)
{
    public bool IsValid => QueueDepth >= 0;
}

/// <summary>
/// Versioned, latest-state metadata sent by the local SignalR fanout bridge.
/// Every envelope targets one group. The authority field preserves the
/// existing JSON contract and reports the source provenance of an update.
/// </summary>
public sealed record RealtimeFanoutEnvelope(
    int SchemaVersion,
    RealtimeFanoutCategory Category,
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? Diagnostics = null,
    bool ResyncRequired = false,
    bool IsAuthoritative = false)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsValid =>
        SchemaVersion == CurrentSchemaVersion &&
        Target.IsValid &&
        Timestamp != default &&
        (CorrelationId is null || RealtimeFanoutTarget.IsSafeIdentifier(CorrelationId)) &&
        (Diagnostics?.IsValid ?? true) &&
        IsCategoryCompatibleWithTarget();

    private bool IsCategoryCompatibleWithTarget() =>
        Category switch
        {
            RealtimeFanoutCategory.Presence or RealtimeFanoutCategory.Telemetry =>
                Target.Scope is RealtimeFanoutTargetScope.Tenant or RealtimeFanoutTargetScope.Client,
            RealtimeFanoutCategory.Command => Target.Scope == RealtimeFanoutTargetScope.Command,
            RealtimeFanoutCategory.Job => Target.Scope == RealtimeFanoutTargetScope.Job,
            RealtimeFanoutCategory.Terminal => Target.Scope == RealtimeFanoutTargetScope.Terminal,
            RealtimeFanoutCategory.RemoteSupport => Target.Scope == RealtimeFanoutTargetScope.RemoteSupport,
            RealtimeFanoutCategory.FileBrowser => Target.Scope == RealtimeFanoutTargetScope.FileBrowser,
            _ => false
        };
}

public abstract record RealtimeFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
{
    protected abstract RealtimeFanoutCategory Category { get; }

    public RealtimeFanoutEnvelope ToEnvelope() => new(
        RealtimeFanoutEnvelope.CurrentSchemaVersion,
        Category,
        Target,
        EventType,
        Status,
        Timestamp,
        Sequence,
        Revision,
        CorrelationId,
        DiagnosticSummary);
}

public sealed record PresenceFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.Presence;
}

public sealed record TelemetryFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.Telemetry;
}

public sealed record CommandFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.Command;
}

public sealed record JobFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.Job;
}

public sealed record TerminalFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.Terminal;
}

public sealed record FileBrowserFanoutUpdated(
    RealtimeFanoutTarget Target,
    RealtimeFanoutEventType EventType,
    RealtimeFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    RealtimeFanoutDiagnosticSummary? DiagnosticSummary = null)
    : RealtimeFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override RealtimeFanoutCategory Category => RealtimeFanoutCategory.FileBrowser;
}
