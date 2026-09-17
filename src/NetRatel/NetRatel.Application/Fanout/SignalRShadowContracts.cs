namespace NetRatel.Application.Fanout;

public enum ShadowFanoutCategory
{
    Presence = 0,
    Telemetry = 1,
    Command = 2,
    Job = 3,
    Terminal = 4,
    RemoteSupport = 5,
    FileBrowser = 6
}

public enum ShadowFanoutTargetScope
{
    Tenant = 0,
    Client = 1,
    Terminal = 2,
    RemoteSupport = 3,
    FileBrowser = 4,
    Job = 5,
    Command = 6
}

public enum ShadowFanoutEventType
{
    Updated = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    Expired = 4,
    Snapshot = 5
}

public enum ShadowFanoutStatus
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
/// Identifies exactly one authorized SignalR shadow group. Identifier text is
/// bounded and is hashed before it becomes part of a server-side group name.
/// </summary>
public sealed record ShadowFanoutTarget(
    int TenantId,
    ShadowFanoutTargetScope Scope,
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
            ShadowFanoutTargetScope.Tenant => HasNoEntityIdentifiers(),
            ShadowFanoutTargetScope.Client =>
                IsSafeIdentifier(ClientId) && HasNoIdentifiersExceptClient(),
            ShadowFanoutTargetScope.Terminal or ShadowFanoutTargetScope.RemoteSupport =>
                IsSafeIdentifier(ClientId) && IsSafeIdentifier(SessionId) &&
                RequestId is null && JobId is null && CommandId is null,
            ShadowFanoutTargetScope.FileBrowser =>
                IsSafeIdentifier(ClientId) && IsSafeIdentifier(RequestId) &&
                SessionId is null && JobId is null && CommandId is null,
            ShadowFanoutTargetScope.Job =>
                JobId > 0 && ClientId is null && SessionId is null &&
                RequestId is null && CommandId is null,
            ShadowFanoutTargetScope.Command =>
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
public sealed record ShadowFanoutDiagnosticSummary(
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
/// Versioned, latest-state metadata sent only by the local SignalR shadow
/// bridge. Every envelope targets one group. DEV authority publication sets
/// <see cref="IsAuthoritative"/> only after its fenced gateway path is active.
/// </summary>
public sealed record ShadowFanoutEnvelope(
    int SchemaVersion,
    ShadowFanoutCategory Category,
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? Diagnostics = null,
    bool ResyncRequired = false,
    bool IsAuthoritative = false)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsValid =>
        SchemaVersion == CurrentSchemaVersion &&
        Target.IsValid &&
        Timestamp != default &&
        (CorrelationId is null || ShadowFanoutTarget.IsSafeIdentifier(CorrelationId)) &&
        (Diagnostics?.IsValid ?? true) &&
        IsCategoryCompatibleWithTarget();

    private bool IsCategoryCompatibleWithTarget() =>
        Category switch
        {
            ShadowFanoutCategory.Presence or ShadowFanoutCategory.Telemetry =>
                Target.Scope is ShadowFanoutTargetScope.Tenant or ShadowFanoutTargetScope.Client,
            ShadowFanoutCategory.Command => Target.Scope == ShadowFanoutTargetScope.Command,
            ShadowFanoutCategory.Job => Target.Scope == ShadowFanoutTargetScope.Job,
            ShadowFanoutCategory.Terminal => Target.Scope == ShadowFanoutTargetScope.Terminal,
            ShadowFanoutCategory.RemoteSupport => Target.Scope == ShadowFanoutTargetScope.RemoteSupport,
            ShadowFanoutCategory.FileBrowser => Target.Scope == ShadowFanoutTargetScope.FileBrowser,
            _ => false
        };
}

public abstract record ShadowFanoutUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
{
    protected abstract ShadowFanoutCategory Category { get; }

    public ShadowFanoutEnvelope ToEnvelope() => new(
        ShadowFanoutEnvelope.CurrentSchemaVersion,
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

public sealed record PresenceShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.Presence;
}

public sealed record TelemetryShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.Telemetry;
}

public sealed record CommandShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.Command;
}

public sealed record JobShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.Job;
}

public sealed record TerminalShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.Terminal;
}

public sealed record FileBrowserShadowUpdated(
    ShadowFanoutTarget Target,
    ShadowFanoutEventType EventType,
    ShadowFanoutStatus Status,
    DateTimeOffset Timestamp,
    ulong? Sequence = null,
    long? Revision = null,
    string? CorrelationId = null,
    ShadowFanoutDiagnosticSummary? DiagnosticSummary = null)
    : ShadowFanoutUpdated(Target, EventType, Status, Timestamp, Sequence, Revision, CorrelationId, DiagnosticSummary)
{
    protected override ShadowFanoutCategory Category => ShadowFanoutCategory.FileBrowser;
}
