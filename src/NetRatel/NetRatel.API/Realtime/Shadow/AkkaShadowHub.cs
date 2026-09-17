using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NetRatel.Application.Fanout;

namespace NetRatel.API.Realtime.Shadow;

/// <summary>
/// Development-only, read-only shadow diagnostics hub. Callers can subscribe
/// only through typed methods; the server derives every group name.
/// </summary>
[Authorize(Policy = "AkkaShadowAccess")]
public sealed class AkkaShadowHub(
    SignalRShadowSubscriptionRegistry subscriptions,
    IShadowTenantAuthorizer tenantAuthorizer,
    IShadowFanoutSnapshotSource snapshots) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User is null ||
            !subscriptions.TryRegisterConnection(Context.ConnectionId, Context.User))
        {
            Context.Abort();
            throw new HubException("Shadow connection capacity or identity validation failed.");
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
        subscriptions.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeTenant(int tenantId) =>
        SubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Tenant));

    public Task UnsubscribeTenant(int tenantId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Tenant));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeClient(int tenantId, string clientId) =>
        SubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Client, ClientId: clientId));

    public Task UnsubscribeClient(int tenantId, string clientId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Client, ClientId: clientId));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeTerminal(
        int tenantId,
        string clientId,
        string sessionId) =>
        SubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.Terminal,
            ClientId: clientId,
            SessionId: sessionId));

    public Task UnsubscribeTerminal(int tenantId, string clientId, string sessionId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.Terminal,
            ClientId: clientId,
            SessionId: sessionId));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeRemoteSupport(
        int tenantId,
        string clientId,
        string sessionId) =>
        SubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.RemoteSupport,
            ClientId: clientId,
            SessionId: sessionId));

    public Task UnsubscribeRemoteSupport(int tenantId, string clientId, string sessionId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.RemoteSupport,
            ClientId: clientId,
            SessionId: sessionId));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeFileBrowser(
        int tenantId,
        string clientId,
        string requestId) =>
        SubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.FileBrowser,
            ClientId: clientId,
            RequestId: requestId));

    public Task UnsubscribeFileBrowser(int tenantId, string clientId, string requestId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.FileBrowser,
            ClientId: clientId,
            RequestId: requestId));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeJob(int tenantId, ulong jobId) =>
        SubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Job, JobId: jobId));

    public Task UnsubscribeJob(int tenantId, ulong jobId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(tenantId, ShadowFanoutTargetScope.Job, JobId: jobId));

    public Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeCommand(int tenantId, string commandId) =>
        SubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.Command,
            CommandId: commandId));

    public Task UnsubscribeCommand(int tenantId, string commandId) =>
        UnsubscribeAsync(new ShadowFanoutTarget(
            tenantId,
            ShadowFanoutTargetScope.Command,
            CommandId: commandId));

    private async Task<IReadOnlyList<ShadowFanoutEnvelope>> SubscribeAsync(ShadowFanoutTarget target)
    {
        var groupName = ResolveAuthorizedGroup(target);
        var disposition = subscriptions.TryBeginAddSubscription(Context.ConnectionId, groupName);
        if (disposition == SubscriptionAdditionDisposition.Rejected)
        {
            throw new HubException("Shadow subscription limit reached.");
        }

        if (disposition == SubscriptionAdditionDisposition.AlreadyPresent)
        {
            return snapshots.GetSnapshots(target);
        }

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return snapshots.GetSnapshots(target);
        }
        catch
        {
            subscriptions.RollbackSubscription(Context.ConnectionId, groupName);
            throw;
        }
    }

    private async Task UnsubscribeAsync(ShadowFanoutTarget target)
    {
        var groupName = ResolveAuthorizedGroup(target);
        var disposition = subscriptions.TryBeginRemoveSubscription(Context.ConnectionId, groupName);
        if (disposition == SubscriptionRemovalDisposition.AlreadyAbsent)
        {
            return;
        }

        if (disposition == SubscriptionRemovalDisposition.Rejected)
        {
            throw new HubException("Shadow subscription rate limit reached.");
        }

        try
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        catch
        {
            subscriptions.CancelRemoveSubscription(Context.ConnectionId, groupName);
            throw;
        }

        subscriptions.CommitRemoveSubscription(Context.ConnectionId, groupName);
    }

    private string ResolveAuthorizedGroup(ShadowFanoutTarget target)
    {
        if (Context.User is null || !tenantAuthorizer.IsAuthorized(Context.User, target.TenantId))
        {
            subscriptions.RecordUnauthorizedSubscription();
            throw new HubException("Tenant authorization failed.");
        }

        if (!SignalRShadowGroupName.TryCreate(target, out var groupName))
        {
            throw new HubException("Shadow subscription identifiers are invalid.");
        }

        return groupName;
    }
}
