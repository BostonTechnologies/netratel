using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<MonitoringConfigurationSnapshot> GetAsync(int tenantId, CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var revision = await db.MonitoringTenantConfigurations.Where(row => row.TenantId == tenantId).Select(row => (ulong?)row.Revision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        return await ReadConfigurationAsync(db, tenantId, revision, cancellationToken).ConfigureAwait(false);
    }

    public Task<MonitoringConfigurationWriteResult> SaveRuleAsync(MonitoringRuleSaveRequest request, CancellationToken cancellationToken)
    {
        MonitoringContractValidator.RequireRule(request.Rule);
        MonitoringContractValidator.RequireReason(request.OperatorId, request.Reason);
        return MutateConfigurationAsync(request.Rule.TenantId, request.ExpectedConfigurationRevision, request.OperatorId, request.Reason, "rule", request.Rule.RuleId, "save_rule",
            async (db, before, eligible, ct) =>
            {
                var rule = request.Rule;
                ValidateTargets(rule, before.Groups, eligible);
                if (rule.PublishedFlowVersionId is { } flow && !await publishedFlows.IsPublishedAsync(rule.TenantId, flow, ct).ConfigureAwait(false))
                    throw new ArgumentException("published_flow_unavailable");
                var row = await db.MonitoringRules.SingleOrDefaultAsync(item => item.TenantId == rule.TenantId && item.RuleId == rule.RuleId, ct).ConfigureAwait(false);
                var old = row is null ? null : Deserialize<MonitoringRuleDto>(row.DefinitionJson);
                if (rule.Revision != checked((old?.Revision ?? 0) + 1)) throw new ArgumentException("invalid_rule_revision");
                var definitionChanged = old is not null && MonitoringContractValidator.EvaluationFingerprint(old) != MonitoringContractValidator.EvaluationFingerprint(rule);
                if (rule.EvaluationRevision != (old is null ? 1UL : definitionChanged ? checked(old.EvaluationRevision + 1) : old.EvaluationRevision) ||
                    definitionChanged && request.ResetPolicy != MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow)
                    throw new ArgumentException("explicit_definition_reset_required");
                if (row is null)
                {
                    if (before.Rules.Length >= MonitoringLimits.MaximumRulesPerTenant) throw new ArgumentException("rule_capacity_exceeded");
                    db.MonitoringRules.Add(new() { TenantId = rule.TenantId, RuleId = rule.RuleId, Revision = rule.Revision, DefinitionJson = Serialize(rule) });
                }
                else { row.Revision = rule.Revision; row.DefinitionJson = Serialize(rule); }
                var after = before with { Rules = before.Rules.Where(item => item.RuleId != rule.RuleId).Append(rule).OrderBy(item => item.RuleId).ToImmutableArray() };
                await ReconcileConfigurationSeriesAsync(db, before, after, eligible, request.OperatorId, request.Reason, definitionChanged ? rule.RuleId : null, ct).ConfigureAwait(false);
                return after;
            }, cancellationToken);
    }

    public Task<MonitoringConfigurationWriteResult> SaveGroupAsync(MonitoringGroupSaveRequest request, CancellationToken cancellationToken)
    {
        if (!MonitoringContractValidator.TryValidateGroup(request.Group, out var error)) throw new ArgumentException(error);
        MonitoringContractValidator.RequireReason(request.OperatorId, request.Reason);
        return MutateConfigurationAsync(request.Group.TenantId, request.ExpectedConfigurationRevision, request.OperatorId, request.Reason, "group", request.Group.GroupId, "save_group",
            async (db, before, eligible, ct) =>
            {
                var group = request.Group;
                if (group.AgentIds.Any(id => !eligible.Contains(id))) throw new ArgumentException("unknown_or_ineligible_group_agent");
                var row = await db.MonitoringGroups.SingleOrDefaultAsync(item => item.TenantId == group.TenantId && item.GroupId == group.GroupId, ct).ConfigureAwait(false);
                if (group.Revision != checked((row?.Revision ?? 0) + 1)) throw new ArgumentException("invalid_group_revision");
                if (row is null)
                {
                    if (before.Groups.Length >= MonitoringLimits.MaximumGroupsPerTenant) throw new ArgumentException("group_capacity_exceeded");
                    db.MonitoringGroups.Add(new() { TenantId = group.TenantId, GroupId = group.GroupId, Revision = group.Revision, DefinitionJson = Serialize(group) });
                }
                else { row.Revision = group.Revision; row.DefinitionJson = Serialize(group); }
                var after = before with { Groups = before.Groups.Where(item => item.GroupId != group.GroupId).Append(group).OrderBy(item => item.GroupId).ToImmutableArray() };
                foreach (var rule in after.Rules) ValidateTargets(rule, after.Groups, eligible, allowIneligibleExisting: true);
                await ReconcileConfigurationSeriesAsync(db, before, after, eligible, request.OperatorId, request.Reason, null, ct).ConfigureAwait(false);
                return after;
            }, cancellationToken);
    }

    public Task<MonitoringConfigurationWriteResult> SaveBypassAsync(MonitoringBypassSaveRequest request, CancellationToken cancellationToken)
    {
        if (!MonitoringContractValidator.TryValidateBypass(request.Bypass, out var error)) throw new ArgumentException(error);
        var bypass = request.Bypass;
        return MutateConfigurationAsync(bypass.TenantId, request.ExpectedConfigurationRevision, bypass.OperatorId, bypass.Reason, "bypass", bypass.BypassId, "save_bypass",
            async (db, before, eligible, ct) =>
            {
                if (bypass.RuleId is { } rule && !before.Rules.Any(item => item.RuleId == rule) ||
                    bypass.AgentId is { } agent && !eligible.Contains(agent) ||
                    bypass.GroupId is { } group && !before.Groups.Any(item => item.GroupId == group)) throw new ArgumentException("unknown_or_foreign_bypass_scope");
                if (await db.MonitoringBypasses.AnyAsync(item => item.TenantId == bypass.TenantId && item.BypassId == bypass.BypassId, ct).ConfigureAwait(false))
                    throw new ArgumentException("bypass_is_immutable_remove_and_create");
                if (before.Bypasses.Length >= MonitoringLimits.MaximumBypassesPerEvaluation) throw new ArgumentException("bypass_capacity_exceeded");
                db.MonitoringBypasses.Add(new() { TenantId = bypass.TenantId, BypassId = bypass.BypassId, ExpiresAtUtc = bypass.ExpiresAtUtc, DefinitionJson = Serialize(bypass) });
                var after = before with { Bypasses = before.Bypasses.Add(bypass) };
                await ReconcileConfigurationSeriesAsync(db, before, after, eligible, bypass.OperatorId, bypass.Reason, null, ct).ConfigureAwait(false);
                return after;
            }, cancellationToken);
    }

    public Task<MonitoringConfigurationWriteResult> DeleteRuleAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken) =>
        DeleteConfigurationAsync(request, "rule", cancellationToken);
    public Task<MonitoringConfigurationWriteResult> DeleteGroupAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken) =>
        DeleteConfigurationAsync(request, "group", cancellationToken);
    public Task<MonitoringConfigurationWriteResult> DeleteBypassAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken) =>
        DeleteConfigurationAsync(request, "bypass", cancellationToken);

    private Task<MonitoringConfigurationWriteResult> DeleteConfigurationAsync(MonitoringConfigurationDeleteRequest request, string kind, CancellationToken ct)
    {
        MonitoringContractValidator.RequireReason(request.OperatorId, request.Reason);
        if (request.EntityId == Guid.Empty) throw new ArgumentException("invalid_entity");
        return MutateConfigurationAsync(request.TenantId, request.ExpectedConfigurationRevision, request.OperatorId, request.Reason, kind, request.EntityId, "delete_" + kind,
            async (db, before, eligible, cancellation) =>
            {
                var after = before;
                switch (kind)
                {
                    case "rule":
                        var rule = await db.MonitoringRules.SingleOrDefaultAsync(row => row.TenantId == request.TenantId && row.RuleId == request.EntityId, cancellation).ConfigureAwait(false);
                        if (rule is null) return null;
                        db.MonitoringRules.Remove(rule);
                        after = before with { Rules = before.Rules.Where(item => item.RuleId != request.EntityId).ToImmutableArray() };
                        // Remove dependent overlays together; their creation and removal remains separately audited here.
                        var ruleBypasses = await db.MonitoringBypasses.Where(row => row.TenantId == request.TenantId).ToListAsync(cancellation).ConfigureAwait(false);
                        foreach (var row in ruleBypasses.Where(row => Deserialize<MonitoringBypassDto>(row.DefinitionJson).RuleId == request.EntityId)) db.MonitoringBypasses.Remove(row);
                        after = after with { Bypasses = after.Bypasses.Where(item => item.RuleId != request.EntityId).ToImmutableArray() };
                        break;
                    case "group":
                        if (before.Rules.Any(rule => rule.Targets.GroupIds.Contains(request.EntityId)) || before.Bypasses.Any(bypass => bypass.GroupId == request.EntityId))
                            throw new ArgumentException("group_in_use");
                        var group = await db.MonitoringGroups.SingleOrDefaultAsync(row => row.TenantId == request.TenantId && row.GroupId == request.EntityId, cancellation).ConfigureAwait(false);
                        if (group is null) return null;
                        db.MonitoringGroups.Remove(group); after = before with { Groups = before.Groups.Where(item => item.GroupId != request.EntityId).ToImmutableArray() };
                        break;
                    default:
                        var bypass = await db.MonitoringBypasses.SingleOrDefaultAsync(row => row.TenantId == request.TenantId && row.BypassId == request.EntityId, cancellation).ConfigureAwait(false);
                        if (bypass is null) return null;
                        db.MonitoringBypasses.Remove(bypass); after = before with { Bypasses = before.Bypasses.Where(item => item.BypassId != request.EntityId).ToImmutableArray() };
                        break;
                }
                await ReconcileConfigurationSeriesAsync(db, before, after, eligible, request.OperatorId, request.Reason, null, cancellation).ConfigureAwait(false);
                return after;
            }, ct);
    }

    private async Task<MonitoringConfigurationWriteResult> MutateConfigurationAsync(int tenant, ulong expectedRevision, Guid operatorId, string reason,
        string kind, Guid entity, string operation,
        Func<OrchestratorDbContext, MonitoringConfigurationSnapshot, ImmutableArray<Guid>, CancellationToken, Task<MonitoringConfigurationSnapshot?>> mutate, CancellationToken ct)
    {
        RequireTenant(tenant);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await LockConfigurationAsync(db, tenant, ct).ConfigureAwait(false);
        var before = await ReadConfigurationAsync(db, tenant, row.Revision, ct).ConfigureAwait(false);
        if (row.Revision != expectedRevision) return new(MonitoringConfigurationWriteDisposition.Conflict, before);
        var after = await mutate(db, before, await EligibleAsync(db, tenant, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (after is null) return new(MonitoringConfigurationWriteDisposition.NotFound, before);
        row.Revision = checked(row.Revision + 1);
        after = after with { Revision = row.Revision, GeneratedAtUtc = timeProvider.GetUtcNow() };
        RequireConfigurationSize(after);
        await EnforceHistoryCapacityAsync(db, tenant, 0, 1, 0, ct).ConfigureAwait(false);
        db.MonitoringAudits.Add(new() { AuditId = Guid.NewGuid(), TenantId = tenant, EntityKind = kind, EntityId = entity, Operation = operation,
            OperatorId = operatorId, Reason = reason, AtUtc = timeProvider.GetUtcNow(), ConfigurationRevision = row.Revision, DetailsJson = Serialize(new { BeforeRevision = expectedRevision, EntityId = entity }) });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(MonitoringConfigurationWriteDisposition.Stored, after);
    }

    private async Task<MonitoringConfigurationSnapshot> ReadConfigurationAsync(OrchestratorDbContext db, int tenant, ulong revision, CancellationToken ct)
    {
        var rules = await db.MonitoringRules.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.RuleId).Take(MonitoringLimits.MaximumRulesPerTenant + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        var groups = await db.MonitoringGroups.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.GroupId).Take(MonitoringLimits.MaximumGroupsPerTenant + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        var bypasses = await db.MonitoringBypasses.AsNoTracking().Where(row => row.TenantId == tenant).OrderBy(row => row.BypassId).Take(MonitoringLimits.MaximumBypassesPerEvaluation + 1).Select(row => row.DefinitionJson).ToListAsync(ct).ConfigureAwait(false);
        if (rules.Count > MonitoringLimits.MaximumRulesPerTenant || groups.Count > MonitoringLimits.MaximumGroupsPerTenant || bypasses.Count > MonitoringLimits.MaximumBypassesPerEvaluation)
            throw new InvalidOperationException("configuration_capacity_exceeded");
        var result = new MonitoringConfigurationSnapshot(tenant, revision, rules.Select(Deserialize<MonitoringRuleDto>).ToImmutableArray(), groups.Select(Deserialize<MonitoringGroupDto>).ToImmutableArray(), bypasses.Select(Deserialize<MonitoringBypassDto>).ToImmutableArray(), timeProvider.GetUtcNow());
        RequireConfigurationSize(result);
        return result;
    }
    private static void RequireConfigurationSize(MonitoringConfigurationSnapshot configuration)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions).Length > MaximumAggregateBytes - 1024)
            throw new InvalidOperationException("monitoring_configuration_size_exceeded");
    }
    private static void ValidateTargets(MonitoringRuleDto rule, ImmutableArray<MonitoringGroupDto> groups, ImmutableArray<Guid> eligible, bool allowIneligibleExisting = false)
    {
        if (rule.Targets.GroupIds.Any(id => !groups.Any(group => group.GroupId == id && group.TenantId == rule.TenantId))) throw new ArgumentException("unknown_or_foreign_group");
        if (!allowIneligibleExisting && (rule.Targets.AgentIds.Any(id => !eligible.Contains(id)) ||
            groups.Where(group => rule.Targets.GroupIds.Contains(group.GroupId)).Any(group => group.AgentIds.Any(id => !eligible.Contains(id)))))
            throw new ArgumentException("unknown_or_ineligible_target");
        var count = rule.Targets.Mode == MonitoringTargetMode.AllEligible ? eligible.Length : rule.Targets.AgentIds.Concat(groups.Where(group => rule.Targets.GroupIds.Contains(group.GroupId)).SelectMany(group => group.AgentIds)).Distinct().Count();
        if (count == 0 || count > MonitoringLimits.MaximumTargetClients) throw new ArgumentException("no_eligible_targets");
    }
    private static bool Applicable(MonitoringRuleDto rule, Guid agent, MonitoringConfigurationSnapshot config, ImmutableArray<Guid> eligible) =>
        rule.Enabled && eligible.Contains(agent) && (rule.Targets.Mode == MonitoringTargetMode.AllEligible || rule.Targets.AgentIds.Contains(agent) ||
            config.Groups.Any(group => rule.Targets.GroupIds.Contains(group.GroupId) && group.AgentIds.Contains(agent)));

    private async Task ReconcileConfigurationSeriesAsync(OrchestratorDbContext db, MonitoringConfigurationSnapshot before,
        MonitoringConfigurationSnapshot after, ImmutableArray<Guid> eligible, Guid operatorId, string reason, Guid? resetRule, CancellationToken ct)
    {
        // The tenant lock prevents ingress and claims observing a half-applied configuration transition.
        var changedGroups = before.Groups.Concat(after.Groups).Select(group => group.GroupId).Distinct()
            .Where(id => Serialize(before.Groups.SingleOrDefault(group => group.GroupId == id)) != Serialize(after.Groups.SingleOrDefault(group => group.GroupId == id))).ToHashSet();
        var changedBypasses = before.Bypasses.Concat(after.Bypasses).GroupBy(bypass => bypass.BypassId)
            .Where(group => group.Count() != 2 || Serialize(group.First()) != Serialize(group.Last())).Select(group => group.First()).ToArray();
        var affectedRules = before.Rules.Concat(after.Rules).Select(rule => rule.RuleId).Distinct().Where(id =>
        {
            var old = before.Rules.SingleOrDefault(rule => rule.RuleId == id); var updated = after.Rules.SingleOrDefault(rule => rule.RuleId == id);
            return Serialize(old) != Serialize(updated) || (updated ?? old)!.Targets.GroupIds.Any(changedGroups.Contains) ||
                changedBypasses.Any(bypass => bypass.RuleId is null || bypass.RuleId == id) ||
                after.Bypasses.Any(bypass => (bypass.RuleId is null || bypass.RuleId == id) && bypass.GroupId is { } group && changedGroups.Contains(group));
        }).ToArray();
        if (affectedRules.Length == 0) return;
        var affected = db.MonitoringSeries.Where(row => row.TenantId == before.TenantId && affectedRules.Contains(row.RuleId));
        if (await affected.CountAsync(ct).ConfigureAwait(false) > MaximumSeriesPerTenant) throw new InvalidOperationException("monitoring_mutation_capacity_exceeded");
        SeriesCursor? last = null;
        while (true)
        {
            var batch = last is null ? affected : db.MonitoringSeries.FromSqlInterpolated($"""
                SELECT * FROM "MonitoringSeries" WHERE "TenantId" = {before.TenantId}
                AND ("RuleId", "AgentId", "ResourceKey") > ({last.RuleId}, {last.AgentId}, {last.ResourceKey})
                """).Where(row => affectedRules.Contains(row.RuleId));
            var rows = await batch.OrderBy(row => row.RuleId).ThenBy(row => row.AgentId).ThenBy(row => row.ResourceKey).Take(ReconciliationBatchSize).ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
            var state = ReadState(row);
            var oldRule = before.Rules.SingleOrDefault(rule => rule.RuleId == row.RuleId);
            if (oldRule is null) continue; // Deleted-rule history stays readable but cannot dispatch.
            var rule = after.Rules.SingleOrDefault(rule => rule.RuleId == row.RuleId);
            MonitoringEvaluationResult? result = null;
            if (rule is null) result = _evaluator.SetApplicability(state, oldRule with { Enabled = false }, false, operatorId, reason);
            else if (resetRule == rule.RuleId) result = _evaluator.ResetDefinition(state, rule, MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow, operatorId, reason);
            else
            {
                var wasApplicable = Applicable(oldRule, state.Series.AgentId, before, eligible);
                var applicable = Applicable(rule, state.Series.AgentId, after, eligible);
                if (!applicable && (state.Phase is not (MonitoringPhase.Suspended or MonitoringPhase.NotApplicable) || state.Occurrence is { EndedAtUtc: null }))
                    result = _evaluator.SetApplicability(state, rule, false, operatorId, reason);
                else if (applicable && (!wasApplicable || state.Phase is MonitoringPhase.Suspended or MonitoringPhase.NotApplicable))
                    result = _evaluator.SetApplicability(state, rule, true, operatorId, reason);
                else if (applicable)
                {
                    // Configuration changes may update suppression, but cannot replay old evidence into a hold.
                    var fence = await directory.GetCurrentEvidenceAsync(new(state.Series.TenantId, state.Series.AgentId), ct).ConfigureAwait(false);
                    result = _evaluator.Refresh(state, rule, after.Bypasses, fence?.EvidenceStreamId, after.Groups);
                }
            }
                if (result is not null && result.State.StateRevision != result.ExpectedStateRevision)
                    await ApplyEvaluationAsync(db, result, checked(before.Revision + 1), ct).ConfigureAwait(false);
            }
            last = new(before.TenantId, rows[^1].RuleId, rows[^1].AgentId, rows[^1].ResourceKey);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // Flush inside the same transaction and release tracked JSONs; no partial result is visible before its final commit.
            foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.Entity is MonitoringSeriesRecord or MonitoringOccurrenceRecord or
                MonitoringEventRecord or MonitoringFlowOutboxRecord or MonitoringAuditRecord or OutboxMessage).ToArray()) entry.State = EntityState.Detached;
        }
    }
}
