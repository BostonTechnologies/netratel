using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using System.Text.Json;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// PostgreSQL-backed authority for Development operator targets. It admits only
/// explicit QA/Development-safe grants bound to a persisted AgentId and never
/// infers safety from a hostname, an API request environment field, or a
/// client-supplied development flag.
/// </summary>
public sealed class DevelopmentOperatorTargetAuthority(
    OrchestratorDbContext db,
    IHostEnvironment environment,
    IMcpOperatorAuthorization? mcpOperatorAuthorization = null) : IDevelopmentOperatorTargetAuthority
{
    private const int MaxActorLength = 256;
    private const int MaxCorrelationLength = 256;
    private const int MaxEvidenceReferenceLength = 512;
    private const string CompatibilityServicePrincipal = "development-compatibility";
    private const string CompatibilityScope = "netratel.mcp.legacy-dev";
    private readonly OrchestratorDbContext _db = db;
    private readonly IHostEnvironment _environment = environment;
    private readonly IMcpOperatorAuthorization _mcpOperatorAuthorization = mcpOperatorAuthorization ?? new McpOperatorAuthorization(db);

    public async Task<DevelopmentOperatorTargetGrantView> GrantAsync(
        DevelopmentOperatorTargetGrantRequest request,
        CancellationToken cancellationToken)
    {
        ValidateGrantRequest(request);
        RequireDevelopmentEnvironment();

        var now = DateTimeOffset.UtcNow;
        var fileFixtureRoot = DevelopmentFileFixture.NormalizeRoot(request.FileFixtureRoot);
        var agent = await _db.Agents.SingleOrDefaultAsync(
            candidate => candidate.TenantId == request.TenantId && candidate.Id == request.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            throw new KeyNotFoundException("The persisted Agent target was not found for this tenant.");
        }

        if (!agent.IsEnabled || agent.Status == AgentStatus.Disabled)
        {
            throw new InvalidOperationException("The persisted Agent target is not enabled.");
        }

        var previous = await _db.DevelopmentOperatorTargetGrants
            .SingleOrDefaultAsync(grant => grant.AgentId == request.AgentId && grant.RevokedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (previous is not null)
        {
            previous.RevokedAtUtc = now;
            previous.RevokedBy = request.ActorId;
            previous.RevocationReason = "superseded_by_new_grant";
            await DisableCompatibilityPolicyAsync(previous.Id, request.ActorId, now, cancellationToken).ConfigureAwait(false);
        }

        var grant = new DevelopmentOperatorTargetGrant
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            AgentId = request.AgentId,
            Classification = request.Classification,
            AllowedOperations = request.AllowedOperations,
            FileFixtureRoot = fileFixtureRoot,
            EvidenceReference = request.EvidenceReference.Trim(),
            GrantedBy = request.ActorId.Trim(),
            GrantedAtUtc = now,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
        _db.DevelopmentOperatorTargetGrants.Add(grant);
        _db.McpOperatorPolicies.Add(CreateCompatibilityPolicy(grant));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(grant, now);
    }

    public async Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(
        int tenantId,
        Guid agentId,
        string reason,
        string actorId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        RequireDevelopmentEnvironment();
        if (tenantId <= 0 || agentId == Guid.Empty || !IsSafeToken(actorId, MaxActorLength) ||
            !IsSafeToken(correlationId, MaxCorrelationLength) || !IsSafeToken(reason, 256))
        {
            throw new ArgumentException("A persisted target, trusted operator identity, correlation identifier, and bounded revocation reason are required.");
        }

        var grant = await _db.DevelopmentOperatorTargetGrants.SingleOrDefaultAsync(candidate =>
            candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.RevokedAtUtc == null,
            cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        grant.RevokedAtUtc = now;
        grant.RevokedBy = actorId.Trim();
        grant.RevocationReason = reason.Trim();
        await DisableCompatibilityPolicyAsync(grant.Id, actorId, now, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToView(grant, now);
    }

    public async Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var grant = await _db.DevelopmentOperatorTargetGrants.AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.TenantId == tenantId &&
                candidate.AgentId == agentId &&
                candidate.RevokedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
        return grant is not null && IsActive(grant, now) ? ToView(grant, now) : null;
    }

    public async Task<DevelopmentOperatorTargetDecision> EvaluateAsync(
        DevelopmentOperatorTargetRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return Reject(request, "development_environment_required");
        }

        if (request.TenantId <= 0 || request.AgentId == Guid.Empty)
        {
            return Reject(request, "target_identity_invalid");
        }

        if (!IsSafeToken(request.ActorId, MaxActorLength))
        {
            return Reject(request, "operator_identity_invalid");
        }

        if (!IsSafeToken(request.CorrelationId, MaxCorrelationLength))
        {
            return Reject(request, "correlation_invalid");
        }

        var agent = await _db.Agents.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == request.TenantId && candidate.Id == request.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return Reject(request, "target_not_found");
        }

        if (!agent.IsEnabled || agent.Status == AgentStatus.Disabled)
        {
            return Reject(request, "target_not_enabled");
        }

        var grant = await _db.DevelopmentOperatorTargetGrants.AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.TenantId == request.TenantId &&
                candidate.AgentId == request.AgentId &&
                candidate.RevokedAtUtc == null,
                cancellationToken).ConfigureAwait(false);
        if (grant is null || !IsActive(grant, DateTimeOffset.UtcNow))
        {
            return Reject(request, "target_not_authorized");
        }

        var authorization = await _mcpOperatorAuthorization.EvaluateAsync(
            CompatibilityRequest(request, grant, agent.IsEnabled && agent.Status != AgentStatus.Disabled),
            cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed)
            return Reject(request, authorization.FailureCode ?? "target_policy_missing");

        return new DevelopmentOperatorTargetDecision(true, null, grant.Id, request);
    }

    public async Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(
        DevelopmentOperatorTargetDecision decision,
        CancellationToken cancellationToken)
    {
        if (!decision.IsAllowed || decision.GrantId is not { } grantId)
        {
            throw new InvalidOperationException("A rejected Development target decision cannot be recorded as accepted.");
        }

        var grant = await _db.DevelopmentOperatorTargetGrants.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == grantId && candidate.TenantId == decision.Request.TenantId && candidate.AgentId == decision.Request.AgentId &&
            candidate.RevokedAtUtc == null,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Development target grant is no longer available for compatibility audit.");
        var agent = await _db.Agents.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TenantId == decision.Request.TenantId && candidate.Id == decision.Request.AgentId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Development target is no longer available for compatibility audit.");
        var operatorDecision = await _mcpOperatorAuthorization.EvaluateAsync(
            CompatibilityRequest(decision.Request, grant, agent.IsEnabled && agent.Status != AgentStatus.Disabled),
            cancellationToken).ConfigureAwait(false);
        if (!operatorDecision.IsAllowed)
            throw new InvalidOperationException("The general operator policy changed before accepted-operation audit could be recorded.");

        await _mcpOperatorAuthorization.RecordAcceptedAsync(operatorDecision, CompatibilityServicePrincipal, cancellationToken).ConfigureAwait(false);

        var audit = new DevelopmentOperatorAcceptedAuditRecord
        {
            Id = Guid.NewGuid(),
            TenantId = decision.Request.TenantId,
            AgentId = decision.Request.AgentId,
            TargetGrantId = grantId,
            Operation = decision.Request.Operation,
            ActorId = decision.Request.ActorId.Trim(),
            CorrelationId = decision.Request.CorrelationId.Trim(),
            OccurredAtUtc = DateTimeOffset.UtcNow
        };
        _db.DevelopmentOperatorAcceptedAudits.Add(audit);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DevelopmentOperatorAcceptedAudit(
            audit.Id,
            audit.TenantId,
            audit.AgentId,
            audit.Operation,
            audit.ActorId,
            audit.CorrelationId,
            audit.OccurredAtUtc);
    }

    private void RequireDevelopmentEnvironment()
    {
        if (!_environment.IsDevelopment())
        {
            throw new InvalidOperationException("Development target grants are unavailable outside the Development environment.");
        }
    }

    private static DevelopmentOperatorTargetDecision Reject(DevelopmentOperatorTargetRequest request, string code) =>
        new(false, code, null, request);

    private static bool IsActive(DevelopmentOperatorTargetGrant grant, DateTimeOffset now) =>
        grant.RevokedAtUtc is null && grant.ExpiresAtUtc > now;

    private static DevelopmentOperatorTargetGrantView ToView(DevelopmentOperatorTargetGrant grant, DateTimeOffset now) =>
        new(
            grant.Id,
            grant.TenantId,
            grant.AgentId,
            grant.Classification,
            grant.AllowedOperations,
            grant.GrantedAtUtc,
            grant.ExpiresAtUtc,
            IsActive(grant, now),
            grant.EvidenceReference,
            grant.FileFixtureRoot);

    private async Task DisableCompatibilityPolicyAsync(
        Guid grantId,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var policy = await _db.McpOperatorPolicies.SingleOrDefaultAsync(
            candidate => candidate.Id == CompatibilityPolicyId(grantId) && candidate.DisabledAtUtc == null,
            cancellationToken).ConfigureAwait(false);
        if (policy is null)
            return;

        policy.DisabledAtUtc = now;
        policy.DisabledBy = actorId.Trim();
        policy.Version++;
    }

    private static McpOperatorPolicyRecord CreateCompatibilityPolicy(DevelopmentOperatorTargetGrant grant) => new()
    {
        Id = CompatibilityPolicyId(grant.Id),
        Name = $"Legacy Dev target grant {grant.Id:D}",
        Environment = McpOperatorEnvironment.Development,
        Effect = McpOperatorPolicyEffect.Allow,
        Priority = 0,
        PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.ServicePrincipal,
        PrincipalSelectorValue = CompatibilityServicePrincipal,
        TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
        TenantId = grant.TenantId,
        AgentId = grant.AgentId,
        TargetClassification = ToMcpClassification(grant.Classification),
        OperationFamily = ToMcpOperationFamily(grant.AllowedOperations),
        ConstraintsJson = JsonSerializer.Serialize(CompatibilityConstraints(grant), McpOperatorJsonContext.Default.McpOperatorConstraints),
        CreatedAtUtc = grant.GrantedAtUtc,
        CreatedBy = grant.GrantedBy,
        ExpiresAtUtc = grant.ExpiresAtUtc,
        Version = 1,
        AuditReference = $"legacy-dev-grant:{grant.Id:D}"
    };

    private static McpOperatorAccessRequest CompatibilityRequest(
        DevelopmentOperatorTargetRequest request,
        DevelopmentOperatorTargetGrant grant,
        bool targetEnabled) => new(
        McpOperatorEnvironment.Development,
        new McpOperatorPrincipal(
            CompatibilityServicePrincipal,
            null,
            null,
            EmptySet(),
            EmptySet(),
            Set(CompatibilityScope),
            CompatibilityServicePrincipal),
        request.TenantId,
        request.AgentId,
        ToMcpClassification(grant.Classification),
        ToMcpOperationFamily(request.Operation),
        $"legacy/{request.Operation}",
        Set(CompatibilityScope),
        McpOperatorConfirmationClass.None,
        request.CorrelationId,
        $"legacy-{request.Operation}-{request.CorrelationId}",
        McpResource: null,
        McpInstance: "dev",
        Tool: "development-compatibility",
        TargetEnabled: targetEnabled,
        TargetOnline: true,
        CapabilityAvailable: true);

    private static McpOperatorConstraints CompatibilityConstraints(DevelopmentOperatorTargetGrant grant) =>
        grant.FileFixtureRoot is null
            ? new McpOperatorConstraints()
            : new McpOperatorConstraints([grant.FileFixtureRoot], [grant.FileFixtureRoot]);

    private static Guid CompatibilityPolicyId(Guid grantId)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"legacy-dev-mcp-policy:{grantId:D}"));
        return Guid.ParseExact(Convert.ToHexString(hash), "N");
    }

    private static McpOperatorTargetClassification ToMcpClassification(DevelopmentOperatorTargetClassification classification) => classification switch
    {
        DevelopmentOperatorTargetClassification.DedicatedQa => McpOperatorTargetClassification.DedicatedQa,
        DevelopmentOperatorTargetClassification.DevelopmentSafe => McpOperatorTargetClassification.DevelopmentSafe,
        _ => McpOperatorTargetClassification.Unknown
    };

    private static McpOperatorOperationFamily ToMcpOperationFamily(DevelopmentOperatorOperationScope scope)
    {
        var mapped = McpOperatorOperationFamily.None;
        if ((scope & DevelopmentOperatorOperationScope.JobRuns) != 0)
            mapped |= McpOperatorOperationFamily.JobExecution | McpOperatorOperationFamily.AutomationWrite | McpOperatorOperationFamily.Requests;
        if ((scope & DevelopmentOperatorOperationScope.Tasks) != 0) mapped |= McpOperatorOperationFamily.TaskExecution;
        if ((scope & DevelopmentOperatorOperationScope.GatewayCommands) != 0) mapped |= McpOperatorOperationFamily.TerminalExecute;
        if ((scope & DevelopmentOperatorOperationScope.FileSystem) != 0) mapped |= McpOperatorOperationFamily.FileRead | McpOperatorOperationFamily.FileWrite;
        if ((scope & DevelopmentOperatorOperationScope.Terminal) != 0) mapped |= McpOperatorOperationFamily.TerminalRead | McpOperatorOperationFamily.TerminalExecute;
        if ((scope & DevelopmentOperatorOperationScope.Scripts) != 0) mapped |= McpOperatorOperationFamily.ScriptsRead | McpOperatorOperationFamily.ScriptsWrite;
        if ((scope & DevelopmentOperatorOperationScope.Onboarding) != 0) mapped |= McpOperatorOperationFamily.Onboarding;
        if ((scope & DevelopmentOperatorOperationScope.Events) != 0) mapped |= McpOperatorOperationFamily.Events;
        if ((scope & DevelopmentOperatorOperationScope.Connectivity) != 0) mapped |= McpOperatorOperationFamily.Connectivity;
        if ((scope & DevelopmentOperatorOperationScope.Observability) != 0) mapped |= McpOperatorOperationFamily.Observability;
        return mapped;
    }

    private static McpOperatorOperationFamily ToMcpOperationFamily(DevelopmentOperatorOperation operation) => operation switch
    {
        DevelopmentOperatorOperation.JobRunStart or DevelopmentOperatorOperation.JobRunCancel or DevelopmentOperatorOperation.JobRunDelete => McpOperatorOperationFamily.JobExecution,
        DevelopmentOperatorOperation.JobDefinitionMutation => McpOperatorOperationFamily.AutomationWrite,
        DevelopmentOperatorOperation.TaskCreate => McpOperatorOperationFamily.TaskExecution,
        DevelopmentOperatorOperation.GatewayCommandDispatch or DevelopmentOperatorOperation.GatewayCommandCancel => McpOperatorOperationFamily.TerminalExecute,
        DevelopmentOperatorOperation.FileBrowse or DevelopmentOperatorOperation.FileRead or DevelopmentOperatorOperation.FileArtifactStatus or DevelopmentOperatorOperation.FileArtifactDownload => McpOperatorOperationFamily.FileRead,
        DevelopmentOperatorOperation.FileWrite or DevelopmentOperatorOperation.FileCollect or DevelopmentOperatorOperation.FileArtifactCleanup => McpOperatorOperationFamily.FileWrite,
        DevelopmentOperatorOperation.TerminalAvailability or DevelopmentOperatorOperation.TerminalSessionRead or DevelopmentOperatorOperation.TerminalStreamRead => McpOperatorOperationFamily.TerminalRead,
        DevelopmentOperatorOperation.TerminalOpen or DevelopmentOperatorOperation.TerminalInput or DevelopmentOperatorOperation.TerminalResize or DevelopmentOperatorOperation.TerminalClose or DevelopmentOperatorOperation.TerminalSelfTest or DevelopmentOperatorOperation.TerminalDeploymentControlPlaneInspect => McpOperatorOperationFamily.TerminalExecute,
        DevelopmentOperatorOperation.ScriptMutation => McpOperatorOperationFamily.ScriptsWrite,
        DevelopmentOperatorOperation.OnboardingMutation or DevelopmentOperatorOperation.OnboardingCollateralRead or DevelopmentOperatorOperation.OnboardingEnrollmentMetadataRead => McpOperatorOperationFamily.Onboarding,
        DevelopmentOperatorOperation.EventMutation => McpOperatorOperationFamily.Events,
        DevelopmentOperatorOperation.ConnectivityMutation or DevelopmentOperatorOperation.AgentPing => McpOperatorOperationFamily.Connectivity,
        DevelopmentOperatorOperation.ClientLogRead or DevelopmentOperatorOperation.ClientLogResync or DevelopmentOperatorOperation.ClientTelemetryRead => McpOperatorOperationFamily.Observability,
        DevelopmentOperatorOperation.RequestSubmit => McpOperatorOperationFamily.Requests,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "A general MCP operator family is required.")
    };

    private static IReadOnlySet<string> EmptySet() => new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> Set(string value) => new HashSet<string>([value], StringComparer.Ordinal);

    private static void ValidateGrantRequest(DevelopmentOperatorTargetGrantRequest request)
    {
        if (request.TenantId <= 0 || request.AgentId == Guid.Empty)
        {
            throw new ArgumentException("A persisted tenant and AgentId are required.", nameof(request));
        }

        if (!request.Classification.IsEligible())
        {
            throw new ArgumentException("Only DedicatedQa or DevelopmentSafe classifications may be granted.", nameof(request));
        }

        if (request.AllowedOperations == DevelopmentOperatorOperationScope.None ||
            (request.AllowedOperations & ~DevelopmentOperatorOperationScope.All) != 0)
        {
            throw new ArgumentException("At least one recognized Development operation scope is required.", nameof(request));
        }

        var fileFixtureRoot = DevelopmentFileFixture.NormalizeRoot(request.FileFixtureRoot);
        var allowsFileSystem = (request.AllowedOperations & DevelopmentOperatorOperationScope.FileSystem) != 0;
        if (allowsFileSystem && fileFixtureRoot is null)
        {
            throw new ArgumentException("A canonical Development file fixture root is required when FileSystem operations are granted.", nameof(request));
        }

        if (!allowsFileSystem && fileFixtureRoot is not null)
        {
            throw new ArgumentException("A file fixture root is only valid when FileSystem operations are granted.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.AddDays(30))
        {
            throw new ArgumentException("The target grant expiry must be in the next 30 days.", nameof(request));
        }

        if (!IsSafeToken(request.ActorId, MaxActorLength) || !IsSafeToken(request.CorrelationId, MaxCorrelationLength))
        {
            throw new ArgumentException("A trusted bounded operator identity and correlation identifier are required.", nameof(request));
        }

        if (!IsSafeToken(request.EvidenceReference, MaxEvidenceReferenceLength))
        {
            throw new ArgumentException("A bounded evidence reference is required.", nameof(request));
        }
    }

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '@' or '_' or '-' or '.' or ':' or '/' or '#');
}
