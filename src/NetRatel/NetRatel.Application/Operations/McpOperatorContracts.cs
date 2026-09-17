using System.Text.Json.Serialization;

namespace NetRatel.Application.Operations;

/// <summary>Explicit deployment environment evaluated by the operator authority.</summary>
public enum McpOperatorEnvironment : short
{
    Development = 1,
    Production = 2
}

/// <summary>Server-owned client risk classification; it is never inferred from a hostname.</summary>
public enum McpOperatorTargetClassification : short
{
    Unknown = 0,
    DedicatedQa = 1,
    DevelopmentSafe = 2,
    ManagedStandard = 3,
    Restricted = 4,
    CriticalInfrastructure = 5
}

/// <summary>Policy effect. Explicit deny policies always take precedence over allow policies.</summary>
public enum McpOperatorPolicyEffect : short
{
    Deny = 1,
    Allow = 2
}

/// <summary>
/// Durable lifecycle state for an operator policy. Disabled policies remain
/// auditable and inactive; revoked policies are terminal and cannot later be
/// reactivated through this administration boundary.
/// </summary>
public enum McpOperatorPolicyLifecycleState : short
{
    Active = 1,
    Disabled = 2,
    Revoked = 3
}

/// <summary>Bounded caller selectors supported by the persisted policy model.</summary>
public enum McpOperatorPrincipalSelectorKind : short
{
    OAuthSubject = 1,
    OAuthClientId = 2,
    OidcGroup = 3,
    MappedRole = 4,
    ServicePrincipal = 5
}

/// <summary>Bounded target selectors. Tag selection is valid only with an authoritative persisted membership source.</summary>
public enum McpOperatorTargetSelectorKind : short
{
    ExactAgent = 1,
    Tenant = 2,
    ClientTag = 3,
    /// <summary>
    /// Explicitly scopes a policy to a non-agent control-plane operation. Its
    /// selector uses tenant ID zero and cannot authorize any tenant or agent
    /// target. This prevents tenant lifecycle administration from being
    /// accidentally represented as authority over an arbitrary live agent.
    /// </summary>
    ControlPlane = 4,
    /// <summary>
    /// Explicit full-feature Dev testing grant, stored at tenant zero. It covers
    /// current and future Dev targets and is never valid in Production.
    /// </summary>
    DevelopmentEnvironment = 5
}

/// <summary>Operation families used for policy authorization and catalog metadata.</summary>
[Flags]
public enum McpOperatorOperationFamily
{
    None = 0,
    Observability = 1 << 0,
    FileRead = 1 << 1,
    FileWrite = 1 << 2,
    TerminalRead = 1 << 3,
    TerminalExecute = 1 << 4,
    ScriptsRead = 1 << 5,
    ScriptsWrite = 1 << 6,
    AutomationRead = 1 << 7,
    AutomationWrite = 1 << 8,
    JobExecution = 1 << 9,
    TaskExecution = 1 << 10,
    Requests = 1 << 11,
    Onboarding = 1 << 12,
    ClientAdministration = 1 << 13,
    TenantAdministration = 1 << 14,
    Notifications = 1 << 15,
    Events = 1 << 16,
    Connectivity = 1 << 17,
    PolicyAdministration = 1 << 18
}

/// <summary>Risk class used by the two-stage mutation confirmation workflow.</summary>
public enum McpOperatorConfirmationClass : short
{
    None = 0,
    StandardMutation = 1,
    RemoteExecution = 2,
    Destructive = 3,
    FleetWide = 4,
    CredentialIssuance = 5,
    PolicyAdministration = 6
}

/// <summary>Stable authorization layers exposed in safe operator failures.</summary>
public enum McpOperatorAuthorizationLayer : short
{
    Authentication = 1,
    OAuthScope = 2,
    Tenant = 3,
    Target = 4,
    Policy = 5,
    Capability = 6,
    Constraint = 7,
    Confirmation = 8,
    Idempotency = 9,
    Delegation = 10
}

/// <summary>Caller identity preserved from the inbound OAuth token for policy and audit; no bearer token is stored here.</summary>
public sealed record McpOperatorPrincipal(
    string Subject,
    string? ClientId,
    string? AuthorizedParty,
    IReadOnlySet<string> Groups,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Scopes,
    string? ServicePrincipal = null);

/// <summary>One bounded principal selector. Multiple policies express an intentional union of principals.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind Kind, string Value);

/// <summary>One bounded target selector; membership is always resolved server-side.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorTargetSelector(
    McpOperatorTargetSelectorKind Kind,
    int TenantId,
    Guid? AgentId = null,
    string? ClientTag = null);

/// <summary>
/// Policy-enforced limits. Null means the policy does not add that constraint;
/// the operation contract may still impose its own stricter server limit.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorConstraints(
    IReadOnlyList<string>? ReadRoots = null,
    IReadOnlyList<string>? WriteRoots = null,
    IReadOnlyList<string>? AllowedShells = null,
    IReadOnlyList<string>? WorkingDirectories = null,
    int? MaxCommandDurationSeconds = null,
    int? MaxTerminalIdleSeconds = null,
    int? MaxTerminalLifetimeSeconds = null,
    int? MaxConcurrentTerminalSessions = null,
    int? MaxConcurrentCommands = null,
    int? MaxOutputBytes = null,
    int? MaxArtifactBytes = null,
    int? MaxScriptBytes = null,
    int? MaxJobTargetCount = null,
    int? MaxTaskTargetCount = null,
    int? MaxFanOut = null,
    IReadOnlyList<McpOperatorTargetClassification>? AllowedTargetClassifications = null,
    int? MaxOnboardingCodeLifetimeSeconds = null,
    int? MaxOnboardingCodeUses = null,
    McpOperatorConfirmationClass? RequiredConfirmationClass = null,
    bool? DestructiveOperationsAllowed = null,
    bool? AllowActiveTenantDeletion = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpOperatorConstraints))]
[JsonSerializable(typeof(McpOperatorCommandOutput))]
[JsonSerializable(typeof(string[]))]
public partial class McpOperatorJsonContext : JsonSerializerContext
{
}

/// <summary>Persisted policy definition with optimistic versioning and review metadata.</summary>
public sealed record McpOperatorPolicy(
    Guid PolicyId,
    string Name,
    McpOperatorEnvironment Environment,
    McpOperatorPolicyEffect Effect,
    int Priority,
    McpOperatorPrincipalSelector PrincipalSelector,
    McpOperatorTargetSelector TargetSelector,
    McpOperatorTargetClassification? TargetClassification,
    McpOperatorOperationFamily OperationFamily,
    string? Operation,
    McpOperatorConstraints Constraints,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ReviewByUtc,
    DateTimeOffset? DisabledAtUtc,
    string? DisabledBy,
    long Version,
    string? AuditReference = null)
{
    /// <summary>Lifecycle state added without changing the established constructor contract.</summary>
    public McpOperatorPolicyLifecycleState LifecycleState { get; init; } = McpOperatorPolicyLifecycleState.Active;
}

/// <summary>
/// Server-owned classification and bounded tags for one V2 agent. These facts
/// are administration data, never inferred from a hostname or online state.
/// </summary>
public sealed record McpOperatorTargetProfile(
    Guid AgentId,
    int TenantId,
    McpOperatorTargetClassification Classification,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy,
    long Version);

/// <summary>Input for a policy create or replace operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorPolicyDraft(
    string Name,
    McpOperatorEnvironment Environment,
    McpOperatorPolicyEffect Effect,
    int Priority,
    McpOperatorPrincipalSelector PrincipalSelector,
    McpOperatorTargetSelector TargetSelector,
    McpOperatorTargetClassification? TargetClassification,
    McpOperatorOperationFamily OperationFamily,
    string? Operation,
    McpOperatorConstraints Constraints,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ReviewByUtc,
    string? AuditReference = null);

public sealed record McpOperatorPolicyPage(IReadOnlyList<McpOperatorPolicy> Items);

/// <summary>
/// Content-free, append-only evidence of an MCP policy or target-profile
/// change. The referenced policy/profile stores the reviewed definition;
/// this row records who changed which version and when.
/// </summary>
public sealed record McpOperatorPolicyChangeAudit(
    Guid AuditId,
    string Action,
    Guid? PolicyId,
    Guid? AgentId,
    int TenantId,
    string ActorId,
    long Version,
    DateTimeOffset OccurredAtUtc);

/// <summary>Bounded query for immutable accepted MCP operator actions.</summary>
public sealed record McpOperatorAcceptedAuditFilter(
    int? TenantId = null,
    Guid? AgentId = null,
    string? Subject = null,
    int? Limit = null);

/// <summary>One bounded page of content-free accepted-operation evidence.</summary>
public sealed record McpOperatorAcceptedAuditPage(IReadOnlyList<McpOperatorAcceptedAudit> Items);

/// <summary>
/// Trusted facts supplied by a V2 transport adapter after it has verified the
/// MCP service authentication and signed caller delegation. Target readiness
/// facts must come from the current gateway/session, never request JSON.
/// </summary>
public sealed record McpOperatorRouteAccessRequest(
    McpOperatorEnvironment Environment,
    McpOperatorPrincipal Principal,
    string ServicePrincipal,
    string McpResource,
    string McpInstance,
    string Tool,
    string Operation,
    int TenantId,
    Guid AgentId,
    IReadOnlySet<string> RequiredScopes,
    string CorrelationId,
    string RequestId,
    bool TargetOnline,
    bool CapabilityAvailable);

/// <summary>
/// Policy result for one V2 route admission. The descriptor is returned only
/// when the tool/operation pair is catalogued and safe for a route adapter.
/// </summary>
public sealed record McpOperatorRouteAdmission(
    McpOperatorDecision Decision,
    McpOperatorOperationDescriptor? Operation);

/// <summary>Application boundary for policy and target-profile administration.</summary>
public interface IMcpOperatorPolicyAdministration
{
    Task<McpOperatorPolicyPage> ListAsync(
        McpOperatorEnvironment? environment,
        int? tenantId,
        CancellationToken cancellationToken);

    Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(
        int? tenantId,
        Guid? policyId,
        Guid? agentId,
        CancellationToken cancellationToken);

    Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(
        McpOperatorAcceptedAuditFilter filter,
        CancellationToken cancellationToken);

    Task<McpOperatorPolicy> CreateAsync(
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates a proposed policy against the same bounded selector and
    /// persisted-target rules used by creation, without writing policy or
    /// audit state. Confirmation previews use this before issuing a plan.
    /// </summary>
    Task ValidateDraftAsync(
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken);

    Task<McpOperatorPolicy> ReplaceAsync(
        Guid policyId,
        long expectedVersion,
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken);

    Task<McpOperatorPolicy?> DisableAsync(
        Guid policyId,
        long expectedVersion,
        string actorId,
        CancellationToken cancellationToken);

    Task<McpOperatorTargetProfile?> GetTargetProfileAsync(
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken);

    Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(
        int tenantId,
        Guid agentId,
        McpOperatorTargetClassification classification,
        IReadOnlyCollection<string> tags,
        long? expectedVersion,
        string actorId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Separate lifecycle capability so existing policy-administration implementers
/// remain source and binary compatible while policy revocation is introduced.
/// </summary>
public interface IMcpOperatorPolicyRevocation
{
    Task<McpOperatorPolicy?> RevokeAsync(
        Guid policyId,
        long expectedVersion,
        string actorId,
        CancellationToken cancellationToken);
}

/// <summary>
/// V2 route boundary that resolves server-owned tenant/target facts before
/// invoking the general operator policy authority and records admission just
/// before downstream dispatch.
/// </summary>
public interface IMcpOperatorRouteAdmission
{
    Task<McpOperatorRouteAdmission> EvaluateAsync(
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raised when the mandatory re-evaluation immediately before accepted-audit
/// persistence no longer admits an operation. The transport must render the
/// safe failure code instead of treating a policy race as an internal error.
/// </summary>
public sealed class McpOperatorAdmissionRejectedException(string failureCode)
    : InvalidOperationException("The MCP operator route admission was no longer valid.")
{
    public string FailureCode { get; } = string.IsNullOrWhiteSpace(failureCode)
        ? throw new ArgumentException("A safe route admission failure code is required.", nameof(failureCode))
        : failureCode;
}

/// <summary>Authorization input assembled only from trusted transport and server-side target resolution data.</summary>
public sealed record McpOperatorAccessRequest(
    McpOperatorEnvironment Environment,
    McpOperatorPrincipal Principal,
    int TenantId,
    Guid? AgentId,
    McpOperatorTargetClassification? TargetClassification,
    McpOperatorOperationFamily OperationFamily,
    string Operation,
    IReadOnlySet<string> RequiredScopes,
    McpOperatorConfirmationClass ConfirmationClass,
    string CorrelationId,
    string RequestId,
    string? TargetSetDigest = null,
    IReadOnlySet<string>? TargetTags = null,
    string? McpResource = null,
    string? McpInstance = null,
    string? Tool = null,
    bool TenantVisible = true,
    bool TargetResolved = true,
    bool TargetEnabled = true,
    bool TargetOnline = true,
    bool CapabilityAvailable = true);

/// <summary>Safe, deterministic result of one authorization evaluation.</summary>
public sealed record McpOperatorDecision(
    bool IsAllowed,
    string? FailureCode,
    McpOperatorAuthorizationLayer? FailureLayer,
    IReadOnlyList<Guid> MatchingPolicyIds,
    McpOperatorConstraints? EffectiveConstraints,
    string? TargetSetDigest,
    McpOperatorAccessRequest Request,
    long? SelectedPolicyVersion = null,
    bool DevelopmentEnvironmentAccess = false)
{
    public static McpOperatorDecision Denied(
        McpOperatorAccessRequest request,
        string code,
        McpOperatorAuthorizationLayer layer,
        IReadOnlyList<Guid>? matchingPolicyIds = null) =>
        new(false, code, layer, matchingPolicyIds ?? [], null, request.TargetSetDigest, request);
}

/// <summary>Durable status for a previously admitted operator dispatch.</summary>
public enum McpOperatorIdempotencyOutcome : short
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>
/// Input for a zero-upstream-write mutation preview. The payload hash must be
/// the SHA-256 of the canonical closed request body assembled by the route.
/// </summary>
public sealed record McpOperatorConfirmationPlanRequest(
    McpOperatorDecision Decision,
    string PayloadHash,
    DateTimeOffset? ExpiresAtUtc = null);

/// <summary>
/// Opaque plan and idempotency credentials returned from a mutation preview.
/// Neither value encodes the operation payload.
/// </summary>
public sealed record McpOperatorConfirmationPlan(
    string PlanToken,
    string IdempotencyKey,
    DateTimeOffset ExpiresAtUtc,
    McpOperatorConfirmationClass ConfirmationClass,
    string PayloadHash,
    string? TargetSetDigest);

/// <summary>Confirmation input evaluated immediately before dispatch.</summary>
public sealed record McpOperatorConfirmationRequest(
    string PlanToken,
    string IdempotencyKey,
    string PayloadHash,
    McpOperatorDecision CurrentDecision);

/// <summary>
/// Result of confirmation-plan consumption and idempotency admission. Routes
/// dispatch only when <see cref="IsNewDispatch"/> is true.
/// </summary>
public sealed record McpOperatorConfirmationAdmission(
    bool IsNewDispatch,
    bool IsReplay,
    string? FailureCode,
    Guid? IdempotencyId,
    McpOperatorIdempotencyOutcome? Outcome,
    string? ResultReference)
{
    public static McpOperatorConfirmationAdmission Denied(string failureCode) =>
        new(false, false, failureCode, null, null, null);
}

/// <summary>
/// Durable two-stage mutation admission. It is transport-neutral so HTTP,
/// stdio, and CLI use the same plan and replay semantics before dispatch.
/// </summary>
public interface IMcpOperatorConfirmationService
{
    Task<McpOperatorConfirmationPlan> CreatePlanAsync(
        McpOperatorConfirmationPlanRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorConfirmationAdmission> ConfirmAsync(
        McpOperatorConfirmationRequest request,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid idempotencyId,
        McpOperatorIdempotencyOutcome outcome,
        string? resultReference,
        CancellationToken cancellationToken);
}

/// <summary>
/// Short-lived, caller-bound bytes collected from one policy-admitted remote
/// file. The source path is never retained: the root fingerprint proves that
/// the current policy still grants the same bounded root set before the
/// artifact can be inspected or downloaded.
/// </summary>
public sealed record McpOperatorFileArtifact(
    Guid ArtifactId,
    int TenantId,
    Guid AgentId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    string ReadRootFingerprint,
    string FileName,
    long SizeBytes,
    string Sha256,
    string MimeType,
    byte[]? Content,
    Guid AcceptedAuditId,
    Guid IdempotencyId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? DeletedAtUtc);

/// <summary>Content-bearing input accepted only after a confirmed file collection.</summary>
public sealed record McpOperatorFileArtifactCreateRequest(
    McpOperatorRouteAccessRequest Access,
    Guid AcceptedAuditId,
    Guid IdempotencyId,
    string ReadRootFingerprint,
    string FileName,
    string MimeType,
    byte[] Content,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Durable artifact ownership boundary for V2 operator file collection. The
/// artifact store persists no source path or bearer token.
/// </summary>
public interface IMcpOperatorFileArtifactStore
{
    Task<McpOperatorFileArtifact> CreateOrGetAsync(
        McpOperatorFileArtifactCreateRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorFileArtifact?> GetOwnedAsync(
        Guid artifactId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        bool includeContent,
        CancellationToken cancellationToken);

    Task<McpOperatorFileArtifact?> CleanupOwnedAsync(
        Guid artifactId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<int> PurgeExpiredAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken);
}

/// <summary>
/// Durable lifecycle state for a Production operator terminal lease. The
/// transport is deliberately not persisted: this state is only the ownership,
/// policy, and close-recovery boundary for an agent-owned PTY.
/// </summary>
public enum McpOperatorTerminalSessionState : short
{
    Opening = 1,
    Opened = 2,
    Closing = 3,
    Closed = 4,
    Failed = 5
}

/// <summary>
/// Minimal, content-free state required to re-admit a terminal that an agent
/// preserved while the API gateway restarted. It intentionally excludes
/// terminal input and output bytes.
/// </summary>
public sealed record McpOperatorTerminalRecovery(
    string SessionId,
    int TenantId,
    Guid AgentId,
    ulong Generation,
    string ShellType,
    int Columns,
    int Rows,
    DateTimeOffset CreatedAtUtc,
    bool CloseRequested,
    string? CloseReason);

/// <summary>
/// Persistence boundary used only by the terminal gateway when an already
/// authenticated agent reannounces a PTY after API-process loss. Ordinary
/// terminal route ownership remains a separate, scoped operation service.
/// </summary>
public interface IMcpOperatorTerminalSessionRecovery
{
    Task<McpOperatorTerminalRecovery?> TryRecoverAsync(
        int tenantId,
        Guid agentId,
        string sessionId,
        ulong generation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable view of a Production terminal lease. The effective policy
/// constraints are snapshotted here at open time so later policy edits cannot
/// widen a terminal already admitted by the operator.
/// </summary>
public sealed record McpOperatorTerminalSessionLease(
    string SessionId,
    int TenantId,
    Guid AgentId,
    ulong Generation,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    Guid AcceptedAuditId,
    Guid? IdempotencyId,
    string ShellType,
    string? WorkingDirectory,
    int Columns,
    int Rows,
    McpOperatorConstraints EffectiveConstraints,
    McpOperatorTerminalSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? CloseRequestedAtUtc,
    string? CloseReason,
    string? FailureCode,
    long Version);

/// <summary>Content-free input for creating one durable terminal ownership lease.</summary>
public sealed record McpOperatorTerminalSessionCreateRequest(
    string SessionId,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    Guid IdempotencyId,
    string ShellType,
    string? WorkingDirectory,
    int Columns,
    int Rows,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Scoped persistence authority for Production terminal ownership. It records
/// no terminal input or output and is deliberately separate from the transient
/// gateway transport registry.
/// </summary>
public interface IMcpOperatorTerminalSessionStore
{
    Task<McpOperatorTerminalSessionLease> CreateOrGetAsync(
        McpOperatorTerminalSessionCreateRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorTerminalSessionLease?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<McpOperatorTerminalSessionLease?> GetOwnedAsync(
        string sessionId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorTerminalSessionLease?> TouchAsync(
        string sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<McpOperatorTerminalSessionLease?> RequestCloseAsync(
        string sessionId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<McpOperatorTerminalSessionLease?> MarkTerminalAsync(
        string sessionId,
        McpOperatorTerminalSessionState state,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorTerminalSessionLease>> ClaimDueClosesAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken);
}

/// <summary>
/// Content-free, server-derived idempotency admission for a single terminal
/// control frame. The signed delegation request identifier is the replay key;
/// callers never choose it and terminal bytes are represented only by their
/// SHA-256 hash.
/// </summary>
public sealed record McpOperatorTerminalActionAdmissionRequest(
    string SessionId,
    string Operation,
    string DelegationRequestId,
    string PayloadHash,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Durable terminal-control idempotency result. Only a newly admitted action
/// may be sent to the agent transport.
/// </summary>
public sealed record McpOperatorTerminalActionAdmission(
    Guid? ActionId,
    bool IsNewDispatch,
    bool IsReplay,
    string? FailureCode,
    McpOperatorIdempotencyOutcome? Outcome,
    string? ResultReference)
{
    public static McpOperatorTerminalActionAdmission Denied(string failureCode) =>
        new(null, false, false, failureCode, null, null);
}

/// <summary>
/// Records idempotent terminal input, resize, and close admissions separately
/// from the transient gateway. This keeps retry protection durable across API
/// restart and deliberately stores no terminal input or output content.
/// </summary>
public interface IMcpOperatorTerminalActionStore
{
    Task<McpOperatorTerminalActionAdmission> AdmitAsync(
        McpOperatorTerminalActionAdmissionRequest request,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Guid actionId,
        McpOperatorIdempotencyOutcome outcome,
        string resultReference,
        Guid? acceptedAuditId,
        CancellationToken cancellationToken);
}

public sealed class McpOperatorTerminalSessionLimitException(string code)
    : InvalidOperationException("The Production terminal session limit was reached.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Durable, policy-frozen lifecycle state for a one-shot Production command.
/// Command content and command output deliberately do not appear in this
/// model; the command dispatcher transports the former only to the agent and
/// the agent returns bounded, redacted result data through the existing
/// command lifecycle channel.
/// </summary>
public enum McpOperatorCommandState : short
{
    Pending = 1,
    Dispatched = 2,
    Accepted = 3,
    Started = 4,
    Completed = 5,
    Failed = 6,
    CancelRequested = 7,
    Cancelled = 8
}

/// <summary>Immutable operator-visible ownership view for one admitted command.</summary>
public sealed record McpOperatorCommandLease(
    string CommandId,
    int TenantId,
    Guid AgentId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    Guid AcceptedAuditId,
    Guid? IdempotencyId,
    string CorrelationId,
    string ShellType,
    string? WorkingDirectory,
    string CommandHash,
    int CommandLength,
    IReadOnlyList<string> EnvironmentReferences,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    McpOperatorConstraints EffectiveConstraints,
    McpOperatorCommandState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    string? FailureCode,
    long Version)
{
    public McpOperatorCommandOutput? Output { get; init; }
}

public sealed record McpOperatorCommandOutput(int? ExitCode, string[] Stdout, string[] Stderr,
    bool OutputTruncated, string? UnavailableReason = null);

/// <summary>
/// Content-free input for one confirmed command dispatch. The raw command is
/// intentionally not accepted at this storage boundary.
/// </summary>
public sealed record McpOperatorCommandCreateRequest(
    string CommandId,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    Guid IdempotencyId,
    string CorrelationId,
    string ShellType,
    string? WorkingDirectory,
    string CommandHash,
    int CommandLength,
    IReadOnlyList<string> EnvironmentReferences,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Scoped durable ownership authority for Production one-shot commands. It
/// stores no command text or environment values. Terminal output is curated,
/// redacted and bounded by the admitted output limit.
/// </summary>
public interface IMcpOperatorCommandStore
{
    Task<McpOperatorCommandLease> CreateOrGetAsync(
        McpOperatorCommandCreateRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorCommandLease?> GetAsync(string commandId, CancellationToken cancellationToken);

    Task<McpOperatorCommandLease?> GetOwnedAsync(
        string commandId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorCommandLease?> RequestCancelAsync(
        string commandId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task RecordLifecycleAsync(
        string commandId,
        int tenantId,
        Guid agentId,
        McpOperatorCommandState state,
        DateTimeOffset now,
        string? failureCode,
        CancellationToken cancellationToken,
        string? resultJson = null,
        int? exitCode = null);
}

public sealed class McpOperatorCommandLimitException(string code)
    : InvalidOperationException("The Production command limit was reached.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Bounded, reviewable side-effect declarations for a Production library
/// script. They are declarations used for policy/audit review, not a claim
/// that the runtime can safely infer or authorize an undeclared effect.
/// </summary>
public enum McpOperatorScriptSideEffect : short
{
    ReadOnly = 1,
    FileSystemWrite = 2,
    ServiceControl = 3,
    NetworkAccess = 4,
    ProcessExecution = 5
}

/// <summary>
/// One typed library-script parameter. A secret parameter contains only a
/// named client-side reference: this API never accepts a secret value or a
/// secret default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorScriptParameter(
    string Name,
    string Type,
    bool Required,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null,
    string? SecretReference = null);

/// <summary>
/// Typed immutable input for a Production operator script revision. The
/// caller-provided content hash is verified against the UTF-8 content before
/// any script-library row is written.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorScriptDraft(
    string Name,
    string Description,
    string ShellType,
    string Content,
    string ContentHash,
    IReadOnlyList<McpOperatorScriptParameter> Parameters,
    int TimeoutSeconds,
    string WorkingDirectory,
    IReadOnlyList<McpOperatorScriptSideEffect> DeclaredSideEffects,
    string? ManifestJson = null);

/// <summary>
/// Caller-bound, tenant-scoped script metadata. Content is intentionally not
/// included in a list projection; callers retrieve it only through the exact
/// owner-bound get route.
/// </summary>
public sealed record McpOperatorScriptLease(
    long ScriptId,
    int TenantId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    string Name,
    string Description,
    string ShellType,
    string ContentHash,
    string ManifestHash,
    IReadOnlyList<McpOperatorScriptParameter> Parameters,
    int TimeoutSeconds,
    string WorkingDirectory,
    IReadOnlyList<McpOperatorScriptSideEffect> DeclaredSideEffects,
    bool IsDeleted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

/// <summary>
/// Content-free input used to persist one owner-bound script revision after a
/// confirmation plan and accepted-operation audit have been recorded.
/// </summary>
public sealed record McpOperatorScriptCreateRequest(
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorScriptDraft Draft,
    DateTimeOffset OccurredAtUtc);

/// <summary>Content-free input for an optimistic owner-bound script update.</summary>
public sealed record McpOperatorScriptReplaceRequest(
    long ScriptId,
    long ExpectedVersion,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorScriptDraft Draft,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Durable owner-scoped script library authority. It writes the existing
/// source-backed script content only together with an explicit operator
/// ownership row and append-only revision evidence; it never enumerates or
/// mutates legacy ownerless definitions.
/// </summary>
public interface IMcpOperatorScriptStore
{
    /// <summary>Validates a proposed revision against its current target policy without writing any script or audit state.</summary>
    Task ValidateAsync(
        McpOperatorDecision decision,
        McpOperatorScriptDraft draft,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorScriptLease>> ListOwnedAsync(
        int tenantId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<(McpOperatorScriptLease Lease, string Content, string? ManifestJson)?> GetOwnedAsync(
        long scriptId,
        int tenantId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorScriptLease> CreateAsync(
        McpOperatorScriptCreateRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorScriptLease?> ReplaceAsync(
        McpOperatorScriptReplaceRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorScriptLease?> DeleteAsync(
        long scriptId,
        long expectedVersion,
        McpOperatorDecision decision,
        McpOperatorAcceptedAudit acceptedAudit,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Raised when a checked optimistic script write lost its ETag race.</summary>
public sealed class McpOperatorScriptConcurrencyException
    : InvalidOperationException
{
    public McpOperatorScriptConcurrencyException() : base("The script revision no longer matches the supplied ETag.") { }
}

/// <summary>
/// Raised when a legacy write or storage corruption changes the source-backed
/// row behind an operator-owned script revision. The caller must create a new
/// reviewed revision; the altered backing content is never executed.
/// </summary>
public sealed class McpOperatorScriptIntegrityException
    : InvalidOperationException
{
    public McpOperatorScriptIntegrityException() : base("The stored script no longer matches its approved operator revision.") { }
}

/// <summary>
/// Typed definition metadata for a Production operator job. Job steps and
/// parameters have independent contracts so a caller can review each change
/// under optimistic concurrency instead of replacing an opaque workflow blob.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorJobDraft(
    string Name,
    string FolderPath,
    string? Description = null,
    string? OptionsJson = null);

/// <summary>
/// One typed job parameter. Secret parameters carry only a named opaque
/// reference; no plaintext default can enter the job definition or audit.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorJobParameter(
    string Name,
    string Type,
    bool Required,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null,
    string? SecretReference = null,
    long? Id = null);

/// <summary>
/// A Production job step currently permits only an exact reviewed library
/// script revision. Arbitrary commands and unreviewed HTTP actions remain
/// excluded from the job authoring boundary.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorJobStep(
    int? Ordinal,
    long ScriptId,
    long ScriptVersion,
    string ScriptContentHash,
    bool Enabled = true,
    long? Id = null);

/// <summary>
/// Immutable owner and policy snapshot for a tenant-scoped operator job.
/// The target-set digest is frozen in the definition and rechecked when a run
/// is admitted; it is never inferred from a legacy client identity string.
/// </summary>
public sealed record McpOperatorJobLease(
    long JobId,
    int TenantId,
    Guid AgentId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    string TargetSetDigest,
    string Name,
    string FolderPath,
    string? Description,
    string? OptionsJson,
    bool IsDeleted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version);

/// <summary>Bounded, owner-scoped run view with decimal-safe identifiers.</summary>
public sealed record McpOperatorJobRunLease(
    ulong RunId,
    long JobId,
    int TenantId,
    Guid AgentId,
    Guid AcceptedAuditId,
    Guid? IdempotencyId,
    string CorrelationId,
    string TargetSetDigest,
    string State,
    int CurrentStepOrdinal,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    bool IsCancellationRequested,
    bool IsDeleted,
    long Version);

public sealed record McpOperatorJobCreateRequest(
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorJobDraft Draft,
    DateTimeOffset OccurredAtUtc);

public sealed record McpOperatorJobReplaceRequest(
    long JobId,
    long ExpectedVersion,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorJobDraft Draft,
    DateTimeOffset OccurredAtUtc);

public sealed record McpOperatorJobParameterMutationRequest(
    long JobId,
    long? ParameterId,
    long ExpectedVersion,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorJobParameter Parameter,
    DateTimeOffset OccurredAtUtc);

public sealed record McpOperatorJobStepMutationRequest(
    long JobId,
    long? StepId,
    long ExpectedVersion,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    McpOperatorJobStep Step,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Isolates Production job lifecycle writes from legacy generic job services.
/// All visible definitions and runs must have a matching explicit owner row;
/// ownerless historical jobs are intentionally invisible here.
/// </summary>
public interface IMcpOperatorJobStore
{
    Task ValidateDraftAsync(McpOperatorDecision decision, McpOperatorJobDraft draft, CancellationToken cancellationToken);
    Task ValidateParameterAsync(McpOperatorJobParameter parameter, CancellationToken cancellationToken);
    Task ValidateStepAsync(McpOperatorJobStep step, McpOperatorDecision decision, CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorJobLease>> ListOwnedAsync(
        int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorJobLease?> GetOwnedAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken);

    /// <summary>Ensures every enabled step still points at the reviewed exact script revision.</summary>
    Task<bool> IsExecutableAsync(
        long jobId, McpOperatorDecision decision, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorJobParameter>> ListParametersAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorJobStep>> ListStepsAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorJobLease> CreateAsync(McpOperatorJobCreateRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> ReplaceAsync(McpOperatorJobReplaceRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> DeleteAsync(long jobId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);

    Task<(McpOperatorJobLease Job, long ParameterId)?> AddParameterAsync(McpOperatorJobParameterMutationRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> ReplaceParameterAsync(McpOperatorJobParameterMutationRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> DeleteParameterAsync(long jobId, long parameterId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);

    Task<(McpOperatorJobLease Job, long StepId)?> AddStepAsync(McpOperatorJobStepMutationRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> ReplaceStepAsync(McpOperatorJobStepMutationRequest request, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> ReorderStepAsync(long jobId, long stepId, int ordinal, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
    Task<McpOperatorJobLease?> DeleteStepAsync(long jobId, long stepId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);

    Task<McpOperatorJobRunLease?> GetRunOwnedAsync(ulong runId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken);
    Task<IReadOnlyList<McpOperatorJobRunLease>> ListRunsOwnedAsync(long? jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken);
    Task<McpOperatorJobRunLease> RecordStartedRunAsync(long jobId, ulong runId, Guid idempotencyId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
    Task<McpOperatorJobRunLease?> RecordCancellationRequestedAsync(ulong runId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
    Task<McpOperatorJobRunLease?> DeleteRunAsync(ulong runId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken);
}

/// <summary>Raised when a checked job write lost its ETag race.</summary>
public sealed class McpOperatorJobConcurrencyException : InvalidOperationException
{
    public McpOperatorJobConcurrencyException() : base("The job revision no longer matches the supplied ETag.") { }
}

/// <summary>
/// Typed, bounded command input for a one-shot Production task. Environment
/// entries name client-local references only; their values never cross this
/// operator boundary or enter durable operator metadata.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorTaskCommandPayload(
    string Shell,
    string Command,
    string? WorkingDirectory,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    IReadOnlyList<string>? EnvironmentReferences = null);

/// <summary>
/// Exact reviewed library-script revision selected for a one-shot Production
/// task. The script content is resolved server-side from the caller-owned
/// revision; this request contains only revision identity and typed values.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorTaskScriptPayload(
    long ScriptId,
    long Version,
    string ContentHash,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record McpOperatorTaskLease(
    long TaskId,
    string CommandId,
    int TenantId,
    Guid AgentId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    string TargetSetDigest,
    string TaskType,
    string? ShellType,
    long? ScriptId,
    long? ScriptVersion,
    string? ScriptContentHash,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    string State,
    string? ResultSummary,
    bool IsCancellationRequested,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Version);

public sealed record McpOperatorTaskLogLease(
    long LogId,
    long TaskId,
    string RequestId,
    DateTimeOffset TimestampUtc,
    string Stream,
    string Message,
    long Sequence);

/// <summary>Content-free confirmed task creation input.</summary>
public sealed record McpOperatorTaskCreateRequest(
    string CommandId,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    Guid IdempotencyId,
    string CorrelationId,
    string TaskType,
    string? ShellType,
    string? CommandHash,
    int CommandLength,
    long? ScriptId,
    long? ScriptVersion,
    string? ScriptContentHash,
    int TimeoutSeconds,
    int MaximumOutputBytes,
    DateTimeOffset OccurredAtUtc);

public sealed record McpOperatorTaskCancelRequest(
    long TaskId,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Durable owner-scoped authority for one-shot Production tasks. The linked
/// activity projection remains gateway-compatible, but is invisible through
/// this surface unless its ownership record matches the current caller.
/// </summary>
public interface IMcpOperatorTaskStore
{
    Task<McpOperatorTaskLease> CreateOrGetAsync(McpOperatorTaskCreateRequest request, CancellationToken cancellationToken);

    Task<McpOperatorTaskLease?> GetOwnedAsync(
        long taskId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<McpOperatorTaskLease?> GetOwnedByRequestIdAsync(
        string requestId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorTaskLease>> ListOwnedAsync(
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        string? state,
        DateTimeOffset? sinceUtc,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorTaskLogLease>> ListLogsOwnedAsync(
        long taskId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        long sinceLogId,
        string? stream,
        int limit,
        CancellationToken cancellationToken);

    Task<McpOperatorTaskLease?> RequestCancellationAsync(McpOperatorTaskCancelRequest request, CancellationToken cancellationToken);

    Task RecordLifecycleAsync(
        string commandId,
        int tenantId,
        Guid agentId,
        string state,
        string? resultSummary,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

public sealed class McpOperatorTaskLimitException(string code)
    : InvalidOperationException("The Production task limit was reached.")
{
    public string Code { get; } = code;
}

/// <summary>Immutable, content-free evidence that an operator request was admitted before dispatch.</summary>
public sealed record McpOperatorAcceptedAudit(
    Guid AuditId,
    Guid PolicyId,
    McpOperatorEnvironment Environment,
    string ServicePrincipal,
    string Subject,
    string? ClientId,
    string? AuthorizedParty,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    string? McpResource,
    string? McpInstance,
    string? Tool,
    int TenantId,
    Guid? AgentId,
    McpOperatorOperationFamily OperationFamily,
    string Operation,
    string CorrelationId,
    string RequestId,
    DateTimeOffset OccurredAtUtc);

/// <summary>Transport-neutral authority for policy evaluation and accepted-operation audit.</summary>
public interface IMcpOperatorAuthorization
{
    Task<bool> HasTenantVisibilityAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        CancellationToken cancellationToken);

    Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken);

    Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(
        McpOperatorDecision decision,
        string servicePrincipal,
        CancellationToken cancellationToken);
}

/// <summary>Bounded, redacted caller facts returned by the access-evaluation read surface.</summary>
public sealed record McpOperatorAccessCaller(
    string Subject,
    string? ClientId,
    McpOperatorEnvironment Environment,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<int> VisibleTenantIds,
    bool TenantVisibilityTruncated,
    bool DevelopmentEnvironmentAccess = false);

/// <summary>Server-resolved target facts safe to disclose after tenant visibility has been established.</summary>
public sealed record McpOperatorAccessTarget(
    int TenantId,
    Guid AgentId,
    McpOperatorTargetClassification? Classification,
    IReadOnlyList<string> Tags,
    bool Enabled,
    bool ProfileConfigured,
    long? ProfileVersion);

/// <summary>Relevant persisted policy metadata without any selector secrets or request content.</summary>
public sealed record McpOperatorAccessPolicySummary(
    Guid PolicyId,
    string Name,
    McpOperatorPolicyEffect Effect,
    McpOperatorOperationFamily OperationFamily,
    string? Operation,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ReviewByUtc,
    bool Active);

/// <summary>Target lookup result that keeps target facts absent when tenant visibility is denied.</summary>
public sealed record McpOperatorAccessTargetResolution(
    bool TenantVisible,
    McpOperatorAccessTarget? Target,
    IReadOnlyList<McpOperatorAccessPolicySummary> RelevantPolicies);

/// <summary>Read-only policy overview for one already-visible target.</summary>
public sealed record McpOperatorEffectiveAccess(
    McpOperatorAccessCaller Caller,
    McpOperatorAccessTarget Target,
    IReadOnlyList<McpOperatorAccessPolicySummary> MatchingPolicies,
    IReadOnlyList<McpOperatorOperationFamily> AllowedOperationFamilies,
    IReadOnlyList<string> MissingPrerequisites,
    string NextAction);

/// <summary>Safe evaluation of one catalogued operation without dispatching it.</summary>
public sealed record McpOperatorExactAccessEvaluation(
    McpOperatorAccessCaller Caller,
    McpOperatorAccessTarget? Target,
    string Tool,
    string Operation,
    string RequiredScope,
    bool Allowed,
    string? FailureCode,
    McpOperatorAuthorizationLayer? FailureLayer,
    IReadOnlyList<McpOperatorAccessPolicySummary> MatchingPolicies,
    McpOperatorConstraints? EffectiveConstraints,
    string CapabilityReadiness,
    IReadOnlyList<string> MissingPrerequisites,
    string NextAction);

/// <summary>
/// Read-only access inspection. It uses the same persisted policy authority as
/// dispatch adapters but does not issue an accepted-operation audit or invoke
/// a target capability.
/// </summary>
public interface IMcpOperatorAccessEvaluation
{
    Task<McpOperatorAccessCaller> WhoAmIAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        CancellationToken cancellationToken);

    Task<McpOperatorAccessTargetResolution> ResolveTargetAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken);

    Task<McpOperatorEffectiveAccess?> EffectiveAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken);

    Task<McpOperatorExactAccessEvaluation?> EvaluateAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        string tool,
        string operation,
        string correlationId,
        string requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates the signed delegated administrator's own access to one exact
    /// persisted target. Callers must first enforce the distinct administrator
    /// scope and role; unlike general access inspection this diagnostic does
    /// not require the administrator to already have tenant visibility.
    /// </summary>
    Task<McpOperatorExactAccessEvaluation?> EvaluateForPolicyAdministratorAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        string tool,
        string operation,
        string correlationId,
        string requestId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable caller, target, policy, and bounded-result projection over a
/// Production request. The underlying legacy request row remains an internal
/// domain detail and is never exposed without this ownership boundary.
/// </summary>
public sealed record McpOperatorRequestLease(
    int RequestId,
    long JobId,
    int TenantId,
    Guid AgentId,
    string Subject,
    string ClientId,
    string McpResource,
    string McpInstance,
    Guid PolicyId,
    long PolicyVersion,
    string TargetSetDigest,
    string State,
    string Summary,
    string? ResultSummary,
    string? ClaimReferenceHash,
    string CorrelationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Version);

public sealed record McpOperatorRequestCreateRequest(
    long JobId,
    string Summary,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    Guid IdempotencyId,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record McpOperatorRequestMutation(
    int RequestId,
    long ExpectedVersion,
    string Action,
    string? Summary,
    string? ResultSummary,
    string? ClaimReference,
    McpOperatorDecision Decision,
    McpOperatorAcceptedAudit AcceptedAudit,
    DateTimeOffset OccurredAtUtc);

public sealed class McpOperatorRequestLimitException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IMcpOperatorRequestStore
{
    Task<McpOperatorRequestLease> CreateOrGetAsync(
        McpOperatorRequestCreateRequest request,
        CancellationToken cancellationToken);

    Task<McpOperatorRequestLease?> GetOwnedAsync(
        int requestId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpOperatorRequestLease>> ListOwnedAsync(
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        string? state,
        long? jobId,
        DateTimeOffset? sinceUtc,
        int limit,
        CancellationToken cancellationToken);

    Task<McpOperatorRequestLease?> MutateAsync(
        McpOperatorRequestMutation request,
        CancellationToken cancellationToken);
}
