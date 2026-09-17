namespace NetRatel.Application.Operations;

/// <summary>
/// An explicit, server-owned classification for a client that may be used by
/// the Development operator surface. Hostnames and caller-supplied flags are
/// deliberately not classifications.
/// </summary>
public enum DevelopmentOperatorTargetClassification : short
{
    Unknown = 0,
    DedicatedQa = 1,
    DevelopmentSafe = 2
}

/// <summary>
/// Bounded families of actions that a Development target grant can allow.
/// New remote actions must be added here before they can be admitted.
/// </summary>
[Flags]
public enum DevelopmentOperatorOperationScope
{
    None = 0,
    JobRuns = 1 << 0,
    Tasks = 1 << 1,
    GatewayCommands = 1 << 2,
    FileSystem = 1 << 3,
    Terminal = 1 << 4,
    Scripts = 1 << 5,
    Onboarding = 1 << 6,
    Events = 1 << 7,
    Connectivity = 1 << 8,
    Observability = 1 << 9,
    All = JobRuns | Tasks | GatewayCommands | FileSystem | Terminal | Scripts | Onboarding | Events | Connectivity | Observability
}

/// <summary>Individual accepted actions that require a server-side Dev target grant.</summary>
public enum DevelopmentOperatorOperation : short
{
    JobRunStart = 1,
    JobRunCancel = 2,
    JobRunDelete = 3,
    TaskCreate = 4,
    GatewayCommandDispatch = 5,
    GatewayCommandCancel = 6,
    FileWrite = 7,
    TerminalOpen = 8,
    TerminalInput = 9,
    TerminalResize = 10,
    TerminalClose = 11,
    ScriptMutation = 12,
    OnboardingMutation = 13,
    EventMutation = 14,
    ConnectivityMutation = 15,
    RequestSubmit = 16,
    AgentPing = 17,
    FileBrowse = 18,
    FileRead = 19,
    FileCollect = 20,
    FileArtifactCleanup = 21,
    FileArtifactStatus = 22,
    FileArtifactDownload = 23,
    ClientLogRead = 24,
    ClientTelemetryRead = 25,
    TerminalAvailability = 26,
    TerminalSessionRead = 27,
    TerminalStreamRead = 28,
    TerminalSelfTest = 29,
    OnboardingCollateralRead = 30,
    OnboardingEnrollmentMetadataRead = 31,
    JobDefinitionMutation = 32,
    TerminalDeploymentControlPlaneInspect = 33,
    ClientLogResync = 34
}

public static class DevelopmentOperatorOperationExtensions
{
    public static DevelopmentOperatorOperationScope RequiredScope(this DevelopmentOperatorOperation operation) => operation switch
    {
        DevelopmentOperatorOperation.JobRunStart or
        DevelopmentOperatorOperation.JobRunCancel or
        DevelopmentOperatorOperation.JobRunDelete or
        DevelopmentOperatorOperation.JobDefinitionMutation or
        DevelopmentOperatorOperation.RequestSubmit => DevelopmentOperatorOperationScope.JobRuns,
        DevelopmentOperatorOperation.TaskCreate => DevelopmentOperatorOperationScope.Tasks,
        DevelopmentOperatorOperation.GatewayCommandDispatch or
        DevelopmentOperatorOperation.GatewayCommandCancel => DevelopmentOperatorOperationScope.GatewayCommands,
        DevelopmentOperatorOperation.FileWrite or
        DevelopmentOperatorOperation.FileBrowse or
        DevelopmentOperatorOperation.FileRead or
        DevelopmentOperatorOperation.FileCollect or
        DevelopmentOperatorOperation.FileArtifactCleanup or
        DevelopmentOperatorOperation.FileArtifactStatus or
        DevelopmentOperatorOperation.FileArtifactDownload => DevelopmentOperatorOperationScope.FileSystem,
        DevelopmentOperatorOperation.TerminalOpen or
        DevelopmentOperatorOperation.TerminalInput or
        DevelopmentOperatorOperation.TerminalResize or
        DevelopmentOperatorOperation.TerminalClose or
        DevelopmentOperatorOperation.TerminalAvailability or
        DevelopmentOperatorOperation.TerminalSessionRead or
        DevelopmentOperatorOperation.TerminalStreamRead or
        DevelopmentOperatorOperation.TerminalSelfTest or
        DevelopmentOperatorOperation.TerminalDeploymentControlPlaneInspect => DevelopmentOperatorOperationScope.Terminal,
        DevelopmentOperatorOperation.ScriptMutation => DevelopmentOperatorOperationScope.Scripts,
        DevelopmentOperatorOperation.OnboardingMutation => DevelopmentOperatorOperationScope.Onboarding,
        DevelopmentOperatorOperation.OnboardingCollateralRead or
        DevelopmentOperatorOperation.OnboardingEnrollmentMetadataRead => DevelopmentOperatorOperationScope.Onboarding,
        DevelopmentOperatorOperation.EventMutation => DevelopmentOperatorOperationScope.Events,
        DevelopmentOperatorOperation.ConnectivityMutation or
        DevelopmentOperatorOperation.AgentPing => DevelopmentOperatorOperationScope.Connectivity,
        DevelopmentOperatorOperation.ClientLogRead or
        DevelopmentOperatorOperation.ClientLogResync or
        DevelopmentOperatorOperation.ClientTelemetryRead => DevelopmentOperatorOperationScope.Observability,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "A Development operator scope is required.")
    };

    public static bool IsEligible(this DevelopmentOperatorTargetClassification classification) =>
        classification is DevelopmentOperatorTargetClassification.DedicatedQa or DevelopmentOperatorTargetClassification.DevelopmentSafe;
}

public sealed record DevelopmentOperatorTargetGrantRequest(
    int TenantId,
    Guid AgentId,
    DevelopmentOperatorTargetClassification Classification,
    DevelopmentOperatorOperationScope AllowedOperations,
    DateTimeOffset ExpiresAtUtc,
    string EvidenceReference,
    string ActorId,
    string CorrelationId,
    string? FileFixtureRoot = null);

public sealed record DevelopmentOperatorTargetGrantView(
    Guid GrantId,
    int TenantId,
    Guid AgentId,
    DevelopmentOperatorTargetClassification Classification,
    DevelopmentOperatorOperationScope AllowedOperations,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsActive,
    string EvidenceReference,
    string? FileFixtureRoot = null);

public sealed record DevelopmentOperatorTargetRequest(
    int TenantId,
    Guid AgentId,
    DevelopmentOperatorOperation Operation,
    string ActorId,
    string CorrelationId);

public sealed record DevelopmentOperatorTargetDecision(
    bool IsAllowed,
    string? RejectionCode,
    Guid? GrantId,
    DevelopmentOperatorTargetRequest Request);

public sealed record DevelopmentOperatorAcceptedAudit(
    Guid AuditId,
    int TenantId,
    Guid AgentId,
    DevelopmentOperatorOperation Operation,
    string ActorId,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Lexical boundary for a Development-only, server-approved file fixture.
/// The API cannot trust a client path, and it must not use the host's runtime
/// filesystem to interpret a remote agent path.
/// </summary>
public static class DevelopmentFileFixture
{
    public static string? NormalizeRoot(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeAbsolutePath(value);

    public static bool Contains(string? fixtureRoot, string? candidate)
    {
        if (fixtureRoot is null || candidate is null)
        {
            return false;
        }

        try
        {
            var root = NormalizeAbsolutePath(fixtureRoot);
            var path = NormalizeAbsolutePath(candidate);
            var windows = IsWindowsPath(root);
            if (windows != IsWindowsPath(path))
            {
                return false;
            }

            var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(root, path, comparison))
            {
                return true;
            }

            var separator = windows ? "\\" : "/";
            return path.StartsWith(root + separator, comparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeAbsolutePath(string value)
    {
        var path = value.Trim();
        if (path.Length is 0 or > 4096 || path.Contains('\0'))
        {
            throw new ArgumentException("The file fixture path must be a bounded absolute path.", nameof(value));
        }

        if (IsWindowsPath(path))
        {
            var drive = char.ToUpperInvariant(path[0]) + @":\";
            var segments = path[3..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            ValidateSegments(segments);
            if (segments.Length == 0)
            {
                throw new ArgumentException("The file fixture path must not be a filesystem root.", nameof(value));
            }

            return drive + string.Join("\\", segments);
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The file fixture path must be an absolute POSIX or Windows path.", nameof(value));
        }

        var posixSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        ValidateSegments(posixSegments);
        if (posixSegments.Length == 0)
        {
            throw new ArgumentException("The file fixture path must not be a filesystem root.", nameof(value));
        }

        return "/" + string.Join("/", posixSegments);
    }

    private static bool IsWindowsPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static void ValidateSegments(IEnumerable<string> segments)
    {
        if (segments.Any(segment => segment is "." or ".." || segment.Contains('\0')))
        {
            throw new ArgumentException("The file fixture path may not contain traversal segments.");
        }
    }
}

/// <summary>
/// Owns the Development-only target gate and immutable acceptance audit for
/// remote operator actions. Callers cannot satisfy this gate with a hostname,
/// an environment field, or an untrusted client identity.
/// </summary>
public interface IDevelopmentOperatorTargetAuthority
{
    Task<DevelopmentOperatorTargetGrantView> GrantAsync(DevelopmentOperatorTargetGrantRequest request, CancellationToken cancellationToken);

    Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(
        int tenantId,
        Guid agentId,
        string reason,
        string actorId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(int tenantId, Guid agentId, CancellationToken cancellationToken);

    Task<DevelopmentOperatorTargetDecision> EvaluateAsync(DevelopmentOperatorTargetRequest request, CancellationToken cancellationToken);

    Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(DevelopmentOperatorTargetDecision decision, CancellationToken cancellationToken);
}
