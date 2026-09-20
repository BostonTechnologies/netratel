using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL projection and audit adapter for the RemoteSupportSessionActor.
/// It is intentionally registered only behind the actor-facing application
/// contract; HTTP and gRPC adapters do not receive this type.
/// </summary>
public sealed class RemoteSupportLifecycleStore(
    IServiceScopeFactory scopeFactory) : IRemoteSupportLifecycleStore
{
    private const string OpenRequestConstraint = "IX_RemoteSupportSessions_TenantId_AgentId_OpenRequestId";

    public async Task<RemoteSupportLifecycleOpenResult> OpenAsync(
        RemoteSupportOpenSessionCommand command,
        Guid remoteSupportSessionId,
        CancellationToken cancellationToken)
    {
        if (!RemoteSupportV2ContractValidator.TryValidate(command, out var error))
        {
            throw new ArgumentException(error?.Message ?? "The remote-support open command is invalid.", nameof(command));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var existing = await db.RemoteSupportSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.TenantId == command.TenantId &&
                record.AgentId == command.AgentId &&
                record.OpenRequestId == command.RequestId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return await ExistingOpenAsync(db, existing, command, cancellationToken).ConfigureAwait(false);
        }

        var now = command.RequestedAtUtc;
        var record = new RemoteSupportSessionRecord
        {
            Id = remoteSupportSessionId,
            TenantId = command.TenantId,
            AgentId = command.AgentId,
            OpenRequestId = command.RequestId,
            ContractVersion = command.ContractVersion,
            InitiatingOperatorId = command.InitiatingOperator.OperatorId.Trim(),
            TargetKind = command.Target.Kind.Trim().ToLowerInvariant(),
            TargetWindowsSessionId = command.Target.WindowsSessionId,
            TargetUserSidHash = command.Target.UserSidHash?.Trim(),
            TargetInventorySequence = command.Target.InventorySequence,
            RequestedCapabilitiesJson = SerializeCapabilities(command.RequestedCapabilities),
            GrantedCapabilitiesJson = "[]",
            State = RemoteSupportV2SessionStates.Requested,
            LifecycleRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = command.ExpiresAtUtc
        };
        var audit = NewAudit(
            record,
            auditSequence: 1,
            RemoteSupportV2AuditEventTypes.SessionRequested,
            "operator",
            record.InitiatingOperatorId,
            command.RequestId,
            "accepted",
            null,
            now);
        db.RemoteSupportSessions.Add(record);
        db.RemoteSupportAuditEvents.Add(audit);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new(ToSnapshot(record), ToContract(audit), IsDuplicate: false);
        }
        catch (DbUpdateException exception) when (IsConcurrentOpenDuplicate(exception))
        {
            db.ChangeTracker.Clear();
            var raced = await db.RemoteSupportSessions
                .AsNoTracking()
                .SingleAsync(item =>
                    item.TenantId == command.TenantId &&
                    item.AgentId == command.AgentId &&
                    item.OpenRequestId == command.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ExistingOpenAsync(db, raced, command, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RemoteSupportSessionSnapshot?> LoadAsync(
        RemoteSupportSessionKey session,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var record = await FindAsync(db, session, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToSnapshot(record);
    }

    public async Task<RemoteSupportLifecycleTransitionResult> TransitionAsync(
        RemoteSupportLifecycleTransition transition,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var record = await db.RemoteSupportSessions.SingleOrDefaultAsync(item =>
                item.Id == transition.Session.RemoteSupportSessionId &&
                item.TenantId == transition.Session.TenantId &&
                item.AgentId == transition.Session.AgentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new(RemoteSupportLifecycleTransitionDisposition.Missing, null, null);
        }

        var duplicate = await db.RemoteSupportAuditEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.RemoteSupportSessionId == record.Id && item.RequestId == transition.RequestId,
                cancellationToken)
            .ConfigureAwait(false);
        if (duplicate is not null)
        {
            return new(RemoteSupportLifecycleTransitionDisposition.Duplicate, ToSnapshot(record), ToContract(duplicate));
        }

        var revision = ToLong(record.LifecycleRevision);
        if (transition.ExpectedLifecycleRevision is { } expected && expected != revision)
        {
            return new(RemoteSupportLifecycleTransitionDisposition.StaleRevision, ToSnapshot(record), null);
        }

        var auditSequence = await db.RemoteSupportAuditEvents
            .Where(item => item.RemoteSupportSessionId == record.Id)
            .Select(item => (decimal?)item.AuditSequence)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        var nextRevision = checked(revision + 1);
        record.State = transition.NextState.Trim().ToLowerInvariant();
        record.LifecycleRevision = nextRevision;
        record.UpdatedAtUtc = transition.OccurredAtUtc;
        if (record.State is RemoteSupportV2SessionStates.Completed or RemoteSupportV2SessionStates.Failed or RemoteSupportV2SessionStates.Expired)
        {
            record.TerminalAtUtc = transition.OccurredAtUtc;
            record.TerminalReasonCode = transition.FailureCode;
        }

        var audit = NewAudit(
            record,
            checked(ToLong(auditSequence) + 1),
            transition.EventType,
            transition.ActorKind,
            transition.ActorId,
            transition.RequestId,
            transition.Outcome,
            transition.FailureCode,
            transition.OccurredAtUtc);
        db.RemoteSupportAuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(RemoteSupportLifecycleTransitionDisposition.Applied, ToSnapshot(record), ToContract(audit));
    }

    public async Task<IReadOnlyList<RemoteSupportAuditEvent>> ReadAuditAsync(
        RemoteSupportSessionKey session,
        long afterAuditSequence,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var records = await db.RemoteSupportAuditEvents
            .AsNoTracking()
            .Where(item => item.RemoteSupportSessionId == session.RemoteSupportSessionId &&
                item.TenantId == session.TenantId && item.AgentId == session.AgentId &&
                item.AuditSequence > afterAuditSequence)
            .OrderBy(item => item.AuditSequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(ToContract).ToArray();
    }

    private static async Task<RemoteSupportLifecycleOpenResult> ExistingOpenAsync(
        OrchestratorDbContext db,
        RemoteSupportSessionRecord record,
        RemoteSupportOpenSessionCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.InitiatingOperatorId, command.InitiatingOperator.OperatorId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A Remote Support request ID is bound to a different initiating operator.");
        }

        if (!string.Equals(record.TargetKind, command.Target.Kind.Trim(), StringComparison.OrdinalIgnoreCase) ||
            record.TargetWindowsSessionId != command.Target.WindowsSessionId ||
            !string.Equals(record.TargetUserSidHash, command.Target.UserSidHash?.Trim(), StringComparison.Ordinal) ||
            record.TargetInventorySequence != (command.Target.InventorySequence is { } sequence ? (decimal?)sequence : null) ||
            !string.Equals(record.RequestedCapabilitiesJson, SerializeCapabilities(command.RequestedCapabilities), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A Remote Support request ID is bound to a different target or capability request.");
        }

        var audit = await db.RemoteSupportAuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.RemoteSupportSessionId == record.Id && item.RequestId == command.RequestId,
                cancellationToken)
            .ConfigureAwait(false);
        return new(ToSnapshot(record), ToContract(audit), IsDuplicate: true);
    }

    private static Task<RemoteSupportSessionRecord?> FindAsync(
        OrchestratorDbContext db,
        RemoteSupportSessionKey session,
        CancellationToken cancellationToken) =>
        db.RemoteSupportSessions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == session.RemoteSupportSessionId &&
            item.TenantId == session.TenantId &&
            item.AgentId == session.AgentId,
            cancellationToken);

    private static RemoteSupportAuditEventRecord NewAudit(
        RemoteSupportSessionRecord record,
        long auditSequence,
        string eventType,
        string actorKind,
        string actorId,
        Guid requestId,
        string outcome,
        string? failureCode,
        DateTimeOffset occurredAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            RemoteSupportSessionId = record.Id,
            TenantId = record.TenantId,
            AgentId = record.AgentId,
            ContractVersion = record.ContractVersion,
            AuditSequence = auditSequence,
            LifecycleRevision = record.LifecycleRevision,
            EventType = eventType,
            ActorKind = actorKind,
            ActorId = actorId,
            RequestId = requestId,
            Outcome = outcome,
            FailureCode = failureCode,
            OccurredAtUtc = occurredAtUtc
        };

    private static RemoteSupportSessionSnapshot ToSnapshot(RemoteSupportSessionRecord record) => new(
        record.ContractVersion,
        new(record.TenantId, record.AgentId, record.Id),
        record.OpenRequestId,
        new(record.InitiatingOperatorId),
        new(record.TargetKind, record.TargetWindowsSessionId, record.TargetUserSidHash,
            record.TargetInventorySequence is { } sequence ? checked((ulong)sequence) : null),
        DeserializeCapabilities(record.GrantedCapabilitiesJson),
        record.State,
        ToLong(record.LifecycleRevision),
        record.CreatedAtUtc,
        record.UpdatedAtUtc,
        record.ExpiresAtUtc,
        record.TerminalReasonCode);

    private static RemoteSupportAuditEvent ToContract(RemoteSupportAuditEventRecord record) => new(
        record.ContractVersion,
        new(record.TenantId, record.AgentId, record.RemoteSupportSessionId),
        ToLong(record.AuditSequence),
        record.Id,
        record.EventType,
        record.ActorKind,
        record.ActorId,
        record.RequestId,
        record.Outcome,
        record.FailureCode,
        record.OccurredAtUtc);

    private static string SerializeCapabilities(IReadOnlyList<string> capabilities) =>
        JsonSerializer.Serialize(capabilities.Select(capability => capability.Trim()).ToArray());

    private static IReadOnlyList<string> DeserializeCapabilities(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static long ToLong(decimal value) => checked((long)value);

    private static bool IsConcurrentOpenDuplicate(DbUpdateException exception) =>
        DatabaseExceptionClassifier.IsUniqueViolation(exception, OpenRequestConstraint);
}
