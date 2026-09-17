using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Durable ownership and frozen-policy lease store for Production terminals.
/// It persists metadata only; PTY input/output stays exclusively in the
/// bounded gateway channels owned by the connected agent.
/// </summary>
public sealed class McpOperatorTerminalSessionStore(OrchestratorDbContext db)
    : IMcpOperatorTerminalSessionStore
{
    private const int MaximumSessionIdLength = 64;
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorTerminalSessionLease> CreateOrGetAsync(
        McpOperatorTerminalSessionCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreate(request);

        var existing = await _db.McpOperatorTerminalSessions.SingleOrDefaultAsync(
            candidate => candidate.IdempotencyId == request.IdempotencyId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return SameAdmission(existing, request) ? ToLease(existing) : throw new InvalidOperationException("The terminal idempotency admission is already bound to another lease.");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            existing = await _db.McpOperatorTerminalSessions.SingleOrDefaultAsync(
                candidate => candidate.IdempotencyId == request.IdempotencyId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SameAdmission(existing, request) ? ToLease(existing) : throw new InvalidOperationException("The terminal idempotency admission is already bound to another lease.");
            }

            var constraints = request.Decision.EffectiveConstraints!;
            var concurrent = await _db.McpOperatorTerminalSessions.CountAsync(candidate =>
                candidate.TenantId == request.Decision.Request.TenantId &&
                candidate.AgentId == request.Decision.Request.AgentId &&
                candidate.Subject == request.Decision.Request.Principal.Subject &&
                candidate.ClientId == ClientId(request.Decision.Request.Principal) &&
                (candidate.State == McpOperatorTerminalSessionState.Opening ||
                 candidate.State == McpOperatorTerminalSessionState.Opened ||
                 candidate.State == McpOperatorTerminalSessionState.Closing),
                cancellationToken).ConfigureAwait(false);
            if (concurrent >= constraints.MaxConcurrentTerminalSessions!.Value)
                throw new McpOperatorTerminalSessionLimitException("terminal_session_limit_reached");

            var record = new McpOperatorTerminalSessionRecord
            {
                Id = Guid.NewGuid(),
                SessionId = request.SessionId,
                TenantId = request.Decision.Request.TenantId,
                AgentId = request.Decision.Request.AgentId!.Value,
                Generation = 1,
                Subject = request.Decision.Request.Principal.Subject,
                ClientId = ClientId(request.Decision.Request.Principal),
                McpResource = request.Decision.Request.McpResource!,
                McpInstance = request.Decision.Request.McpInstance!,
                PolicyId = request.Decision.MatchingPolicyIds.Single(),
                PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
                AcceptedAuditId = request.AcceptedAudit.AuditId,
                IdempotencyId = request.IdempotencyId,
                ShellType = request.ShellType,
                WorkingDirectory = request.WorkingDirectory,
                Columns = request.Columns,
                Rows = request.Rows,
                EffectiveConstraintsJson = JsonSerializer.Serialize(constraints, McpOperatorJsonContext.Default.McpOperatorConstraints),
                State = McpOperatorTerminalSessionState.Opening,
                CreatedAtUtc = request.CreatedAtUtc,
                LastActivityAtUtc = request.CreatedAtUtc,
                IdleExpiresAtUtc = request.IdleExpiresAtUtc,
                ExpiresAtUtc = request.ExpiresAtUtc,
                Version = 1
            };
            _db.McpOperatorTerminalSessions.Add(record);
            AddAudit(record, "lease_created", null, request.CreatedAtUtc);
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
            existing = await _db.McpOperatorTerminalSessions.SingleOrDefaultAsync(
                candidate => candidate.IdempotencyId == request.IdempotencyId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null && SameAdmission(existing, request))
                return ToLease(existing);
            throw;
        }
    }

    public async Task<McpOperatorTerminalSessionLease?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!IsSessionId(sessionId))
            return null;
        var record = await _db.McpOperatorTerminalSessions.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.SessionId == sessionId,
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<McpOperatorTerminalSessionLease?> GetOwnedAsync(
        string sessionId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (!IsSessionId(sessionId) || tenantId <= 0 || agentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(principal.Subject) || string.IsNullOrWhiteSpace(mcpResource) || string.IsNullOrWhiteSpace(mcpInstance))
        {
            return null;
        }

        var record = await _db.McpOperatorTerminalSessions.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.SessionId == sessionId &&
            candidate.TenantId == tenantId &&
            candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject &&
            candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource &&
            candidate.McpInstance == mcpInstance,
            cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToLease(record);
    }

    public async Task<McpOperatorTerminalSessionLease?> TouchAsync(
        string sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await FindMutableAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.State is McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
            return ToLease(record);

        if (record.ExpiresAtUtc <= now || record.IdleExpiresAtUtc <= now)
            return await CloseForExpiryAsync(record, now, cancellationToken).ConfigureAwait(false);
        if (record.State == McpOperatorTerminalSessionState.Closing)
            return ToLease(record);

        var idleDuration = record.IdleExpiresAtUtc - record.LastActivityAtUtc;
        record.LastActivityAtUtc = now;
        record.IdleExpiresAtUtc = Min(now.Add(idleDuration), record.ExpiresAtUtc);
        record.Version++;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(record);
    }

    public async Task<McpOperatorTerminalSessionLease?> RequestCloseAsync(
        string sessionId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await FindMutableAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.State is McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed or McpOperatorTerminalSessionState.Closing)
            return ToLease(record);
        if (!IsReason(reason))
            throw new ArgumentException("A bounded terminal close reason is required.", nameof(reason));

        record.State = McpOperatorTerminalSessionState.Closing;
        record.CloseRequestedAtUtc = now;
        record.CloseReason = reason;
        record.Version++;
        AddAudit(record, "close_requested", reason, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(record);
    }

    public async Task<McpOperatorTerminalSessionLease?> MarkTerminalAsync(
        string sessionId,
        McpOperatorTerminalSessionState state,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state is not McpOperatorTerminalSessionState.Opened and not McpOperatorTerminalSessionState.Closed and not McpOperatorTerminalSessionState.Failed)
            throw new ArgumentOutOfRangeException(nameof(state));
        var record = await FindMutableAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.State is McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
            return record is null ? null : ToLease(record);
        // A close request is irrevocable. A late opened notification from a
        // reconnecting agent must not revive the lease or bypass the policy
        // expiry that already fenced it.
        if (record.State == McpOperatorTerminalSessionState.Closing && state == McpOperatorTerminalSessionState.Opened)
            return ToLease(record);
        // Gateway transport retries can replay a lifecycle frame after the
        // original durable write committed. Keep the transition idempotent at
        // the authority boundary so a duplicate Opened cannot increment the
        // lease version or create a second audit record.
        if (record.State == state)
            return ToLease(record);

        record.State = state;
        record.LastActivityAtUtc = now;
        if (state is McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
        {
            record.ClosedAtUtc = now;
            record.FailureCode = state == McpOperatorTerminalSessionState.Failed ? reason : null;
        }
        record.Version++;
        AddAudit(record, state == McpOperatorTerminalSessionState.Opened ? "opened" : state == McpOperatorTerminalSessionState.Closed ? "closed" : "failed", reason, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(record);
    }

    public async Task<IReadOnlyList<McpOperatorTerminalSessionLease>> ClaimDueClosesAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (maximum is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        var due = await _db.McpOperatorTerminalSessions
            .Where(record =>
                record.State == McpOperatorTerminalSessionState.Closing ||
                ((record.State == McpOperatorTerminalSessionState.Opening || record.State == McpOperatorTerminalSessionState.Opened) &&
                 (record.ExpiresAtUtc <= now || record.IdleExpiresAtUtc <= now)))
            .OrderBy(record => record.CloseRequestedAtUtc ?? record.IdleExpiresAtUtc)
            .Take(maximum)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var record in due.Where(record => record.State != McpOperatorTerminalSessionState.Closing))
        {
            record.State = McpOperatorTerminalSessionState.Closing;
            record.CloseRequestedAtUtc = now;
            record.CloseReason = "terminal_policy_lease_expired";
            record.Version++;
            AddAudit(record, "close_requested", record.CloseReason, now);
            changed = true;
        }
        if (changed)
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return due.Select(ToLease).ToArray();
    }

    private async Task<McpOperatorTerminalSessionLease> CloseForExpiryAsync(
        McpOperatorTerminalSessionRecord record,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (record.State != McpOperatorTerminalSessionState.Closing)
        {
            record.State = McpOperatorTerminalSessionState.Closing;
            record.CloseRequestedAtUtc = now;
            record.CloseReason = "terminal_policy_lease_expired";
            record.Version++;
            AddAudit(record, "close_requested", record.CloseReason, now);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return ToLease(record);
    }

    private async Task<McpOperatorTerminalSessionRecord?> FindMutableAsync(string sessionId, CancellationToken cancellationToken) =>
        !IsSessionId(sessionId)
            ? null
            : await _db.McpOperatorTerminalSessions.SingleOrDefaultAsync(candidate => candidate.SessionId == sessionId, cancellationToken).ConfigureAwait(false);

    private static void ValidateCreate(McpOperatorTerminalSessionCreateRequest request)
    {
        var decision = request.Decision;
        var access = decision.Request;
        var constraints = decision.EffectiveConstraints;
        if (!IsSessionId(request.SessionId) || request.IdempotencyId == Guid.Empty ||
            !decision.IsAllowed || access.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) ||
            access.AgentId is null || access.AgentId == Guid.Empty || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null ||
            constraints is null || constraints.MaxConcurrentTerminalSessions is null || constraints.MaxTerminalIdleSeconds is null || constraints.MaxTerminalLifetimeSeconds is null ||
            constraints.AllowedShells is not { Count: > 0 } || constraints.WorkingDirectories is not { Count: > 0 } ||
            !IsSafeShell(request.ShellType) || !constraints.AllowedShells.Contains(request.ShellType, StringComparer.OrdinalIgnoreCase) ||
            !IsWorkingDirectoryAllowed(request.WorkingDirectory, constraints.WorkingDirectories) ||
            request.Columns is < 40 or > 300 || request.Rows is < 10 or > 120 ||
            request.CreatedAtUtc >= request.IdleExpiresAtUtc || request.IdleExpiresAtUtc > request.ExpiresAtUtc ||
            request.AcceptedAudit.PolicyId != decision.MatchingPolicyIds[0] ||
            request.AcceptedAudit.TenantId != access.TenantId || request.AcceptedAudit.AgentId != access.AgentId ||
            !string.Equals(request.AcceptedAudit.Subject, access.Principal.Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException("The terminal lease request is not an allowed, policy-frozen Production admission.", nameof(request));
        }
    }

    private static bool SameAdmission(McpOperatorTerminalSessionRecord record, McpOperatorTerminalSessionCreateRequest request) =>
        record.SessionId == request.SessionId &&
        record.TenantId == request.Decision.Request.TenantId &&
        record.AgentId == request.Decision.Request.AgentId &&
        record.PolicyId == request.Decision.MatchingPolicyIds.Single() &&
        record.PolicyVersion == request.Decision.SelectedPolicyVersion &&
        record.AcceptedAuditId == request.AcceptedAudit.AuditId;

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 and <= MaximumSessionIdLength } && value.All(char.IsAsciiHexDigit);

    private static bool IsSafeShell(string? value) =>
        value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsWorkingDirectoryAllowed(string? value, IReadOnlyList<string> allowed) =>
        value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl) &&
        allowed.AllowsWorkingDirectory(value);

    private static bool IsReason(string? value) =>
        value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private void AddAudit(McpOperatorTerminalSessionRecord record, string action, string? reason, DateTimeOffset now)
    {
        _db.McpOperatorTerminalSessionAudits.Add(new McpOperatorTerminalSessionAuditRecord
        {
            Id = Guid.NewGuid(),
            SessionRecordId = record.Id,
            State = record.State,
            Action = action,
            Reason = reason,
            OccurredAtUtc = now
        });
    }

    private static McpOperatorTerminalSessionLease ToLease(McpOperatorTerminalSessionRecord record) => new(
        record.SessionId,
        record.TenantId,
        record.AgentId,
        record.Generation,
        record.Subject,
        record.ClientId,
        record.McpResource,
        record.McpInstance,
        record.PolicyId,
        record.PolicyVersion,
        record.AcceptedAuditId,
        record.IdempotencyId,
        record.ShellType,
        record.WorkingDirectory,
        record.Columns,
        record.Rows,
        JsonSerializer.Deserialize(record.EffectiveConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints)
            ?? throw new InvalidOperationException("The terminal lease has invalid frozen constraints."),
        record.State,
        record.CreatedAtUtc,
        record.LastActivityAtUtc,
        record.IdleExpiresAtUtc,
        record.ExpiresAtUtc,
        record.CloseRequestedAtUtc,
        record.CloseReason,
        record.FailureCode,
        record.Version);
}
