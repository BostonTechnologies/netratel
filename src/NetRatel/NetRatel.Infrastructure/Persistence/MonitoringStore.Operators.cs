using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    /// <summary>Compare the occurrence and operator/configuration fences against locked current state, independently of observations.</summary>
    public async Task<MonitoringStoreWriteResult> OperateOccurrenceAsync(MonitoringOperatorCommand command, bool clear,
        CancellationToken cancellationToken)
    {
        RequireSeries(command.Series);
        MonitoringContractValidator.RequireReason(command.OperatorId, command.Reason);
        if (command.OccurrenceId == Guid.Empty) throw new ArgumentException("occurrence_required");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Keep the existing lock order and generic repository CAS for every telemetry/configuration commit.
        var configuration = await LockConfigurationAsync(db, command.Series.TenantId, cancellationToken).ConfigureAwait(false);
        var current = await LockSeriesAsync(db, command.Series, cancellationToken).ConfigureAwait(false);
        var state = current is null ? null : ReadState(current);
        MonitoringStoreWriteResult Conflict(string code) => new(MonitoringStoreWriteDisposition.Conflict, state, code);
        if (state?.Occurrence is not { } occurrence || occurrence.OccurrenceId != command.OccurrenceId)
            return Conflict("monitoring_occurrence_replaced");
        if (command.ExpectedConfigurationRevision is { } expectedConfiguration && expectedConfiguration != configuration.Revision)
            return Conflict("monitoring_configuration_conflict");
        // Retrying the same action has one deliberate successful result and creates no second event/audit.
        if (!clear && occurrence is { EndedAtUtc: null, AcknowledgedAtUtc: not null } ||
            clear && occurrence.ClosureDisposition == MonitoringClosureDisposition.ManuallyCleared)
            return new(MonitoringStoreWriteDisposition.Stored, state);
        if (occurrence.EndedAtUtc is not null || state.Phase is MonitoringPhase.Suspended or MonitoringPhase.NotApplicable)
            return Conflict("monitoring_occurrence_closed");
        if (command.ExpectedOperatorRevision is { } expectedOperator && expectedOperator != state.OperatorRevision)
            return Conflict("monitoring_operator_conflict");
        var definition = await db.MonitoringRules.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == command.Series.TenantId &&
            row.RuleId == command.Series.RuleId, cancellationToken).ConfigureAwait(false);
        var rule = definition is null ? null : Deserialize<MonitoringRuleDto>(definition.DefinitionJson);
        if (rule is null || !rule.Enabled || rule.EvaluationRevision != occurrence.PinnedRule.EvaluationRevision)
            return Conflict("monitoring_configuration_conflict");
        var result = clear
            ? _evaluator.Clear(state, rule, command.OperatorId, command.Reason, command.OperatorDisplayName)
            : _evaluator.Acknowledge(state, rule, command.OperatorId, command.Reason, command.OperatorDisplayName);
        result = await AttributeClientIdentityAsync(db, result, current, cancellationToken).ConfigureAwait(false);
        await ApplyEvaluationAsync(db, result, configuration.Revision, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(MonitoringStoreWriteDisposition.Stored, result.State);
    }
}
