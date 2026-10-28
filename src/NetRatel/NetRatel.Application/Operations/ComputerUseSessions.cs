using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Application.Operations;

/// <summary>The authenticated caller binding; never supplied in a desktop action payload.</summary>
public sealed record ComputerUseOwner(string Subject, string ClientId, string ServicePrincipal, string Resource, string Instance);

public sealed record ComputerUseSessionLease(
    Guid SessionId, int TenantId, Guid AgentId, Guid ConnectionId, ulong ConnectionEpoch,
    ComputerUseOwner Owner, ComputerUseTarget Target, string Provider, bool ControlAllowed,
    string State, DateTimeOffset CreatedAtUtc, DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc, int IdleTimeoutSeconds, long Revision, Guid? PendingOperationId, bool OperatorPaused = false);

public sealed record ComputerUseSessionCreate(
    Guid SessionId, Guid OperationId, int TenantId, Guid AgentId, Guid ConnectionId, ulong ConnectionEpoch,
    ComputerUseOwner Owner, ComputerUseTarget Target, string Provider, bool ControlAllowed,
    int LifetimeSeconds, int IdleTimeoutSeconds, string PayloadHash, Guid? AcceptedAuditId,
    string CorrelationId, DateTimeOffset Now, bool ReplayOnly = false);

public sealed record ComputerUseOperationStart(
    Guid SessionId, Guid OperationId, int TenantId, Guid AgentId, ComputerUseOwner Owner,
    string Operation, string PayloadHash, Guid? AcceptedAuditId, string CorrelationId,
    DateTimeOffset Now, bool HumanTakeover = false, bool ReplayOnly = false);

public sealed record ComputerUseOperationAdmission(
    ComputerUseSessionLease? Session, bool Dispatch, bool Replayed, string? FailureCode,
    ComputerUseResult? PreviousResult = null);

/// <summary>Content-free immutable evidence; text, accessibility trees and screenshots are never persisted here.</summary>
public sealed record ComputerUseAudit(
    Guid AuditId, int TenantId, Guid AgentId, Guid? SessionId, Guid? OperationId,
    ComputerUseOwner Owner, string Operation, string Outcome, string? Code,
    string CorrelationId, Guid? AcceptedAuditId, string? PayloadHash, DateTimeOffset OccurredAtUtc);

public interface IComputerUseSessionStore
{
    Task<ComputerUseOperationAdmission> CreateAsync(ComputerUseSessionCreate request, CancellationToken cancellationToken);
    Task<ComputerUseSessionLease?> GetOwnedAsync(Guid sessionId, int tenantId, Guid agentId, ComputerUseOwner owner, CancellationToken cancellationToken);
    Task<ComputerUseSessionLease?> GetActiveAsync(int tenantId, Guid agentId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ComputerUseOperationAdmission> BeginAsync(ComputerUseOperationStart request, CancellationToken cancellationToken);
    Task CompleteAsync(Guid operationId, ComputerUseResult result, DateTimeOffset now, CancellationToken cancellationToken);
    Task RecordAuditAsync(ComputerUseAudit audit, CancellationToken cancellationToken);
}
