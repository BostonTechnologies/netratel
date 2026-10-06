using System.Collections.Immutable;
using System.Security.Claims;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Authorizes exact tenant/resource pairs before any monitoring read or audited write.</summary>
public sealed class MonitoringApiService(IMonitoringResourceAuthorizer authorization, IEffectiveAccessService access,
    IMonitoringTenantCatalog tenants, IMonitoringConfigurationStore configurations, IMonitoringRuntime runtime,
    IMonitoringClientDirectory directory, IAgentManagementService agents, IMonitoringPublishedFlowProvider flows,
    IClientServicesRouter services, IClientTelemetryRouter telemetry, IAgentTelemetryGatewaySessionRegistry sessions,
    MonitoringWatchPolicyReconciler watches, TimeProvider timeProvider, ILogger<MonitoringApiService> logger)
{
    public const int MaximumHttpBytes = 4 * 1024 * 1024;

    public async Task<ImmutableArray<MonitoringTenantDto>> GetTenantsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var scope = await access.GetAuthorizedTenantIdsAsync(user, NetRatelPermissions.MonitoringRead, cancellationToken).ConfigureAwait(false);
        if (scope is { Length: > 256 }) throw new MonitoringApiException(413, "monitoring_tenant_capacity_exceeded");
        var result = await tenants.GetAsync(scope, 256, cancellationToken).ConfigureAwait(false);
        var visible = ImmutableArray.CreateBuilder<MonitoringTenantDto>();
        foreach (var tenant in result)
            if (await AllowedAsync(user, NetRatelPermissions.MonitoringRead, new(tenant.TenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false)) visible.Add(tenant);
        return visible.ToImmutable();
    }

    public async Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var resource = new MonitoringResource(tenantId, MonitoringResourceKind.Tenant);
        var read = await AllowedAsync(user, NetRatelPermissions.MonitoringRead, resource, cancellationToken).ConfigureAwait(false);
        var manage = await AllowedAsync(user, NetRatelPermissions.MonitoringManage, resource, cancellationToken).ConfigureAwait(false);
        var ack = await AllowedAsync(user, NetRatelPermissions.MonitoringAcknowledge, resource, cancellationToken).ConfigureAwait(false);
        var clear = await AllowedAsync(user, NetRatelPermissions.MonitoringClear, resource, cancellationToken).ConfigureAwait(false);
        var bypass = await AllowedAsync(user, NetRatelPermissions.MonitoringBypass, resource, cancellationToken).ConfigureAwait(false);
        var all = await AllowedAsync(user, NetRatelPermissions.MonitoringAllTargets, resource, cancellationToken).ConfigureAwait(false);
        if (!(read || manage || ack || clear || bypass || all)) throw new MonitoringApiException(403, "monitoring_permission_required");
        return new(tenantId, read, manage, ack, clear, bypass, all);
    }

    public async Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        return Bounded(ToDto(await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false)));
    }

    public async Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, int maximumCount, string? cursor, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        RequirePage(maximumCount, cursor);
        var result = await runtime.ReadTenantAsync(tenantId, maximumCount, cursor, cancellationToken).ConfigureAwait(false);
        if (result.Items.Any(item => item.Series.TenantId != tenantId)) throw new InvalidOperationException("Monitoring series returned a different tenant.");
        return Bounded(result);
    }

    public async Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, int maximumCount, string? cursor, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        RequirePage(maximumCount, cursor);
        var result = await runtime.ReadTenantEventsAsync(tenantId, maximumCount, cursor, cancellationToken).ConfigureAwait(false);
        if (result.Items.Any(item => item.Series.TenantId != tenantId)) throw new InvalidOperationException("Monitoring events returned a different tenant.");
        return Bounded(result);
    }

    public async Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        var result = await runtime.ReadTenantSummaryAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (result.TenantId != tenantId) throw new InvalidOperationException("Monitoring summary returned a different tenant.");
        return result;
    }

    public async Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(int tenantId, Guid agentId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenantId, MonitoringResourceKind.Agent, agentId, agentId), cancellationToken).ConfigureAwait(false);
        await RequireAgentAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        var result = await runtime.GetClientAsync(new(tenantId, agentId), cancellationToken).ConfigureAwait(false);
        if (result.Any(item => item.Series.TenantId != tenantId || item.Series.AgentId != agentId)) throw new InvalidOperationException("Monitoring series returned a different client.");
        return Bounded(result);
    }

    public async Task<ImmutableArray<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireReadOrManageAsync(user, tenantId, cancellationToken).ConfigureAwait(false);
        if (!await access.AuthorizeAsync(user, "flow.read", tenantId, cancellationToken).ConfigureAwait(false) ||
            !await access.AuthorizeAsync(user, "flow.execute", tenantId, cancellationToken).ConfigureAwait(false)) return [];
        return Bounded(await flows.ListPublishedAsync(tenantId, MonitoringLimits.MaximumRowsPerRead, cancellationToken).ConfigureAwait(false));
    }

    public async Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, int maximumCount, string? cursor, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireReadOrManageAsync(user, tenantId, cancellationToken).ConfigureAwait(false);
        RequirePage(maximumCount, cursor);
        var ids = (await directory.GetEligibleAgentsAsync(tenantId, cancellationToken).ConfigureAwait(false)).Order().ToArray();
        var index = 0;
        if (cursor is not null)
        {
            if (!Guid.TryParseExact(cursor, "N", out var previous)) throw new MonitoringApiException(400, "invalid_monitoring_cursor");
            index = Array.FindIndex(ids, id => id == previous) + 1;
            if (index == 0) throw new MonitoringApiException(400, "invalid_monitoring_cursor");
        }
        var selected = ids.Skip(index).Take(maximumCount).ToArray();
        var result = ImmutableArray.CreateBuilder<MonitoringClientDto>();
        foreach (var id in selected)
        {
            var agent = await RequireAgentAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
            var evidence = await GetServiceSupportAsync(new(tenantId, id), cancellationToken).ConfigureAwait(false);
            result.Add(new(id, agent.DisplayName, evidence.Platform, evidence.Support, evidence.Code));
        }
        return new(result.ToImmutable(), index + selected.Length < ids.Length ? selected[^1].ToString("N") : null, ids.Length);
    }

    public async Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringManage, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        var configuration = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var ids = await ResolveTargetsAsync(tenantId, request.Targets, configuration, user, NetRatelPermissions.MonitoringManage, cancellationToken).ConfigureAwait(false);
        if (request.Condition is not null)
            MonitoringContractValidator.RequireRule(new(tenantId, Guid.NewGuid(), 1, 1, "Preview", true, MonitoringSeverity.Warning,
                request.Targets, request.Condition, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)));
        var details = ImmutableArray.CreateBuilder<MonitoringTargetPreviewEntryDto>();
        foreach (var id in ids.Take(100))
        {
            var agent = await RequireAgentAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
            var support = request.Condition?.Kind == MonitoringMetricKind.ServiceExpectedState
                ? await GetServiceSupportAsync(new(tenantId, id), cancellationToken).ConfigureAwait(false)
                : await GetMetricSupportAsync(new(tenantId, id), request.Condition, cancellationToken).ConfigureAwait(false);
            if (request.Condition?.ServicePlatform is { } selected && support.Platform is { } actual && selected != actual)
                support = (MonitoringTargetSupport.Unsupported, "platform-mismatch", support.At, actual);
            details.Add(new(id, agent.DisplayName, support.Support, support.Code, support.At));
        }
        return new(ids, configuration.Revision, details.ToImmutable(), ids.Length, ids.Length > details.Count);
    }

    public async Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, Guid ruleId, MonitoringRuleWriteDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringManage, new(tenantId, MonitoringResourceKind.Rule, ruleId), cancellationToken).ConfigureAwait(false);
        var operatorId = RequireOperator(user, request.Reason);
        var current = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (request.Rule is null || request.Rule.Condition is null || request.Rule.Targets is null || request.Rule.TenantId != tenantId || request.Rule.RuleId != ruleId)
            throw new MonitoringApiException(400, "monitoring_rule_route_mismatch");
        var existing = current.Rules.SingleOrDefault(rule => rule.RuleId == ruleId);
        var rule = request.Rule with
        {
            Condition = request.Rule.Condition with
            {
                ExpectedServiceStates = request.Rule.Condition.ExpectedServiceStates.IsDefault ? [] : request.Rule.Condition.ExpectedServiceStates
            },
            ExecutionPrincipalId = request.Rule.PublishedFlowVersionId is null ? null : existing?.PublishedFlowVersionId == request.Rule.PublishedFlowVersionId
                ? existing.ExecutionPrincipalId : user.FindFirstValue("netratel_principal_id"),
            ExecutionCredentialId = request.Rule.PublishedFlowVersionId is null ? null : existing?.PublishedFlowVersionId == request.Rule.PublishedFlowVersionId
                ? existing.ExecutionCredentialId : user.FindFirstValue("netratel_integration_credential_id")
        };
        MonitoringContractValidator.RequireRule(rule);
        if (request.ResetPolicy is { } reset && !Enum.IsDefined(reset)) throw new MonitoringApiException(400, "invalid_monitoring_reset_policy");
        if (rule.Revision != (existing is null ? 1UL : checked(existing.Revision + 1))) throw new MonitoringApiException(409, "monitoring_rule_revision_conflict");
        var changed = existing is not null && MonitoringContractValidator.EvaluationFingerprint(existing) != MonitoringContractValidator.EvaluationFingerprint(rule);
        if ((changed && request.ResetPolicy != MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow) ||
            rule.EvaluationRevision != (changed ? checked(existing!.EvaluationRevision + 1) : existing?.EvaluationRevision ?? 1))
            throw new MonitoringApiException(400, "monitoring_reset_policy_required");
        var minimumFreshness = rule.Condition.Kind switch
        {
            MonitoringMetricKind.CpuUsagePercent => TimeSpan.FromSeconds(10),
            MonitoringMetricKind.ServiceExpectedState => TimeSpan.FromSeconds(ClientServicesLimits.DefaultWatchIntervalSeconds + 5),
            _ => TimeSpan.FromSeconds(65)
        };
        if (rule.FreshnessBudget < minimumFreshness) throw new MonitoringApiException(400, "monitoring_freshness_below_collection_cadence");
        await ResolveTargetsAsync(tenantId, rule.Targets, current, user, NetRatelPermissions.MonitoringManage, cancellationToken).ConfigureAwait(false);
        if (rule.PublishedFlowVersionId is Guid flowId && existing?.PublishedFlowVersionId != flowId)
        {
            await RequireFlowSelectionAuthorityAsync(user, tenantId, cancellationToken).ConfigureAwait(false);
            if (!await flows.IsPublishedAsync(tenantId, flowId, cancellationToken).ConfigureAwait(false))
                throw new MonitoringApiException(400, "published_flow_unavailable");
        }
        var candidate = current with { Rules = current.Rules.Where(item => item.RuleId != ruleId).Append(rule).ToImmutableArray() };
        await ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var saved = await configurations.SaveRuleAsync(new(rule, request.ExpectedConfigurationRevision, operatorId, request.Reason, request.ResetPolicy), cancellationToken).ConfigureAwait(false);
        return await FinishConfigurationWriteAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, Guid groupId, MonitoringGroupWriteDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringManage, new(tenantId, MonitoringResourceKind.Group, groupId), cancellationToken).ConfigureAwait(false);
        var operatorId = RequireOperator(user, request.Reason);
        var group = request.Group;
        if (group is null || group.TenantId != tenantId || group.GroupId != groupId || group.Name is null || group.Name.Length > MonitoringLimits.MaximumNameLength ||
            group.AgentIds.IsDefaultOrEmpty || group.AgentIds.Length > MonitoringLimits.MaximumTargetClients || group.AgentIds.Distinct().Count() != group.AgentIds.Length)
            throw new MonitoringApiException(400, "invalid_monitoring_group");
        MonitoringContractValidator.RequireReason(operatorId, group.Name);
        var current = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var existing = current.Groups.SingleOrDefault(item => item.GroupId == groupId);
        if (group.Revision != (existing is null ? 1UL : checked(existing.Revision + 1))) throw new MonitoringApiException(409, "monitoring_group_revision_conflict");
        await ResolveTargetsAsync(tenantId, new(MonitoringTargetMode.Selected, group.AgentIds, []), current, user, NetRatelPermissions.MonitoringManage, cancellationToken).ConfigureAwait(false);
        var candidate = current with { Groups = current.Groups.Where(item => item.GroupId != groupId).Append(group).ToImmutableArray() };
        await ValidateCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var result = await configurations.SaveGroupAsync(new(group, request.ExpectedConfigurationRevision, operatorId, request.Reason), cancellationToken).ConfigureAwait(false);
        return await FinishConfigurationWriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await RequireAsync(user, NetRatelPermissions.MonitoringBypass, new(tenantId, MonitoringResourceKind.Bypass, bypassId), cancellationToken).ConfigureAwait(false);
        var operatorId = RequireOperator(user, request.Reason);
        var current = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        await RequireBypassScopeAsync(current, request.RuleId, request.AgentId, request.GroupId, user, cancellationToken).ConfigureAwait(false);
        var bypass = new MonitoringBypassDto(bypassId, tenantId, request.RuleId, request.AgentId, request.ResourceKey, operatorId,
            request.Reason, timeProvider.GetUtcNow(), request.ExpiresAtUtc, request.GroupId);
        if (!MonitoringContractValidator.TryValidateBypass(bypass, out _)) throw new MonitoringApiException(400, "invalid_monitoring_bypass");
        var result = await configurations.SaveBypassAsync(new(bypass, request.ExpectedConfigurationRevision), cancellationToken).ConfigureAwait(false);
        return await FinishConfigurationWriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, Guid entityId, MonitoringResourceKind kind, MonitoringDeleteDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var permission = kind == MonitoringResourceKind.Bypass ? NetRatelPermissions.MonitoringBypass : NetRatelPermissions.MonitoringManage;
        await RequireAsync(user, permission, new(tenantId, kind, entityId), cancellationToken).ConfigureAwait(false);
        var operatorId = RequireOperator(user, request.Reason);
        var current = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (kind == MonitoringResourceKind.Bypass)
        {
            var bypass = current.Bypasses.SingleOrDefault(item => item.BypassId == entityId) ?? throw new MonitoringApiException(404, "monitoring_resource_not_found");
            await RequireBypassScopeAsync(current, bypass.RuleId, bypass.AgentId, bypass.GroupId, user, cancellationToken).ConfigureAwait(false);
        }
        else if ((kind == MonitoringResourceKind.Rule && current.Rules.All(item => item.RuleId != entityId)) ||
                 (kind == MonitoringResourceKind.Group && current.Groups.All(item => item.GroupId != entityId)))
            throw new MonitoringApiException(404, "monitoring_resource_not_found");
        var command = new MonitoringConfigurationDeleteRequest(tenantId, entityId, request.ExpectedConfigurationRevision, operatorId, request.Reason);
        var result = kind switch
        {
            MonitoringResourceKind.Rule => await configurations.DeleteRuleAsync(command, cancellationToken).ConfigureAwait(false),
            MonitoringResourceKind.Group => await configurations.DeleteGroupAsync(command, cancellationToken).ConfigureAwait(false),
            MonitoringResourceKind.Bypass => await configurations.DeleteBypassAsync(command, cancellationToken).ConfigureAwait(false),
            _ => throw new MonitoringApiException(400, "invalid_monitoring_resource")
        };
        return await FinishConfigurationWriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MonitoringSeriesState> ActAsync(int tenantId, Guid agentId, Guid ruleId, string resourceKey,
        MonitoringOperatorActionDto request, bool clear, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var permission = clear ? NetRatelPermissions.MonitoringClear : NetRatelPermissions.MonitoringAcknowledge;
        await RequireAsync(user, permission, new(tenantId, MonitoringResourceKind.Series, ruleId, agentId, resourceKey), cancellationToken).ConfigureAwait(false);
        await RequireAgentAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        var operatorId = RequireOperator(user, request.Reason);
        var configuration = await GetConfigurationAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (configuration.Rules.All(rule => rule.RuleId != ruleId)) throw new MonitoringApiException(404, "monitoring_resource_not_found");
        if (string.IsNullOrWhiteSpace(resourceKey) || resourceKey.Length > MonitoringLimits.MaximumResourceKeyLength || resourceKey.Any(char.IsControl) || request.OccurrenceId == Guid.Empty)
            throw new MonitoringApiException(400, "invalid_monitoring_series");
        var command = new MonitoringOperatorCommand(new(tenantId, ruleId, agentId, resourceKey), request.OccurrenceId, operatorId, request.Reason, request.ExpectedStateRevision);
        var result = clear ? await runtime.ClearAsync(command, cancellationToken).ConfigureAwait(false) : await runtime.AcknowledgeAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Disposition != MonitoringStoreWriteDisposition.Stored || result.State is null) throw new MonitoringApiException(409, "monitoring_series_conflict");
        if (result.State.Series != command.Series) throw new InvalidOperationException("Monitoring command returned a different series.");
        return Bounded(result.State);
    }

    private async Task<MonitoringConfigurationDto> FinishConfigurationWriteAsync(MonitoringConfigurationWriteResult result, CancellationToken cancellationToken)
    {
        if (result.Disposition != MonitoringConfigurationWriteDisposition.Stored)
            throw new MonitoringApiException(result.Disposition == MonitoringConfigurationWriteDisposition.NotFound ? 404 : 409, "monitoring_configuration_conflict");
        var pending = false;
        try { pending = !await watches.ReconcileTenantAsync(result.Configuration.TenantId, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            pending = true;
            logger.LogWarning(exception, "Monitoring configuration was saved; its service watch reconciliation is pending.");
        }
        return Bounded(ToDto(result.Configuration) with { WatchPolicyUpdatePending = pending });
    }

    private async Task ValidateCandidateAsync(MonitoringConfigurationSnapshot candidate, CancellationToken cancellationToken)
    {
        if (candidate.Rules.Length > MonitoringLimits.MaximumRulesPerTenant || candidate.Groups.Length > MonitoringLimits.MaximumGroupsPerTenant)
            throw new MonitoringApiException(413, "monitoring_configuration_capacity_exceeded");
        Bounded(ToDto(candidate));
        foreach (var id in await directory.GetEligibleAgentsAsync(candidate.TenantId, cancellationToken).ConfigureAwait(false))
        {
            var names = MonitoringServiceWatchPolicySource.SelectNames(candidate, id, true);
            if (!ClientServicesCoordinator.IsValidPolicy(new(1, names, ClientServicesLimits.DefaultWatchIntervalSeconds,
                ClientServicesLimits.DefaultInventoryIntervalSeconds, timeProvider.GetUtcNow().Add(ClientServicesLimits.MaximumPolicyLifetime)), timeProvider.GetUtcNow()))
                throw new MonitoringApiException(400, "service_watch_capacity_exceeded");
        }
    }

    private async Task<ImmutableArray<Guid>> ResolveTargetsAsync(int tenantId, MonitoringTargetSelectionDto targets,
        MonitoringConfigurationSnapshot configuration, ClaimsPrincipal user, string permission, CancellationToken cancellationToken)
    {
        if (targets is null || !MonitoringContractValidator.TryValidateTargets(targets, out _)) throw new MonitoringApiException(400, "invalid_monitoring_targets");
        if (targets.Mode == MonitoringTargetMode.AllEligible)
            await RequireAsync(user, NetRatelPermissions.MonitoringAllTargets, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
        foreach (var groupId in targets.GroupIds)
            await RequireAsync(user, permission, new(tenantId, MonitoringResourceKind.Group, groupId), cancellationToken).ConfigureAwait(false);
        var eligible = await directory.GetEligibleAgentsAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var authorized = new HashSet<Guid>();
        foreach (var id in eligible)
            if (await AllowedAsync(user, permission, new(tenantId, MonitoringResourceKind.Agent, id, id), cancellationToken).ConfigureAwait(false)) authorized.Add(id);
        return MonitoringTargetResolver.Resolve(tenantId, targets, eligible.ToDictionary(id => id, _ => tenantId),
            configuration.Groups.ToDictionary(group => group.GroupId), authorized);
    }

    private async Task RequireBypassScopeAsync(MonitoringConfigurationSnapshot configuration, Guid? ruleId, Guid? agentId,
        Guid? groupId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var tenantId = configuration.TenantId;
        if (ruleId is Guid rule)
        {
            await RequireAsync(user, NetRatelPermissions.MonitoringBypass, new(tenantId, MonitoringResourceKind.Rule, rule), cancellationToken).ConfigureAwait(false);
            if (configuration.Rules.All(item => item.RuleId != rule)) throw new MonitoringApiException(404, "monitoring_resource_not_found");
        }
        if (agentId is Guid agent)
        {
            await RequireAsync(user, NetRatelPermissions.MonitoringBypass, new(tenantId, MonitoringResourceKind.Agent, agent, agent), cancellationToken).ConfigureAwait(false);
            await RequireAgentAsync(tenantId, agent, cancellationToken).ConfigureAwait(false);
        }
        if (groupId is Guid group)
        {
            await RequireAsync(user, NetRatelPermissions.MonitoringBypass, new(tenantId, MonitoringResourceKind.Group, group), cancellationToken).ConfigureAwait(false);
            var selected = configuration.Groups.SingleOrDefault(item => item.GroupId == group) ?? throw new MonitoringApiException(404, "monitoring_resource_not_found");
            await ResolveTargetsAsync(tenantId, new(MonitoringTargetMode.Selected, selected.AgentIds, []), configuration, user, NetRatelPermissions.MonitoringBypass, cancellationToken).ConfigureAwait(false);
        }
        if (ruleId is null && agentId is null && groupId is null)
            await RequireAsync(user, NetRatelPermissions.MonitoringAllTargets, new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false);
    }

    private async Task<(MonitoringTargetSupport Support, string Code, DateTimeOffset? At, ClientServicePlatform? Platform)> GetServiceSupportAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var state = await services.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (state.Client != client) throw new InvalidOperationException("Services projection returned a different client.");
        var platforms = state.LastCompleteInventory?.Services.Select(item => item.Platform).Distinct().ToArray() ?? [];
        var platform = platforms.Length == 1 ? platforms[0] : (ClientServicePlatform?)null;
        var active = sessions.GetStatus(client);
        if (active.Connected && !active.SupportsServices) return (MonitoringTargetSupport.Unsupported, "capability-not-negotiated", null, platform);
        if (state.LatestAttempt?.Status == ServiceCollectionStatus.Unsupported)
            return (MonitoringTargetSupport.Unsupported, "collection-unsupported", state.LatestAttempt.ObservedAtUtc, platform);
        if (state.LastCompleteInventory is { } complete)
            return (MonitoringTargetSupport.Supported, "cached-service-evidence", complete.ObservedAtUtc, platform);
        return (MonitoringTargetSupport.Unknown, "evidence-unavailable", null, platform);
    }

    private async Task<(MonitoringTargetSupport Support, string Code, DateTimeOffset? At, ClientServicePlatform? Platform)> GetMetricSupportAsync(ClientKey client, MonitoringConditionDto? condition, CancellationToken cancellationToken)
    {
        var state = await telemetry.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (state.Client != client) throw new InvalidOperationException("Telemetry projection returned a different client.");
        var known = condition?.Kind == MonitoringMetricKind.CpuUsagePercent ? state.Latest?.Cpu is not null : state.Latest?.Disks.Count > 0;
        return (known ? MonitoringTargetSupport.Supported : MonitoringTargetSupport.Unknown,
            known ? "cached-metric-evidence" : "evidence-unavailable", state.Latest?.ObservedAtUtc, null);
    }

    private async Task<AgentDetailDto> RequireAgentAsync(int tenantId, Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await agents.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null || agent.TenantId != tenantId || agent.AgentId != agentId || agent.DeletedAtUtc is not null)
            throw new MonitoringApiException(404, "monitoring_resource_not_found");
        return agent;
    }

    private async Task<MonitoringConfigurationSnapshot> GetConfigurationAsync(int tenantId, CancellationToken cancellationToken)
    {
        var configuration = await configurations.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (configuration.TenantId != tenantId || configuration.Rules.Any(rule => rule.TenantId != tenantId) || configuration.Groups.Any(group => group.TenantId != tenantId) || configuration.Bypasses.Any(bypass => bypass.TenantId != tenantId))
            throw new InvalidOperationException("Monitoring configuration returned a different tenant.");
        return configuration;
    }

    private Task<bool> AllowedAsync(ClaimsPrincipal user, string permission, MonitoringResource resource, CancellationToken cancellationToken) => authorization.AuthorizeAsync(user, permission, resource, cancellationToken);

    private async Task RequireAsync(ClaimsPrincipal user, string permission, MonitoringResource resource, CancellationToken cancellationToken)
    {
        if (!await AllowedAsync(user, permission, resource, cancellationToken).ConfigureAwait(false)) throw new MonitoringApiException(403, "monitoring_permission_required");
    }

    private async Task RequireFlowSelectionAuthorityAsync(ClaimsPrincipal user, int tenantId, CancellationToken cancellationToken)
    {
        if (!await access.AuthorizeAsync(user, "flow.read", tenantId, cancellationToken).ConfigureAwait(false) ||
            !await access.AuthorizeAsync(user, "flow.execute", tenantId, cancellationToken).ConfigureAwait(false))
            throw new MonitoringApiException(403, "flow_selection_permission_required");
    }

    private async Task RequireReadOrManageAsync(ClaimsPrincipal user, int tenantId, CancellationToken cancellationToken)
    {
        var resource = new MonitoringResource(tenantId, MonitoringResourceKind.Tenant);
        if (!await AllowedAsync(user, NetRatelPermissions.MonitoringRead, resource, cancellationToken).ConfigureAwait(false) &&
            !await AllowedAsync(user, NetRatelPermissions.MonitoringManage, resource, cancellationToken).ConfigureAwait(false)) throw new MonitoringApiException(403, "monitoring_permission_required");
    }

    private static Guid RequireOperator(ClaimsPrincipal user, string reason)
    {
        if (!Guid.TryParse(user.FindFirstValue("netratel_principal_id"), out var operatorId) || operatorId == Guid.Empty) throw new MonitoringApiException(401, "monitoring_operator_identity_required");
        MonitoringContractValidator.RequireReason(operatorId, reason);
        return operatorId;
    }

    private static void RequirePage(int maximumCount, string? cursor)
    {
        if (maximumCount is < 1 or > MonitoringLimits.MaximumRowsPerRead || cursor?.Length > 256) throw new MonitoringApiException(400, "invalid_monitoring_page");
    }

    private static MonitoringConfigurationDto ToDto(MonitoringConfigurationSnapshot configuration) => new(configuration.TenantId,
        configuration.Revision, configuration.Rules, configuration.Groups, configuration.Bypasses, configuration.GeneratedAtUtc);

    public static T Bounded<T>(T value) => MonitoringHttpSerialization.NormalizeBounded(value, MaximumHttpBytes);
}
