using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NetRatel.Application.Fanout;

namespace NetRatel.API.Realtime;

/// <summary>
/// Read-only Akka projection hub. Callers can subscribe only through typed
/// methods; the server derives every group name.
/// </summary>
[Authorize(Policy = "RealtimeAccess")]
public sealed class RealtimeHub(
    RealtimeSubscriptionRegistry subscriptions,
    IRealtimeTenantAuthorizer tenantAuthorizer,
    IRealtimeFanoutSnapshotSource snapshots) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User is null ||
            !subscriptions.TryRegisterConnection(Context.ConnectionId, Context.User))
        {
            Context.Abort();
            throw new HubException("Realtime connection capacity or identity validation failed.");
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

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeTenant(int tenantId) =>
        SubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Tenant));

    public Task UnsubscribeTenant(int tenantId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Tenant));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeClient(int tenantId, string clientId) =>
        SubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Client, ClientId: clientId));

    public Task UnsubscribeClient(int tenantId, string clientId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Client, ClientId: clientId));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeTerminal(
        int tenantId,
        string clientId,
        string sessionId) =>
        SubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.Terminal,
            ClientId: clientId,
            SessionId: sessionId));

    public Task UnsubscribeTerminal(int tenantId, string clientId, string sessionId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.Terminal,
            ClientId: clientId,
            SessionId: sessionId));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeRemoteSupport(
        int tenantId,
        string clientId,
        string sessionId) =>
        SubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.RemoteSupport,
            ClientId: clientId,
            SessionId: sessionId));

    public Task UnsubscribeRemoteSupport(int tenantId, string clientId, string sessionId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.RemoteSupport,
            ClientId: clientId,
            SessionId: sessionId));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeFileBrowser(
        int tenantId,
        string clientId,
        string requestId) =>
        SubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.FileBrowser,
            ClientId: clientId,
            RequestId: requestId));

    public Task UnsubscribeFileBrowser(int tenantId, string clientId, string requestId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.FileBrowser,
            ClientId: clientId,
            RequestId: requestId));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeJob(int tenantId, ulong jobId) =>
        SubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Job, JobId: jobId));

    public Task UnsubscribeJob(int tenantId, ulong jobId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(tenantId, RealtimeFanoutTargetScope.Job, JobId: jobId));

    public Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeCommand(int tenantId, string commandId) =>
        SubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.Command,
            CommandId: commandId));

    public Task UnsubscribeCommand(int tenantId, string commandId) =>
        UnsubscribeAsync(new RealtimeFanoutTarget(
            tenantId,
            RealtimeFanoutTargetScope.Command,
            CommandId: commandId));

    private async Task<IReadOnlyList<RealtimeFanoutEnvelope>> SubscribeAsync(RealtimeFanoutTarget target)
    {
        var groupName = await ResolveAuthorizedGroupAsync(target).ConfigureAwait(false);
        var disposition = subscriptions.TryBeginAddSubscription(Context.ConnectionId, groupName);
        if (disposition == SubscriptionAdditionDisposition.Rejected)
        {
            throw new HubException("Realtime subscription limit reached.");
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

    private async Task UnsubscribeAsync(RealtimeFanoutTarget target)
    {
        var groupName = await ResolveAuthorizedGroupAsync(target).ConfigureAwait(false);
        var disposition = subscriptions.TryBeginRemoveSubscription(Context.ConnectionId, groupName);
        if (disposition == SubscriptionRemovalDisposition.AlreadyAbsent)
        {
            return;
        }

        if (disposition == SubscriptionRemovalDisposition.Rejected)
        {
            throw new HubException("Realtime subscription rate limit reached.");
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

    private async Task<string> ResolveAuthorizedGroupAsync(RealtimeFanoutTarget target)
    {
        if (Context.User is null ||
            !await tenantAuthorizer.IsAuthorizedAsync(Context.User, target.TenantId, Context.ConnectionAborted).ConfigureAwait(false))
        {
            subscriptions.RecordUnauthorizedSubscription();
            throw new HubException("Tenant authorization failed.");
        }

        if (!RealtimeGroupName.TryCreate(target, out var groupName))
        {
            throw new HubException("Realtime subscription identifiers are invalid.");
        }

        return groupName;
    }
}
