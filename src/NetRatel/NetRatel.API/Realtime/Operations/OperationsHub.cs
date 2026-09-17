using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Realtime.Operations;

/// <summary>Authorized live-log fan-out for the operations UI.</summary>
[Authorize(Policy = "Operator")]
public sealed class OperationsHub(
    OperationsLogSubscriptionRegistry subscriptions,
    IOperationsLogTenantAuthorizer tenantAuthorizer,
    IAgentLogGatewaySessionRegistry logSessions,
    IAgentLogGatewayQueryDispatcher logQueries,
    ILogger<OperationsHub> logger) : Hub
{
    public const string HubPath = "/hubs/operations";
    private static readonly TimeSpan FollowStopGracePeriod = TimeSpan.FromSeconds(2);

    public override async Task OnConnectedAsync()
    {
        if (Context.User is null || !subscriptions.TryRegister(Context.ConnectionId, Context.User))
        {
            logger.LogWarning("Operations log hub connection was rejected by bounded registration.");
            Context.Abort();
            throw new HubException("Operations hub connection capacity or identity validation failed.");
        }

        try
        {
            await base.OnConnectedAsync().ConfigureAwait(false);
        }
        catch
        {
            subscriptions.RemoveConnection(Context.ConnectionId);
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var followStop in subscriptions.RemoveConnection(Context.ConnectionId)) ScheduleFollowStop(followStop);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public async Task<GatewayLogPageDto> SubscribeLogs(int tenantId, string agentId, string sourceId, string? cursor = null, GatewayLogQueryFilters? filters = null)
    {
        var client = ResolveClient(tenantId, agentId);
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 || !IsValid(filters) ||
            !logSessions.GetSources(client).Any(source => source.Available && string.Equals(source.SourceId, sourceId, StringComparison.Ordinal)))
        {
            throw new HubException("The requested log source is unavailable.");
        }

        var groupName = GroupName(client);
        if (!subscriptions.TryAdd(Context.ConnectionId, groupName, client, sourceId, out var joinGroup, out _))
        {
            throw new HubException("Operations log subscription limit reached.");
        }

        try
        {
            if (joinGroup) await Groups.AddToGroupAsync(Context.ConnectionId, groupName, Context.ConnectionAborted).ConfigureAwait(false);
            return await logQueries.QueryAsync(client, ToRequest(sourceId, cursor, filters),
                // The agent owns one follow per source. Reasserting StartLive
                // makes a reconnect or a late disconnect self-healing while
                // registry reference counts prevent one operator from stopping
                // a source still viewed by another.
                NetRatel.AgentGateway.Contracts.V1.LogQueryOperation.StartLive,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (subscriptions.Remove(Context.ConnectionId, groupName, client, sourceId, out var leaveGroup, out var stopFollow))
            {
                try
                {
                    if (leaveGroup) await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    if (stopFollow is not null) ScheduleFollowStop(stopFollow);
                }
            }
            logger.LogWarning("Operations log subscription failed. FailureKind={FailureKind}", exception.GetType().Name);
            throw;
        }
    }

    public async Task UnsubscribeLogs(int tenantId, string agentId, string sourceId)
    {
        var client = ResolveClient(tenantId, agentId);
        var groupName = GroupName(client);
        var removed = subscriptions.Remove(Context.ConnectionId, groupName, client, sourceId, out var leaveGroup, out var stopFollow);
        try
        {
            if (removed && leaveGroup)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName, Context.ConnectionAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            if (stopFollow is not null) ScheduleFollowStop(stopFollow);
        }
    }

    private void ScheduleFollowStop(OperationsLogFollowLease followStop) =>
        _ = StopFollowAfterGracePeriodAsync(followStop);

    private async Task StopFollowAfterGracePeriodAsync(OperationsLogFollowLease followStop)
    {
        try
        {
            await Task.Delay(FollowStopGracePeriod).ConfigureAwait(false);
            if (!subscriptions.TryClaimFollowStop(followStop)) return;

            var subscription = followStop.Subscription;
            await logQueries.QueryAsync(subscription.Client,
                new GatewayLogPageRequest(subscription.SourceId, null, 1, null, null, null, null, null),
                NetRatel.AgentGateway.Contracts.V1.LogQueryOperation.StopLive, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning("The agent log follow cleanup did not complete. FailureKind={FailureKind}", exception.GetType().Name);
        }
    }

    private ClientKey ResolveClient(int tenantId, string agentId)
    {
        if (Context.User is null || !tenantAuthorizer.IsAuthorized(Context.User, tenantId) ||
            !Guid.TryParse(agentId, out var parsedAgentId) || parsedAgentId == Guid.Empty)
        {
            throw new HubException("Tenant authorization failed.");
        }

        return new ClientKey(tenantId, parsedAgentId);
    }

    private static GatewayLogPageRequest ToRequest(string sourceId, string? cursor, GatewayLogQueryFilters? filters) => new(
        sourceId, cursor, 100, filters?.FromUtc, filters?.ToUtc, filters?.Severities, filters?.Prefixes, filters?.Text, filters?.Providers, filters?.EventIds, filters?.Categories);

    private static bool IsValid(GatewayLogQueryFilters? filters) => filters is null ||
        ((!filters.FromUtc.HasValue || !filters.ToUtc.HasValue || filters.FromUtc.Value <= filters.ToUtc.Value) &&
         (!filters.FromUtc.HasValue || !filters.ToUtc.HasValue || filters.ToUtc.Value - filters.FromUtc.Value <= TimeSpan.FromDays(31)) &&
         (filters.Severities?.Count ?? 0) <= 8 && (filters.Prefixes?.Count ?? 0) <= 32 &&
         (filters.Categories?.Count ?? 0) <= 32 && (filters.Providers?.Count ?? 0) <= 32 && (filters.EventIds?.Count ?? 0) <= 32 &&
         (filters.Text?.Length ?? 0) <= 512 &&
         filters.Categories?.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256) != false &&
         filters.Providers?.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256) != false &&
         filters.EventIds?.All(value => value >= 0) != false);

    public static string GroupName(ClientKey client) => $"operations:logs:{client.TenantId}:{client.AgentId:D}";
}
