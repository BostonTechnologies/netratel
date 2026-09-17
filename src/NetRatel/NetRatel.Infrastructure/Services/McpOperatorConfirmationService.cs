using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Persists the two-stage mutation admission contract. A plan contains only
/// identity, policy, target-set, and canonical payload hashes; it never stores
/// a command, file body, script body, or operation result content.
/// </summary>
public sealed class McpOperatorConfirmationService(OrchestratorDbContext db) : IMcpOperatorConfirmationService
{
    private const int MinimumPlanLifetimeSeconds = 30;
    private const int MaximumPlanLifetimeSeconds = 600;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorConfirmationPlan> CreatePlanAsync(
        McpOperatorConfirmationPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreview(request);

        var now = DateTimeOffset.UtcNow;
        var expiresAtUtc = request.ExpiresAtUtc ?? now.AddMinutes(5);
        var lifetimeSeconds = (expiresAtUtc - now).TotalSeconds;
        if (lifetimeSeconds is < MinimumPlanLifetimeSeconds or > MaximumPlanLifetimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(request), "Confirmation plans must expire between 30 seconds and 10 minutes after preview.");

        var planToken = NewOpaqueToken();
        var idempotencyKey = NewOpaqueToken();
        var access = request.Decision.Request;
        var record = new McpOperatorConfirmationPlanRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(planToken),
            Environment = access.Environment,
            Subject = access.Principal.Subject,
            ClientId = ClientId(access),
            McpResource = access.McpResource,
            McpInstance = access.McpInstance,
            TenantId = access.TenantId,
            AgentId = access.AgentId,
            TargetSetDigest = access.TargetSetDigest!,
            PolicyId = request.Decision.MatchingPolicyIds.Single(),
            PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
            OperationFamily = access.OperationFamily,
            Operation = access.Operation,
            ConfirmationClass = access.ConfirmationClass,
            PayloadHash = request.PayloadHash,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            Version = 1
        };

        _db.McpOperatorConfirmationPlans.Add(record);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new McpOperatorConfirmationPlan(
            planToken,
            idempotencyKey,
            record.ExpiresAtUtc,
            record.ConfirmationClass,
            record.PayloadHash,
            access.TargetSetDigest);
    }

    public async Task<McpOperatorConfirmationAdmission> ConfirmAsync(
        McpOperatorConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsOpaqueToken(request.PlanToken) || !IsOpaqueToken(request.IdempotencyKey) || !IsSha256(request.PayloadHash))
            return McpOperatorConfirmationAdmission.Denied("confirmation_plan_invalid");
        if (!IsUsableDecision(request.CurrentDecision))
            return McpOperatorConfirmationAdmission.Denied(request.CurrentDecision.FailureCode ?? "target_policy_missing");

        var plan = await _db.McpOperatorConfirmationPlans.SingleOrDefaultAsync(
            candidate => candidate.TokenHash == Hash(request.PlanToken), cancellationToken).ConfigureAwait(false);
        if (plan is null)
            return McpOperatorConfirmationAdmission.Denied("confirmation_plan_invalid");

        var access = request.CurrentDecision.Request;
        if (!PlanMatches(plan, request.CurrentDecision, request.PayloadHash, request.IdempotencyKey))
            return McpOperatorConfirmationAdmission.Denied("confirmation_plan_stale");
        if (plan.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return McpOperatorConfirmationAdmission.Denied("confirmation_plan_expired");

        var existing = await FindIdempotencyAsync(plan, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return AdmissionFor(existing, plan);

        if (plan.ConsumedAtUtc is not null)
            return McpOperatorConfirmationAdmission.Denied("confirmation_plan_stale");

        var idempotency = new McpOperatorIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Environment = access.Environment,
            Subject = access.Principal.Subject,
            ClientId = ClientId(access),
            TenantId = access.TenantId,
            AgentId = access.AgentId,
            TargetSetDigest = access.TargetSetDigest!,
            PolicyId = request.CurrentDecision.MatchingPolicyIds.Single(),
            PolicyVersion = request.CurrentDecision.SelectedPolicyVersion!.Value,
            OperationFamily = access.OperationFamily,
            Operation = access.Operation,
            IdempotencyKey = request.IdempotencyKey,
            PayloadHash = request.PayloadHash,
            Outcome = McpOperatorIdempotencyOutcome.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1
        };
        plan.ConsumedAtUtc = idempotency.CreatedAtUtc;
        plan.ConsumedIdempotencyId = idempotency.Id;
        plan.Version++;
        _db.McpOperatorIdempotencyRecords.Add(idempotency);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new McpOperatorConfirmationAdmission(true, false, null, idempotency.Id, idempotency.Outcome, null);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var concurrent = await FindIdempotencyAsync(plan, cancellationToken).ConfigureAwait(false);
            if (concurrent is null)
                throw;
            return AdmissionFor(concurrent, plan);
        }
    }

    public async Task CompleteAsync(
        Guid idempotencyId,
        McpOperatorIdempotencyOutcome outcome,
        string? resultReference,
        CancellationToken cancellationToken)
    {
        if (idempotencyId == Guid.Empty || outcome == McpOperatorIdempotencyOutcome.Pending ||
            !IsSafeResultReference(resultReference))
            throw new ArgumentException("A completed operator dispatch requires an identifier, terminal outcome, and an optional bounded result reference.");

        var record = await _db.McpOperatorIdempotencyRecords.SingleOrDefaultAsync(
            candidate => candidate.Id == idempotencyId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The operator idempotency admission was not found.");
        if (record.Outcome != McpOperatorIdempotencyOutcome.Pending)
        {
            if (record.Outcome != outcome || !string.Equals(record.ResultReference, resultReference, StringComparison.Ordinal))
                throw new InvalidOperationException("A completed operator dispatch cannot be overwritten by a conflicting result.");
            return;
        }

        record.Outcome = outcome;
        record.ResultReference = resultReference;
        record.CompletedAtUtc = DateTimeOffset.UtcNow;
        record.Version++;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpOperatorIdempotencyRecord?> FindIdempotencyAsync(
        McpOperatorConfirmationPlanRecord plan,
        CancellationToken cancellationToken) =>
        await _db.McpOperatorIdempotencyRecords.SingleOrDefaultAsync(candidate =>
            candidate.Environment == plan.Environment &&
            candidate.Subject == plan.Subject &&
            candidate.ClientId == plan.ClientId &&
            candidate.TenantId == plan.TenantId &&
            candidate.OperationFamily == plan.OperationFamily &&
            candidate.Operation == plan.Operation &&
            candidate.IdempotencyKey == plan.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);

    private static McpOperatorConfirmationAdmission AdmissionFor(
        McpOperatorIdempotencyRecord record,
        McpOperatorConfirmationPlanRecord plan)
    {
        if (record.PayloadHash != plan.PayloadHash || record.TargetSetDigest != plan.TargetSetDigest ||
            record.AgentId != plan.AgentId || record.PolicyId != plan.PolicyId || record.PolicyVersion != plan.PolicyVersion ||
            record.OperationFamily != plan.OperationFamily)
            return McpOperatorConfirmationAdmission.Denied("idempotency_conflict");

        return new McpOperatorConfirmationAdmission(
            false,
            true,
            null,
            record.Id,
            record.Outcome,
            record.ResultReference);
    }

    private static bool PlanMatches(
        McpOperatorConfirmationPlanRecord plan,
        McpOperatorDecision decision,
        string payloadHash,
        string idempotencyKey)
    {
        var access = decision.Request;
        return plan.Environment == access.Environment &&
               plan.Subject == access.Principal.Subject &&
               plan.ClientId == ClientId(access) &&
               string.Equals(plan.McpResource, access.McpResource, StringComparison.Ordinal) &&
               string.Equals(plan.McpInstance, access.McpInstance, StringComparison.Ordinal) &&
               plan.TenantId == access.TenantId &&
               plan.AgentId == access.AgentId &&
               plan.TargetSetDigest == access.TargetSetDigest &&
               plan.PolicyId == decision.MatchingPolicyIds.Single() &&
               plan.PolicyVersion == decision.SelectedPolicyVersion &&
               plan.OperationFamily == access.OperationFamily &&
               plan.Operation == access.Operation &&
               plan.ConfirmationClass == access.ConfirmationClass &&
               plan.PayloadHash == payloadHash &&
               plan.IdempotencyKey == idempotencyKey;
    }

    private static void ValidatePreview(McpOperatorConfirmationPlanRequest request)
    {
        if (!IsUsableDecision(request.Decision) || !IsSha256(request.PayloadHash))
            throw new ArgumentException("A preview requires an allowed, target-frozen decision and canonical SHA-256 payload hash.", nameof(request));
    }

    private static bool IsUsableDecision(McpOperatorDecision decision)
    {
        if (!decision.IsAllowed || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null)
            return false;
        var access = decision.Request;
        return access.ConfirmationClass != McpOperatorConfirmationClass.None &&
               IsSha256(access.TargetSetDigest) &&
               IsSafeIdentity(access.Principal.Subject) &&
               (access.Principal.ClientId is null || IsSafeIdentity(access.Principal.ClientId)) &&
               IsSafeIdentity(access.Operation) &&
               (!string.IsNullOrWhiteSpace(access.McpResource) ? IsSafeIdentity(access.McpResource) : true) &&
               (!string.IsNullOrWhiteSpace(access.McpInstance) ? IsSafeIdentity(access.McpInstance) : true);
    }

    private static string ClientId(McpOperatorAccessRequest access) => access.Principal.ClientId ?? string.Empty;

    private static string NewOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsOpaqueToken(string? value) =>
        value is { Length: >= 32 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSafeIdentity(string? value) =>
        value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);

    private static bool IsSafeResultReference(string? value) =>
        value is null || (value.Length <= 512 && !value.Any(char.IsControl));
}
