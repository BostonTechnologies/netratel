using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Rechecks current execution authority in the caller's accepted-write transaction.</summary>
public interface IFlowTransactionAdmission
{
    Task<FlowDispatchDecision> CanStartActionAsync(OrchestratorDbContext db, FlowRunLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>The mandatory default requires real current Monitoring and owner authority; no local fallback admits an action.</summary>
public sealed class MonitoringFlowTransactionAdmission(IFlowExecutionAuthorityVerifier authority, TimeProvider clock)
    : IFlowTransactionAdmission
{
    public async Task<FlowDispatchDecision> CanStartActionAsync(OrchestratorDbContext db, FlowRunLease lease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!db.Database.IsNpgsql() || lease.RunId == Guid.Empty)
            return new(false, "flow-transaction-admission-invalid");
        var currentTransaction = db.Database.CurrentTransaction;
        if (currentTransaction is null)
            return new(false, "flow-transaction-admission-invalid");
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var transaction = (NpgsqlTransaction)currentTransaction.GetDbTransaction();
            if (lease.ExpiresAtUtc <= await ClientConnectionEpochStore.EffectiveNowAsync(connection, transaction, clock,
                cancellationToken).ConfigureAwait(false))
                return new(false, "flow-transaction-lease-expired");
            if (!await authority.AuthorizeAsync(lease.Event.TenantId, lease.Event.Authority, cancellationToken).ConfigureAwait(false))
                return new(false, "flow-transaction-authority-unavailable");
            var decision = await MonitoringStore.LockAndAdmitFlowAsync(db, lease.Event, lease.RunId, clock,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return lease.ExpiresAtUtc <= await ClientConnectionEpochStore.EffectiveNowAsync(connection, transaction, clock,
                cancellationToken).ConfigureAwait(false)
                ? new(false, "flow-transaction-lease-expired") : decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "flow-transaction-admission-unavailable"); }
    }
}
