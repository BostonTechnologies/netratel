using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.ComputerUse;
using Npgsql;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Database-owned controller lease and command fences. The unique active slot
/// covers every API replica. A command receipt is committed before dispatch;
/// a lost response can never cause an automatic second desktop mutation.
/// </summary>
public sealed class ComputerUseSessionStore(OrchestratorDbContext db) : IComputerUseSessionStore
{
    public async Task<ComputerUseOperationAdmission> CreateAsync(ComputerUseSessionCreate request, CancellationToken cancellationToken)
    {
        var prior = await db.ComputerUseOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.OperationId, cancellationToken);
        if (prior is not null)
            return await ReplayAsync(prior, request.Owner, request.SessionId, request.PayloadHash, cancellationToken);
        if (request.ReplayOnly)
            return new(null, false, true, "computer_use_outcome_unavailable");

        var active = await db.ComputerUseSessions.AsTracking().Where(row => row.TenantId == request.TenantId &&
            row.AgentId == request.AgentId && row.ActiveSlot != null).ToListAsync(cancellationToken);
        foreach (var row in active.Where(row => row.ExpiresAtUtc <= request.Now || row.IdleExpiresAtUtc <= request.Now))
        {
            row.State = "expired";
            row.ActiveSlot = null;
            row.Revision++;
            AddAudit(row, null, request.Owner, "expire", "expired", "lease_expired", request.CorrelationId, null, null, request.Now);
        }
        if (active.Any(row => row.ActiveSlot != null))
            return new(null, false, false, "computer_use_controller_conflict");
        if (active.Count > 0)
        {
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(null, false, false, "computer_use_controller_conflict"); }
        }

        var session = new ComputerUseSessionRecord
        {
            Id = request.SessionId, TenantId = request.TenantId, AgentId = request.AgentId,
            ConnectionId = request.ConnectionId, ConnectionEpoch = request.ConnectionEpoch,
            Subject = request.Owner.Subject, ClientId = request.Owner.ClientId, ServicePrincipal = request.Owner.ServicePrincipal,
            Resource = request.Owner.Resource, Instance = request.Owner.Instance,
            TargetJson = JsonSerializer.Serialize(request.Target, ComputerUseContract.JsonOptions), Provider = request.Provider,
            ControlAllowed = request.ControlAllowed, State = "opening", ActiveSlot = 1,
            CreatedAtUtc = request.Now, IdleTimeoutSeconds = request.IdleTimeoutSeconds,
            IdleExpiresAtUtc = request.Now.AddSeconds(request.IdleTimeoutSeconds), ExpiresAtUtc = request.Now.AddSeconds(request.LifetimeSeconds),
            Revision = 1, PendingOperationId = request.OperationId
        };
        db.ComputerUseSessions.Add(session);
        var operation = request.ControlAllowed ? "open" : "open_view";
        AddOperation(session, request.OperationId, request.Owner, operation, request.PayloadHash, request.AcceptedAuditId, request.CorrelationId, request.Now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(ToLease(session), true, false, null);
        }
        catch (DbUpdateException exception) when (IsConflict(exception))
        {
            db.ChangeTracker.Clear();
            prior = await db.ComputerUseOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.OperationId, cancellationToken);
            return prior is null ? new(null, false, false, "computer_use_controller_conflict")
                : await ReplayAsync(prior, request.Owner, request.SessionId, request.PayloadHash, cancellationToken);
        }
    }

    public async Task<ComputerUseSessionLease?> GetOwnedAsync(Guid sessionId, int tenantId, Guid agentId, ComputerUseOwner owner, CancellationToken cancellationToken)
    {
        var row = await db.ComputerUseSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId &&
            row.TenantId == tenantId && row.AgentId == agentId && row.Subject == owner.Subject &&
            row.ClientId == owner.ClientId && row.ServicePrincipal == owner.ServicePrincipal &&
            row.Resource == owner.Resource && row.Instance == owner.Instance, cancellationToken);
        return row is null ? null : ToLease(row);
    }

    public async Task<ComputerUseSessionLease?> GetActiveAsync(int tenantId, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.ComputerUseSessions.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId &&
            row.AgentId == agentId && row.ActiveSlot != null && row.ExpiresAtUtc > now && row.IdleExpiresAtUtc > now, cancellationToken);
        return row is null ? null : ToLease(row);
    }

    public async Task<ComputerUseOperationAdmission> BeginAsync(ComputerUseOperationStart request, CancellationToken cancellationToken)
    {
        var session = await db.ComputerUseSessions.AsTracking().SingleOrDefaultAsync(row => row.Id == request.SessionId &&
            row.TenantId == request.TenantId && row.AgentId == request.AgentId, cancellationToken);
        if (session is null || (!request.HumanTakeover && Owner(session) != request.Owner))
            return new(null, false, false, "computer_use_session_not_found");
        if (request.HumanTakeover && request.Operation is not ("pause" or "close"))
            return new(null, false, false, "computer_use_control_not_allowed");
        var prior = await db.ComputerUseOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.OperationId, cancellationToken);
        if (prior is not null)
            return await ReplayAsync(prior, request.Owner, request.SessionId, request.PayloadHash, cancellationToken);
        if (request.ReplayOnly)
            return new(ToLease(session), false, true, "computer_use_outcome_unavailable");

        var closing = request.Operation is "close" or "close_view";
        if (request.Operation == "close_view" && session.ControlAllowed ||
            request.Operation is "act" or "pause" or "resume" && !session.ControlAllowed && !request.HumanTakeover)
            return new(ToLease(session), false, false, "computer_use_control_not_allowed");
        if (session.ExpiresAtUtc <= request.Now || session.IdleExpiresAtUtc <= request.Now || session.ActiveSlot is null)
        {
            if (session.ActiveSlot is not null)
            {
                session.State = "expired";
                session.ActiveSlot = null;
                session.Revision++;
                AddAudit(session, null, request.Owner, request.Operation, "refused", "computer_use_lease_expired", request.CorrelationId, request.AcceptedAuditId, request.PayloadHash, request.Now);
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return new(null, false, false, "computer_use_operation_conflict"); }
            }
            return closing
                ? new(ToLease(session), false, true, null, new(ComputerUseContract.Version, request.OperationId, session.Id, ComputerUseStatuses.Completed, "lease_already_closed", Provider: session.Provider))
                : new(ToLease(session), false, false, "computer_use_lease_expired");
        }
        if (!closing && session.State == "closing")
            return new(ToLease(session), false, false, "computer_use_session_closing");
        if (request.Operation is "act" or "observe" && session.State != "active")
            return new(ToLease(session), false, false, "computer_use_session_paused");
        if (request.Operation == "resume" && session.State != "paused")
            return new(ToLease(session), false, false, "computer_use_session_not_paused");
        if (request.Operation == "resume" && session.OperatorPaused)
            return new(ToLease(session), false, false, "computer_use_operator_takeover");
        if (session.PendingOperationId is not null && !closing && request.Operation != "pause")
            return new(ToLease(session), false, false, "computer_use_operation_pending");

        session.Revision++;
        session.PendingOperationId = request.OperationId;
        session.IdleExpiresAtUtc = Min(request.Now.AddSeconds(session.IdleTimeoutSeconds), session.ExpiresAtUtc);
        if (closing) session.State = "closing";
        if (request.Operation == "pause")
        {
            session.State = "paused";
            session.OperatorPaused |= request.HumanTakeover;
        }
        AddOperation(session, request.OperationId, request.Owner, request.Operation, request.PayloadHash, request.AcceptedAuditId, request.CorrelationId, request.Now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(ToLease(session), true, false, null);
        }
        catch (DbUpdateException exception) when (IsConflict(exception))
        {
            db.ChangeTracker.Clear();
            prior = await db.ComputerUseOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.OperationId, cancellationToken);
            return prior is null ? new(null, false, false, "computer_use_operation_conflict")
                : await ReplayAsync(prior, request.Owner, request.SessionId, request.PayloadHash, cancellationToken);
        }
    }

    public async Task CompleteAsync(Guid operationId, ComputerUseResult result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Use a fresh tracked snapshot after gateway I/O. A human pause/close
        // may have advanced the revision while the original action ran.
        db.ChangeTracker.Clear();
        var operation = await db.ComputerUseOperations.AsTracking().SingleOrDefaultAsync(row => row.Id == operationId, cancellationToken);
        if (operation is null || operation.Status != "pending") return;
        var session = await db.ComputerUseSessions.AsTracking().SingleAsync(row => row.Id == operation.SessionId, cancellationToken);
        operation.Status = result.Status;
        operation.Code = result.Code is { Length: <= 128 } ? result.Code : "provider_result_invalid";
        operation.MayHaveApplied = result.MayHaveApplied;
        operation.CompletedAtUtc = now;
        if (session.PendingOperationId == operationId)
        {
            session.PendingOperationId = null;
            session.Revision++;
            var completed = result.Status is ComputerUseStatuses.Completed or ComputerUseStatuses.Observed or ComputerUseStatuses.Verified;
            if (operation.Operation is "open" or "open_view")
            {
                session.State = completed ? "active" : "failed";
                if (!completed && !result.MayHaveApplied && result.Status is not (ComputerUseStatuses.Unknown or ComputerUseStatuses.Cancelled))
                    session.ActiveSlot = null;
            }
            else if (operation.Operation is "close" or "close_view")
            {
                if (completed) { session.State = "closed"; session.ActiveSlot = null; }
            }
            else if (operation.Operation == "resume" && completed) session.State = "active";
            if (session.ExpiresAtUtc <= now || session.IdleExpiresAtUtc <= now)
            {
                session.State = "expired";
                session.ActiveSlot = null;
            }
        }
        AddAudit(session, operation.Id, OperationOwner(operation), operation.Operation, result.Status,
            operation.Code, operation.CorrelationId, operation.AcceptedAuditId, operation.PayloadHash, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            // Persist the command outcome independently if a concurrent revoke
            // won the session write. Never restore the old session state.
            db.ChangeTracker.Clear();
            operation = await db.ComputerUseOperations.AsTracking().SingleAsync(row => row.Id == operationId, cancellationToken);
            if (operation.Status != "pending") return;
            operation.Status = result.Status;
            operation.Code = result.Code is { Length: <= 128 } ? result.Code : "provider_result_invalid";
            operation.MayHaveApplied = result.MayHaveApplied;
            operation.CompletedAtUtc = now;
            AddAudit(session, operation.Id, OperationOwner(operation), operation.Operation, result.Status,
                operation.Code, operation.CorrelationId, operation.AcceptedAuditId, operation.PayloadHash, now);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RecordAuditAsync(ComputerUseAudit audit, CancellationToken cancellationToken)
    {
        db.ComputerUseAudits.Add(new ComputerUseAuditRecord
        {
            Id = audit.AuditId, TenantId = audit.TenantId, AgentId = audit.AgentId, SessionId = audit.SessionId,
            OperationId = audit.OperationId, Subject = audit.Owner.Subject, ClientId = audit.Owner.ClientId,
            ServicePrincipal = audit.Owner.ServicePrincipal, Resource = audit.Owner.Resource, Instance = audit.Owner.Instance,
            Operation = audit.Operation, Outcome = audit.Outcome, Code = audit.Code, CorrelationId = audit.CorrelationId,
            AcceptedAuditId = audit.AcceptedAuditId, PayloadHash = audit.PayloadHash, OccurredAtUtc = audit.OccurredAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ComputerUseOperationAdmission> ReplayAsync(ComputerUseOperationRecord operation, ComputerUseOwner owner,
        Guid sessionId, string hash, CancellationToken cancellationToken)
    {
        if (operation.SessionId != sessionId || OperationOwner(operation) != owner || operation.PayloadHash != hash)
            return new(null, false, false, "computer_use_idempotency_conflict");
        var session = await db.ComputerUseSessions.AsNoTracking().SingleAsync(row => row.Id == sessionId, cancellationToken);
        return operation.Status == "pending"
            ? new(ToLease(session), false, true, "computer_use_operation_pending")
            : new(ToLease(session), false, true, null,
                new(ComputerUseContract.Version, operation.Id, sessionId, operation.Status, operation.Code,
                    "This is a recorded outcome. Observe again for current application state.", session.Provider,
                    MayHaveApplied: operation.MayHaveApplied));
    }

    private void AddOperation(ComputerUseSessionRecord session, Guid operationId, ComputerUseOwner owner,
        string operation, string payloadHash, Guid? auditId, string correlationId, DateTimeOffset now)
    {
        db.ComputerUseOperations.Add(new ComputerUseOperationRecord
        {
            Id = operationId, SessionId = session.Id, Subject = owner.Subject, ClientId = owner.ClientId,
            ServicePrincipal = owner.ServicePrincipal, Resource = owner.Resource, Instance = owner.Instance,
            Operation = operation, PayloadHash = payloadHash, AcceptedAuditId = auditId, CorrelationId = correlationId, CreatedAtUtc = now
        });
        AddAudit(session, operationId, owner, operation, "admitted", null, correlationId, auditId, payloadHash, now);
    }

    private void AddAudit(ComputerUseSessionRecord session, Guid? operationId, ComputerUseOwner owner,
        string operation, string outcome, string? code, string correlationId, Guid? auditId, string? payloadHash, DateTimeOffset now) =>
        db.ComputerUseAudits.Add(new ComputerUseAuditRecord
        {
            Id = Guid.NewGuid(), TenantId = session.TenantId, AgentId = session.AgentId, SessionId = session.Id,
            OperationId = operationId, Subject = owner.Subject, ClientId = owner.ClientId, ServicePrincipal = owner.ServicePrincipal,
            Resource = owner.Resource, Instance = owner.Instance, Operation = operation, Outcome = outcome, Code = code,
            CorrelationId = correlationId, AcceptedAuditId = auditId, PayloadHash = payloadHash, OccurredAtUtc = now
        });

    private static ComputerUseSessionLease ToLease(ComputerUseSessionRecord row) => new(
        row.Id, row.TenantId, row.AgentId, row.ConnectionId, checked((ulong)row.ConnectionEpoch), Owner(row),
        JsonSerializer.Deserialize<ComputerUseTarget>(row.TargetJson, ComputerUseContract.JsonOptions)!, row.Provider,
        row.ControlAllowed, row.State, row.CreatedAtUtc, row.IdleExpiresAtUtc, row.ExpiresAtUtc,
        row.IdleTimeoutSeconds, row.Revision, row.PendingOperationId, row.OperatorPaused);
    private static ComputerUseOwner Owner(ComputerUseSessionRecord row) => new(row.Subject, row.ClientId, row.ServicePrincipal, row.Resource, row.Instance);
    private static ComputerUseOwner OperationOwner(ComputerUseOperationRecord row) => new(row.Subject, row.ClientId, row.ServicePrincipal, row.Resource, row.Instance);
    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static bool IsConflict(DbUpdateException exception) => exception is DbUpdateConcurrencyException ||
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure };
}
