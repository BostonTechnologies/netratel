using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Transactional administration boundary for general MCP policies and target
/// profiles. It validates every selector before persistence, uses explicit
/// optimistic versions, and never accepts a hostname or online-state as a
/// target classification.
/// </summary>
public sealed class McpOperatorPolicyAdministration(OrchestratorDbContext db) : IMcpOperatorPolicyAdministration, IMcpOperatorPolicyRevocation
{
    private const int MaxIdentityLength = 256;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorPolicyPage> ListAsync(
        McpOperatorEnvironment? environment,
        int? tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId is < 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId));

        var query = _db.McpOperatorPolicies.AsNoTracking().AsQueryable();
        if (environment is { } selectedEnvironment)
            query = query.Where(policy => policy.Environment == selectedEnvironment);
        if (tenantId is { } selectedTenant)
            query = query.Where(policy => policy.TenantId == selectedTenant);

        var policies = await query
            .OrderBy(policy => policy.Environment)
            .ThenBy(policy => policy.TenantId)
            .ThenByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new McpOperatorPolicyPage(policies.Select(ToPolicy).ToArray());
    }

    public async Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken)
    {
        if (policyId == Guid.Empty)
            throw new ArgumentException("A policy identifier is required.", nameof(policyId));

        var policy = await _db.McpOperatorPolicies.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == policyId, cancellationToken)
            .ConfigureAwait(false);
        return policy is null ? null : ToPolicy(policy);
    }

    public async Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(
        int? tenantId,
        Guid? policyId,
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        if (tenantId is < 0 || policyId == Guid.Empty || agentId == Guid.Empty)
            throw new ArgumentOutOfRangeException("An audit filter must contain a valid tenant, policy, or Agent identifier.");

        var query = _db.McpOperatorPolicyChangeAudits.AsNoTracking().AsQueryable();
        if (tenantId is { } selectedTenant)
            query = query.Where(audit => audit.TenantId == selectedTenant);
        if (policyId is { } selectedPolicy)
            query = query.Where(audit => audit.PolicyId == selectedPolicy);
        if (agentId is { } selectedAgent)
            query = query.Where(audit => audit.AgentId == selectedAgent);

        return await query.OrderByDescending(audit => audit.OccurredAtUtc)
            .ThenByDescending(audit => audit.Id)
            .Take(250)
            .Select(audit => ToChangeAudit(audit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(
        McpOperatorAcceptedAuditFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.TenantId is < 0 || filter.AgentId == Guid.Empty ||
            (filter.Subject is not null && !IsSafeValue(filter.Subject, MaxIdentityLength)) ||
            filter.Limit is <= 0 or > 250)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "Accepted-action audit filters must be bounded and valid.");
        }

        var query = _db.McpOperatorAcceptedAudits.AsNoTracking().AsQueryable();
        if (filter.TenantId is { } tenantId)
            query = query.Where(audit => audit.TenantId == tenantId);
        if (filter.AgentId is { } agentId)
            query = query.Where(audit => audit.AgentId == agentId);
        if (filter.Subject is { } subject)
            query = query.Where(audit => audit.Subject == subject);

        var records = await query
            .OrderByDescending(audit => audit.OccurredAtUtc)
            .ThenByDescending(audit => audit.Id)
            .Take(filter.Limit ?? 100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new McpOperatorAcceptedAuditPage(records.Select(ToAcceptedAudit).ToArray());
    }

    public async Task<McpOperatorPolicy> CreateAsync(
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken)
    {
        await ValidateDraftAsync(draft, actorId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = now,
            CreatedBy = actorId.Trim(),
            Version = 1
        };
        Apply(record, draft);
        _db.McpOperatorPolicies.Add(record);
        AddChangeAudit("policy_created", record.TenantId, record.Id, null, record.Version, record.CreatedBy, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToPolicy(record);
    }

    public async Task ValidateDraftAsync(
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken)
    {
        ValidateDraft(draft, actorId);
        await EnsureExactTargetExistsAsync(draft.TargetSelector, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpOperatorPolicy> ReplaceAsync(
        Guid policyId,
        long expectedVersion,
        McpOperatorPolicyDraft draft,
        string actorId,
        CancellationToken cancellationToken)
    {
        await ValidateDraftAsync(draft, actorId, cancellationToken).ConfigureAwait(false);
        if (expectedVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));

        var record = await _db.McpOperatorPolicies.SingleOrDefaultAsync(policy => policy.Id == policyId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The operator policy was not found.");
        if (record.LifecycleState == McpOperatorPolicyLifecycleState.Revoked)
            throw new InvalidOperationException("A revoked operator policy cannot be replaced; create a new reviewed policy instead.");
        if (record.LifecycleState != McpOperatorPolicyLifecycleState.Active || record.DisabledAtUtc is not null)
            throw new InvalidOperationException("A disabled operator policy cannot be replaced; create a new reviewed policy instead.");
        if (record.Version != expectedVersion)
            throw new DbUpdateConcurrencyException("The operator policy changed before replacement.");

        Apply(record, draft);
        record.Version++;
        AddChangeAudit("policy_replaced", record.TenantId, record.Id, null, record.Version, actorId.Trim(), DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToPolicy(record);
    }

    public async Task<McpOperatorPolicy?> DisableAsync(
        Guid policyId,
        long expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeValue(actorId, MaxIdentityLength) || expectedVersion <= 0)
            throw new ArgumentException("A trusted administrator identity and positive expected version are required.");

        var record = await _db.McpOperatorPolicies.SingleOrDefaultAsync(policy => policy.Id == policyId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.Version != expectedVersion)
            throw new DbUpdateConcurrencyException("The operator policy changed before disable.");
        if (record.LifecycleState == McpOperatorPolicyLifecycleState.Revoked)
            throw new InvalidOperationException("A revoked operator policy cannot be disabled.");
        if (record.LifecycleState == McpOperatorPolicyLifecycleState.Active)
        {
            var now = DateTimeOffset.UtcNow;
            record.LifecycleState = McpOperatorPolicyLifecycleState.Disabled;
            record.DisabledAtUtc = now;
            record.DisabledBy = actorId.Trim();
            record.Version++;
            AddChangeAudit("policy_disabled", record.TenantId, record.Id, null, record.Version, record.DisabledBy, now);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return ToPolicy(record);
    }

    public async Task<McpOperatorPolicy?> RevokeAsync(
        Guid policyId,
        long expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeValue(actorId, MaxIdentityLength) || expectedVersion <= 0)
            throw new ArgumentException("A trusted administrator identity and positive expected version are required.");

        var record = await _db.McpOperatorPolicies.SingleOrDefaultAsync(policy => policy.Id == policyId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.Version != expectedVersion)
            throw new DbUpdateConcurrencyException("The operator policy changed before revocation.");
        if (record.LifecycleState == McpOperatorPolicyLifecycleState.Revoked)
            return ToPolicy(record);

        var now = DateTimeOffset.UtcNow;
        record.LifecycleState = McpOperatorPolicyLifecycleState.Revoked;
        record.DisabledAtUtc ??= now;
        record.DisabledBy ??= actorId.Trim();
        record.Version++;
        AddChangeAudit("policy_revoked", record.TenantId, record.Id, null, record.Version, actorId.Trim(), now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToPolicy(record);
    }

    public async Task<McpOperatorTargetProfile?> GetTargetProfileAsync(
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || agentId == Guid.Empty)
            throw new ArgumentException("A tenant and AgentId are required.");

        var profile = await _db.McpOperatorTargetProfiles.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.AgentId == agentId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null ? null : ToProfile(profile);
    }

    public async Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(
        int tenantId,
        Guid agentId,
        McpOperatorTargetClassification classification,
        IReadOnlyCollection<string> tags,
        long? expectedVersion,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || agentId == Guid.Empty || !Enum.IsDefined(classification) ||
            !IsSafeValue(actorId, MaxIdentityLength))
            throw new ArgumentException("A persisted target, known classification, and trusted administrator identity are required.");
        var normalizedTags = NormalizeTags(tags);
        var agentExists = await _db.Agents.AsNoTracking().AnyAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == agentId,
            cancellationToken).ConfigureAwait(false);
        if (!agentExists)
            throw new KeyNotFoundException("The persisted Agent target was not found for this tenant.");

        var profile = await _db.McpOperatorTargetProfiles.SingleOrDefaultAsync(
            candidate => candidate.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            if (expectedVersion is not null)
                throw new DbUpdateConcurrencyException("The target profile does not yet exist.");
            profile = new McpOperatorTargetProfileRecord
            {
                AgentId = agentId,
                TenantId = tenantId,
                Version = 1
            };
            _db.McpOperatorTargetProfiles.Add(profile);
        }
        else
        {
            if (profile.TenantId != tenantId)
                throw new InvalidOperationException("An Agent target profile cannot cross tenant ownership.");
            if (expectedVersion is null || profile.Version != expectedVersion)
                throw new DbUpdateConcurrencyException("The target profile changed before update.");
            profile.Version++;
        }

        profile.Classification = classification;
        profile.TagsJson = SerializeValues(normalizedTags);
        var now = DateTimeOffset.UtcNow;
        profile.UpdatedAtUtc = now;
        profile.UpdatedBy = actorId.Trim();
        AddChangeAudit("target_profile_upserted", profile.TenantId, null, profile.AgentId, profile.Version, profile.UpdatedBy, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToProfile(profile);
    }

    private async Task EnsureExactTargetExistsAsync(McpOperatorTargetSelector target, CancellationToken cancellationToken)
    {
        if (target.Kind != McpOperatorTargetSelectorKind.ExactAgent)
            return;
        if (target.AgentId is not { } agentId || agentId == Guid.Empty)
            throw new ArgumentException("An exact target selector requires an AgentId.");
        var exists = await _db.Agents.AsNoTracking().AnyAsync(
            candidate => candidate.TenantId == target.TenantId && candidate.Id == agentId,
            cancellationToken).ConfigureAwait(false);
        if (!exists)
            throw new KeyNotFoundException("The persisted Agent target was not found for this tenant.");
    }

    private static void Apply(McpOperatorPolicyRecord record, McpOperatorPolicyDraft draft)
    {
        record.Name = draft.Name.Trim();
        record.Environment = draft.Environment;
        record.Effect = draft.Effect;
        record.Priority = draft.Priority;
        record.PrincipalSelectorKind = draft.PrincipalSelector.Kind;
        record.PrincipalSelectorValue = draft.PrincipalSelector.Value.Trim();
        record.TargetSelectorKind = draft.TargetSelector.Kind;
        record.TenantId = draft.TargetSelector.TenantId;
        record.AgentId = draft.TargetSelector.AgentId;
        record.ClientTag = draft.TargetSelector.ClientTag?.Trim();
        record.TargetClassification = draft.TargetClassification;
        record.OperationFamily = draft.OperationFamily;
        record.Operation = string.IsNullOrWhiteSpace(draft.Operation) ? null : draft.Operation.Trim();
        record.ConstraintsJson = JsonSerializer.Serialize(draft.Constraints, McpOperatorJsonContext.Default.McpOperatorConstraints);
        record.ExpiresAtUtc = draft.ExpiresAtUtc;
        record.ReviewByUtc = draft.ReviewByUtc;
        record.AuditReference = string.IsNullOrWhiteSpace(draft.AuditReference) ? null : draft.AuditReference.Trim();
    }

    private static McpOperatorPolicy ToPolicy(McpOperatorPolicyRecord record) => new(
        record.Id,
        record.Name,
        record.Environment,
        record.Effect,
        record.Priority,
        new McpOperatorPrincipalSelector(record.PrincipalSelectorKind, record.PrincipalSelectorValue),
        new McpOperatorTargetSelector(record.TargetSelectorKind, record.TenantId, record.AgentId, record.ClientTag),
        record.TargetClassification,
        record.OperationFamily,
        record.Operation,
        DeserializeConstraints(record.ConstraintsJson),
        record.CreatedAtUtc,
        record.CreatedBy,
        record.ExpiresAtUtc,
        record.ReviewByUtc,
        record.DisabledAtUtc,
        record.DisabledBy,
        record.Version,
        record.AuditReference)
    {
        LifecycleState = record.LifecycleState
    };

    private static McpOperatorTargetProfile ToProfile(McpOperatorTargetProfileRecord record) => new(
        record.AgentId,
        record.TenantId,
        record.Classification,
        DeserializeValues(record.TagsJson),
        record.UpdatedAtUtc,
        record.UpdatedBy,
        record.Version);

    private static McpOperatorPolicyChangeAudit ToChangeAudit(McpOperatorPolicyChangeAuditRecord record) => new(
        record.Id,
        record.Action,
        record.PolicyId,
        record.AgentId,
        record.TenantId,
        record.ActorId,
        record.Version,
        record.OccurredAtUtc);

    private static McpOperatorAcceptedAudit ToAcceptedAudit(McpOperatorAcceptedAuditRecord record) => new(
        record.Id,
        record.PolicyId,
        record.Environment,
        record.ServicePrincipal,
        record.Subject,
        record.ClientId,
        record.AuthorizedParty,
        DeserializeValues(record.GroupsJson),
        DeserializeValues(record.RolesJson),
        DeserializeValues(record.ScopesJson),
        record.McpResource,
        record.McpInstance,
        record.Tool,
        record.TenantId,
        record.AgentId,
        record.OperationFamily,
        record.Operation,
        record.CorrelationId,
        record.RequestId,
        record.OccurredAtUtc);

    private void AddChangeAudit(
        string action,
        int tenantId,
        Guid? policyId,
        Guid? agentId,
        long version,
        string actorId,
        DateTimeOffset occurredAtUtc) =>
        _db.McpOperatorPolicyChangeAudits.Add(new McpOperatorPolicyChangeAuditRecord
        {
            Id = Guid.NewGuid(),
            Action = action,
            PolicyId = policyId,
            AgentId = agentId,
            TenantId = tenantId,
            ActorId = actorId,
            Version = version,
            OccurredAtUtc = occurredAtUtc
        });

    private static void ValidateDraft(McpOperatorPolicyDraft draft, string actorId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IsSafeValue(actorId, MaxIdentityLength) || !IsBoundedText(draft.Name, 160) ||
            !Enum.IsDefined(draft.Environment) || !Enum.IsDefined(draft.Effect) ||
            draft.Priority is < -10_000 or > 10_000 || !Enum.IsDefined(draft.PrincipalSelector.Kind) ||
            !IsSafeValue(draft.PrincipalSelector.Value, MaxIdentityLength) ||
            !Enum.IsDefined(draft.TargetSelector.Kind) ||
            (draft.TargetSelector.Kind is McpOperatorTargetSelectorKind.ControlPlane or McpOperatorTargetSelectorKind.DevelopmentEnvironment
                ? draft.TargetSelector.TenantId != 0
                : draft.TargetSelector.TenantId <= 0) ||
            draft.OperationFamily == McpOperatorOperationFamily.None ||
            (draft.OperationFamily & ~KnownOperationFamilies) != 0 ||
            (draft.Operation is not null && !IsSafeValue(draft.Operation, MaxIdentityLength)) ||
            (draft.AuditReference is not null && !IsBoundedText(draft.AuditReference, 512)))
            throw new ArgumentException("The operator policy has invalid bounded selectors, operation, or audit metadata.");

        if (draft.TargetSelector.Kind == McpOperatorTargetSelectorKind.DevelopmentEnvironment &&
            (draft.Environment != McpOperatorEnvironment.Development || draft.TargetClassification is not null))
            throw new ArgumentException("Environment-wide feature testing policies are only valid in Development without a classification selector.");
        ValidateTargetSelector(draft.TargetSelector);
        if (draft.TargetClassification is { } classification && !Enum.IsDefined(classification))
            throw new ArgumentException("The policy target classification is unknown.");
        if (draft.TargetSelector.Kind == McpOperatorTargetSelectorKind.ControlPlane && draft.TargetClassification is not null)
            throw new ArgumentException("A control-plane policy cannot select an agent target classification.");
        if (draft.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new ArgumentException("The policy expiry must be in the future.");
        if (draft.ReviewByUtc is { } review && review <= DateTimeOffset.UtcNow)
            throw new ArgumentException("The policy review date must be in the future.");
        if (draft.Effect == McpOperatorPolicyEffect.Allow &&
            draft.PrincipalSelector.Kind == McpOperatorPrincipalSelectorKind.OAuthSubject &&
            string.Equals(draft.PrincipalSelector.Value, actorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Policy administrators cannot grant themselves an allow policy through this API.");

        ValidateConstraints(draft.Constraints);
    }

    private static void ValidateTargetSelector(McpOperatorTargetSelector selector)
    {
        switch (selector.Kind)
        {
            case McpOperatorTargetSelectorKind.ExactAgent when selector.AgentId is { } agentId && agentId != Guid.Empty && selector.ClientTag is null:
            case McpOperatorTargetSelectorKind.Tenant when selector.AgentId is null && selector.ClientTag is null:
            case McpOperatorTargetSelectorKind.ClientTag when selector.AgentId is null && IsSafeValue(selector.ClientTag, 128):
            case McpOperatorTargetSelectorKind.ControlPlane when selector.TenantId == 0 && selector.AgentId is null && selector.ClientTag is null:
            case McpOperatorTargetSelectorKind.DevelopmentEnvironment when selector.TenantId == 0 && selector.AgentId is null && selector.ClientTag is null:
                return;
            default:
                throw new ArgumentException("The policy target selector is malformed.");
        }
    }

    private static void ValidateConstraints(McpOperatorConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ValidatePaths(constraints.ReadRoots, nameof(constraints.ReadRoots));
        ValidatePaths(constraints.WriteRoots, nameof(constraints.WriteRoots));
        ValidateValues(constraints.AllowedShells, nameof(constraints.AllowedShells), 64);
        ValidatePaths(constraints.WorkingDirectories, nameof(constraints.WorkingDirectories));
        ValidatePositive(constraints.MaxCommandDurationSeconds, nameof(constraints.MaxCommandDurationSeconds));
        ValidatePositive(constraints.MaxTerminalIdleSeconds, nameof(constraints.MaxTerminalIdleSeconds));
        ValidatePositive(constraints.MaxTerminalLifetimeSeconds, nameof(constraints.MaxTerminalLifetimeSeconds));
        ValidatePositive(constraints.MaxConcurrentTerminalSessions, nameof(constraints.MaxConcurrentTerminalSessions));
        ValidatePositive(constraints.MaxConcurrentCommands, nameof(constraints.MaxConcurrentCommands));
        ValidatePositive(constraints.MaxOutputBytes, nameof(constraints.MaxOutputBytes));
        ValidatePositive(constraints.MaxArtifactBytes, nameof(constraints.MaxArtifactBytes));
        ValidatePositive(constraints.MaxScriptBytes, nameof(constraints.MaxScriptBytes));
        ValidatePositive(constraints.MaxJobTargetCount, nameof(constraints.MaxJobTargetCount));
        ValidatePositive(constraints.MaxTaskTargetCount, nameof(constraints.MaxTaskTargetCount));
        ValidatePositive(constraints.MaxFanOut, nameof(constraints.MaxFanOut));
        ValidatePositive(constraints.MaxOnboardingCodeLifetimeSeconds, nameof(constraints.MaxOnboardingCodeLifetimeSeconds));
        ValidatePositive(constraints.MaxOnboardingCodeUses, nameof(constraints.MaxOnboardingCodeUses));
        if (constraints.AllowedTargetClassifications?.Any(classification => !Enum.IsDefined(classification)) == true ||
            constraints.RequiredConfirmationClass is { } confirmation && !Enum.IsDefined(confirmation))
            throw new ArgumentException("The policy constraints contain an unknown classification or confirmation class.");
    }

    private static void ValidatePositive(int? value, string name)
    {
        if (value is <= 0)
            throw new ArgumentException($"{name} must be positive when supplied.");
    }

    private static void ValidatePaths(IReadOnlyList<string>? values, string name)
        => ValidateValues(values, name, 4_096);

    private static void ValidateValues(IReadOnlyList<string>? values, string name, int maximumLength)
    {
        if (values is null)
            return;
        if (values.Count > 64 || values.Any(value => !IsBoundedText(value, maximumLength)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException($"{name} must contain at most 64 distinct bounded values.");
    }

    private static string[] NormalizeTags(IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > 32 || tags.Any(tag => !IsSafeValue(tag, 128)))
            throw new ArgumentException("Target tags must contain at most 32 bounded identifiers.", nameof(tags));
        return tags.Select(tag => tag.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static McpOperatorConstraints DeserializeConstraints(string json)
        => JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.McpOperatorConstraints)
           ?? throw new InvalidOperationException("The persisted operator policy constraints are invalid.");

    private static string SerializeValues(IReadOnlyCollection<string> values)
        => JsonSerializer.Serialize(values.ToArray(), McpOperatorJsonContext.Default.StringArray);

    private static IReadOnlyList<string> DeserializeValues(string json)
        => JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.StringArray)
           ?? throw new InvalidOperationException("The persisted operator target tags are invalid.");

    private static bool IsSafeValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private const McpOperatorOperationFamily KnownOperationFamilies =
        McpOperatorOperationFamily.Observability |
        McpOperatorOperationFamily.FileRead |
        McpOperatorOperationFamily.FileWrite |
        McpOperatorOperationFamily.TerminalRead |
        McpOperatorOperationFamily.TerminalExecute |
        McpOperatorOperationFamily.ScriptsRead |
        McpOperatorOperationFamily.ScriptsWrite |
        McpOperatorOperationFamily.AutomationRead |
        McpOperatorOperationFamily.AutomationWrite |
        McpOperatorOperationFamily.JobExecution |
        McpOperatorOperationFamily.TaskExecution |
        McpOperatorOperationFamily.Requests |
        McpOperatorOperationFamily.Onboarding |
        McpOperatorOperationFamily.ClientAdministration |
        McpOperatorOperationFamily.TenantAdministration |
        McpOperatorOperationFamily.Notifications |
        McpOperatorOperationFamily.Events |
        McpOperatorOperationFamily.Connectivity |
        McpOperatorOperationFamily.PolicyAdministration;
}
