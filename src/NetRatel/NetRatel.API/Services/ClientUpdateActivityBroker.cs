using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using Npgsql;

namespace NetRatel.API.Services;

// Notifications contain no row data and are disposable. The history endpoint is authoritative.
public sealed record ClientUpdateActivityNotification(Guid Id, int? TenantId);

public static class ClientUpdateActivityNotifications
{
    public const string ChannelName = "netratel_client_update_activity";

    // PostgreSQL delivers NOTIFY only after the surrounding transaction commits.
    public static Task NotifyAsync(OrchestratorDbContext db, int? tenantId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return Task.CompletedTask;
        var payload = JsonSerializer.Serialize(new ClientUpdateActivityNotification(Guid.NewGuid(), tenantId));
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify('netratel_client_update_activity', {payload})", cancellationToken);
    }
}

public sealed class ClientUpdateActivityBroker
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly object _gate = new();
    private bool _connected;
    public bool IsConnected => Volatile.Read(ref _connected);

    public Subscription? Subscribe(int? tenantId, int[]? authorizedTenants)
    {
        lock (_gate)
        {
            if (_subscriptions.Count >= 128) return null;
            var subscription = new Subscription(this, tenantId, authorizedTenants);
            _subscriptions[subscription.Id] = subscription;
            return subscription;
        }
    }

    public void Publish(ClientUpdateActivityNotification notification)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (notification.TenantId is { } tenant &&
                (subscription.TenantId is { } requested && requested != tenant ||
                 subscription.AuthorizedTenants is { } allowed && !allowed.Contains(tenant))) continue;
            subscription.Writer.TryWrite(notification);
        }
    }

    public void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        Volatile.Write(ref _connected, connected);
        // An established listener lost notifications while disconnected. Reconcile every viewer.
        Publish(new(Guid.NewGuid(), null));
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ClientUpdateActivityBroker _owner;
        private readonly Channel<ClientUpdateActivityNotification> _channel =
            Channel.CreateBounded<ClientUpdateActivityNotification>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        internal Guid Id { get; } = Guid.NewGuid();
        internal int? TenantId { get; }
        internal int[]? AuthorizedTenants { get; }
        internal ChannelWriter<ClientUpdateActivityNotification> Writer => _channel.Writer;
        public ChannelReader<ClientUpdateActivityNotification> Reader => _channel.Reader;
        internal Subscription(ClientUpdateActivityBroker owner, int? tenantId, int[]? authorizedTenants)
        { _owner = owner; TenantId = tenantId; AuthorizedTenants = authorizedTenants; }
        public void Dispose()
        { _owner._subscriptions.TryRemove(Id, out _); Writer.TryComplete(); }
    }
}

public sealed class ClientUpdateActivityListener(
    IConfiguration configuration, ClientUpdateActivityBroker broker,
    ILogger<ClientUpdateActivityListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("NetRatelDb") ?? configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var listenerConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { KeepAlive = 15 }.ConnectionString;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(listenerConnectionString);
                connection.Notification += (_, message) =>
                {
                    try
                    {
                        var notification = JsonSerializer.Deserialize<ClientUpdateActivityNotification>(message.Payload);
                        if (notification is not null) broker.Publish(notification);
                    }
                    catch (JsonException) { /* Unrecognized notification is not a history row. */ }
                };
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using (var command = new NpgsqlCommand("LISTEN " + ClientUpdateActivityNotifications.ChannelName, connection))
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                broker.SetConnected(true);
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                broker.SetConnected(false);
                logger.LogWarning(exception, "Client update activity listener disconnected; viewers must reconcile after reconnect.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally { broker.SetConnected(false); }
        }
    }
}
