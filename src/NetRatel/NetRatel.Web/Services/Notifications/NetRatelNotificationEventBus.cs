namespace NetRatel.Web.Services.Notifications;

public sealed class NetRatelNotificationEventBus
{
    public event Action<int>? OnNotificationsRead;

    public Task NotifyReadAsync(int count)
    {
        if (count > 0)
            OnNotificationsRead?.Invoke(count);

        return Task.CompletedTask;
    }
}
