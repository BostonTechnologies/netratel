using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Security;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Durable V2 request boundary. It links the legacy domain row to immutable
/// caller ownership and policy evidence without admitting legacy unbounded
/// request reads or writes through the operator surface.
/// </summary>
public sealed class McpOperatorRequestStore(OrchestratorDbContext db) : IMcpOperatorRequestStore
{
    private const int MaximumSummaryBytes = 4 * 1024;
    private const int MaximumResultBytes = 48 * 1024;
    private const int MaximumListSize = 100;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorRequestLease> CreateOrGetAsync(McpOperatorRequestCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreate(request);
        var existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return SameAdmission(existing, request) ? ToLease(existing) : throw new McpOperatorRequestLimitException("idempotency_conflict");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SameAdmission(existing, request) ? ToLease(existing) : throw new McpOperatorRequestLimitException("idempotency_conflict");
            }

            var access = request.Decision.Request;
            var job = await _db.McpOperatorJobs.SingleOrDefaultAsync(candidate =>
                candidate.JobId == request.JobId && candidate.TenantId == access.TenantId && candidate.AgentId == access.AgentId &&
                candidate.Subject == access.Principal.Subject && candidate.ClientId == ClientId(access.Principal) &&
                candidate.McpResource == access.McpResource && candidate.McpInstance == access.McpInstance &&
                candidate.TargetSetDigest == access.TargetSetDigest && candidate.DeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false);
            if (job is null)
                throw new McpOperatorRequestLimitException("request_job_not_owned");

            var summary = BoundText(request.Summary, MaximumSummaryBytes) ?? throw new McpOperatorRequestLimitException("request_invalid");
            var domain = new RequestRecord
            {
                SourceSystem = "mcp-operator-v2",
                TargetClientIdentity = $"agent:{access.AgentId!.Value:D}",
                TargetTenantId = access.TenantId,
                TargetAgentId = access.AgentId,
                JobDefinitionId = request.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Status = "New",
                ResultMessage = summary,
                Logs = [$"[{request.OccurredAtUtc:O}] Production operator request created."],
                CreatedAtUtc = request.OccurredAtUtc,
                UpdatedAtUtc = request.OccurredAtUtc
            };
            _db.Requests.Add(domain);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var record = new McpOperatorRequestRecord
            {
                Id = Guid.NewGuid(),
                RequestId = domain.Id,
                JobId = request.JobId,
                TenantId = access.TenantId,
                AgentId = access.AgentId.Value,
                Subject = access.Principal.Subject,
                ClientId = ClientId(access.Principal),
                McpResource = access.McpResource!,
                McpInstance = access.McpInstance!,
                PolicyId = request.Decision.MatchingPolicyIds.Single(),
                PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
                TargetSetDigest = access.TargetSetDigest!,
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                IdempotencyId = request.IdempotencyId,
                CorrelationId = request.CorrelationId,
                Summary = summary,
                State = "Pending",
                CreatedAtUtc = request.OccurredAtUtc,
                UpdatedAtUtc = request.OccurredAtUtc,
                Version = 1
            };
            _db.McpOperatorRequests.Add(record);
            _db.McpOperatorRequestAudits.Add(Audit(record, "create", request.AcceptedAudit.AuditId, request.OccurredAtUtc));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToLease(record);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            existing = await FindByIdempotencyAsync(request.IdempotencyId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && SameAdmission(existing, request))
                return ToLease(existing);
            throw;
        }
    }

    public async Task<McpOperatorRequestLease?> GetOwnedAsync(int requestId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken)
    {
        if (requestId <= 0 || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;
        var record = await FindOwnedAsync(requestId, tenantId, agentId, principal, mcpResource, mcpInstance, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<IReadOnlyList<McpOperatorRequestLease>> ListOwnedAsync(int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, string? state, long? jobId, DateTimeOffset? sinceUtc, int limit, CancellationToken cancellationToken)
    {
        if (!IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return [];
        limit = Math.Clamp(limit, 1, MaximumListSize);
        var query = _db.McpOperatorRequests.AsNoTracking().Where(candidate =>
            candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.Subject == principal.Subject &&
            candidate.ClientId == ClientId(principal) && candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance);
        if (!string.IsNullOrWhiteSpace(state)) query = query.Where(candidate => candidate.State == state);
        if (jobId is > 0) query = query.Where(candidate => candidate.JobId == jobId.Value);
        if (sinceUtc.HasValue) query = query.Where(candidate => candidate.CreatedAtUtc >= sinceUtc.Value);
        var rows = await query.OrderByDescending(candidate => candidate.UpdatedAtUtc).ThenByDescending(candidate => candidate.RequestId)
            .Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToLease).ToArray();
    }

    public async Task<McpOperatorRequestLease?> MutateAsync(McpOperatorRequestMutation request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMutation(request);
        var record = await _db.McpOperatorRequests.SingleOrDefaultAsync(candidate => candidate.RequestId == request.RequestId, cancellationToken).ConfigureAwait(false);
        if (record is null || !MatchesDecision(record, request.Decision))
            return null;
        if (record.Version != request.ExpectedVersion)
            throw new McpOperatorRequestLimitException("request_version_conflict");
        if (!CanTransition(record.State, request.Action))
            throw new McpOperatorRequestLimitException("request_terminal_conflict");

        var domain = await _db.Requests.SingleOrDefaultAsync(candidate => candidate.Id == record.RequestId, cancellationToken).ConfigureAwait(false);
        if (domain is null)
            throw new McpOperatorRequestLimitException("request_not_found");
        ApplyMutation(record, domain, request);
        _db.McpOperatorRequestAudits.Add(Audit(record, request.Action, request.AcceptedAudit.AuditId, request.OccurredAtUtc));
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new McpOperatorRequestLimitException("request_version_conflict");
        }
        return ToLease(record);
    }

    private void ApplyMutation(McpOperatorRequestRecord record, RequestRecord domain, McpOperatorRequestMutation request)
    {
        var summary = BoundText(request.Summary, MaximumSummaryBytes);
        var result = BoundText(request.ResultSummary, MaximumResultBytes);
        switch (request.Action)
        {
            case "update":
                if (summary is null) throw new McpOperatorRequestLimitException("request_invalid");
                record.Summary = summary;
                domain.ResultMessage = summary;
                break;
            case "claim":
                if (string.IsNullOrWhiteSpace(request.ClaimReference)) throw new McpOperatorRequestLimitException("request_invalid");
                record.State = "Claimed";
                record.ClaimReferenceHash = Hash(request.ClaimReference);
                domain.ExecutionId = $"operator:{record.ClaimReferenceHash}";
                domain.Status = "Processing";
                break;
            case "complete":
                if (result is null) throw new McpOperatorRequestLimitException("request_invalid");
                record.State = "Completed";
                record.ResultSummary = result;
                record.CompletedAtUtc = request.OccurredAtUtc;
                domain.Status = "Success";
                domain.ResultData = result;
                break;
            case "fail":
                if (result is null) throw new McpOperatorRequestLimitException("request_invalid");
                record.State = "Failed";
                record.ResultSummary = result;
                record.CompletedAtUtc = request.OccurredAtUtc;
                domain.Status = "Failed";
                domain.ResultData = result;
                break;
            case "cancel":
                record.State = "Cancelled";
                record.ResultSummary = result ?? "Cancellation requested by the authenticated operator.";
                record.CompletedAtUtc = request.OccurredAtUtc;
                domain.Status = "Cancelled";
                domain.ResultData = record.ResultSummary;
                break;
            default:
                throw new McpOperatorRequestLimitException("request_invalid");
        }

        record.PolicyId = request.Decision.MatchingPolicyIds.Single();
        record.PolicyVersion = request.Decision.SelectedPolicyVersion!.Value;
        record.TargetSetDigest = request.Decision.Request.TargetSetDigest!;
        record.AcceptedAuditId = request.AcceptedAudit.AuditId;
        record.UpdatedAtUtc = request.OccurredAtUtc;
        record.Version++;
        domain.Logs.Add($"[{request.OccurredAtUtc:O}] Production operator request {request.Action}.");
        domain.UpdatedAtUtc = request.OccurredAtUtc;
    }

    private Task<McpOperatorRequestRecord?> FindByIdempotencyAsync(Guid idempotencyId, CancellationToken cancellationToken) =>
        _db.McpOperatorRequests.SingleOrDefaultAsync(candidate => candidate.IdempotencyId == idempotencyId, cancellationToken);

    private Task<McpOperatorRequestRecord?> FindOwnedAsync(int requestId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken) =>
        _db.McpOperatorRequests.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.RequestId == requestId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance,
            cancellationToken);

    private static void ValidateCreate(McpOperatorRequestCreateRequest request)
    {
        if (request.JobId <= 0 || request.IdempotencyId == Guid.Empty || BoundText(request.Summary, MaximumSummaryBytes) is null ||
            !IsCorrelationId(request.CorrelationId) || !IsValidDecision(request.Decision) || !AcceptedAuditMatches(request.AcceptedAudit, request.Decision))
            throw new McpOperatorRequestLimitException("request_invalid");
    }

    private static void ValidateMutation(McpOperatorRequestMutation request)
    {
        if (request.RequestId <= 0 || request.ExpectedVersion <= 0 || request.Action is not ("update" or "claim" or "complete" or "fail" or "cancel") ||
            !IsValidDecision(request.Decision) || !AcceptedAuditMatches(request.AcceptedAudit, request.Decision))
            throw new McpOperatorRequestLimitException("request_invalid");
    }

    private static bool IsValidDecision(McpOperatorDecision decision)
    {
        if (!decision.IsAllowed || decision.Request.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) || decision.Request.TenantId <= 0 ||
            decision.Request.AgentId is not { } agent || agent == Guid.Empty)
            return false;
        return decision.MatchingPolicyIds.Count == 1 && decision.SelectedPolicyVersion is not null &&
               !string.IsNullOrWhiteSpace(decision.Request.TargetSetDigest) && !string.IsNullOrWhiteSpace(decision.Request.McpResource) &&
               !string.IsNullOrWhiteSpace(decision.Request.McpInstance) && !string.IsNullOrWhiteSpace(decision.Request.Principal.Subject);
    }

    private static bool AcceptedAuditMatches(McpOperatorAcceptedAudit audit, McpOperatorDecision decision) =>
        audit.PolicyId == decision.MatchingPolicyIds.Single() && audit.TenantId == decision.Request.TenantId &&
        audit.AgentId == decision.Request.AgentId && string.Equals(audit.Subject, decision.Request.Principal.Subject, StringComparison.Ordinal);

    private static bool SameAdmission(McpOperatorRequestRecord record, McpOperatorRequestCreateRequest request) =>
        record.JobId == request.JobId && record.TenantId == request.Decision.Request.TenantId && record.AgentId == request.Decision.Request.AgentId &&
        record.Subject == request.Decision.Request.Principal.Subject && record.ClientId == ClientId(request.Decision.Request.Principal) &&
        record.McpResource == request.Decision.Request.McpResource && record.McpInstance == request.Decision.Request.McpInstance &&
        string.Equals(record.Summary, BoundText(request.Summary, MaximumSummaryBytes), StringComparison.Ordinal);

    private static bool MatchesDecision(McpOperatorRequestRecord record, McpOperatorDecision decision) =>
        record.TenantId == decision.Request.TenantId && record.AgentId == decision.Request.AgentId &&
        record.Subject == decision.Request.Principal.Subject && record.ClientId == ClientId(decision.Request.Principal) &&
        record.McpResource == decision.Request.McpResource && record.McpInstance == decision.Request.McpInstance &&
        record.TargetSetDigest == decision.Request.TargetSetDigest;

    private static bool CanTransition(string state, string action) => action switch
    {
        "update" => state is "Pending" or "Claimed",
        "claim" => state == "Pending",
        "complete" or "fail" or "cancel" => state is "Pending" or "Claimed",
        _ => false
    };

    private static McpOperatorRequestAuditRecord Audit(McpOperatorRequestRecord record, string action, Guid acceptedAuditId, DateTimeOffset occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        RequestRecordId = record.Id,
        Action = action,
        AcceptedAuditId = acceptedAuditId,
        OccurredAtUtc = occurredAtUtc
    };

    private static McpOperatorRequestLease ToLease(McpOperatorRequestRecord record) => new(
        record.RequestId, record.JobId, record.TenantId, record.AgentId, record.Subject, record.ClientId, record.McpResource, record.McpInstance,
        record.PolicyId, record.PolicyVersion, record.TargetSetDigest, record.State, record.Summary, record.ResultSummary, record.ClaimReferenceHash,
        record.CorrelationId, record.CreatedAtUtc, record.UpdatedAtUtc, record.CompletedAtUtc, record.Version);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;
    private static bool IsOwnerInputValid(int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance) =>
        tenantId > 0 && agentId != Guid.Empty && !string.IsNullOrWhiteSpace(principal.Subject) && !string.IsNullOrWhiteSpace(mcpResource) && !string.IsNullOrWhiteSpace(mcpInstance);
    private static bool IsCorrelationId(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => !char.IsControl(character));
    private static string? BoundText(string? value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bounded = OperatorOutputRedactor.Redact(value.Trim());
        while (Encoding.UTF8.GetByteCount(bounded) > maximumBytes)
            bounded = bounded[..Math.Max(1, bounded.Length / 2)];
        return bounded;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
}
