using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using NetRatel.Infrastructure.Services;
using Npgsql;

namespace NetRatel.API.Services;

// Invalidation carries target keys only; persisted execution snapshots are authoritative.
public sealed record ExecutionActivityNotification(Guid Id, int? TenantId, ulong? RunId, string? RequestId);

public sealed class ExecutionActivityBroker
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly object _gate = new();
    private bool _connected;
    public bool IsConnected => Volatile.Read(ref _connected);

    public Subscription? Subscribe(int? tenantId = null, ulong? runId = null, string? requestId = null, int[]? authorizedTenants = null)
    {
        lock (_gate)
        {
            if (_subscriptions.Count >= 128) return null;
            var subscription = new Subscription(this, tenantId, runId, requestId, authorizedTenants);
            _subscriptions[subscription.Id] = subscription;
            return subscription;
        }
    }

    public void Publish(ExecutionActivityNotification notification)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (notification.TenantId is { } tenant &&
                (subscription.TenantId is { } requested && requested != tenant ||
                 subscription.AuthorizedTenants is { } allowed && !allowed.Contains(tenant))) continue;
            if (notification.RunId is { } run && subscription.RunId is { } requestedRun && requestedRun != run) continue;
            if (notification.RequestId is { } request && subscription.RequestId is { } requestedRequest && requestedRequest != request) continue;
            subscription.Writer.TryWrite(notification);
        }
    }

    public void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        Volatile.Write(ref _connected, connected);
        // An established listener lost notifications while disconnected. Reconcile every viewer.
        Publish(new(Guid.NewGuid(), null, null, null));
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ExecutionActivityBroker _owner;
        private readonly Channel<ExecutionActivityNotification> _channel =
            Channel.CreateBounded<ExecutionActivityNotification>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        internal Guid Id { get; } = Guid.NewGuid();
        internal int? TenantId { get; }
        internal int[]? AuthorizedTenants { get; }
        internal ulong? RunId { get; }
        internal string? RequestId { get; }
        internal ChannelWriter<ExecutionActivityNotification> Writer => _channel.Writer;
        public ChannelReader<ExecutionActivityNotification> Reader => _channel.Reader;
        internal Subscription(ExecutionActivityBroker owner, int? tenantId, ulong? runId, string? requestId, int[]? authorizedTenants)
        { _owner = owner; TenantId = tenantId; RunId = runId; RequestId = requestId; AuthorizedTenants = authorizedTenants; }
        public void Dispose()
        { _owner._subscriptions.TryRemove(Id, out _); Writer.TryComplete(); }
    }
}

public sealed class ExecutionActivityListener(
    IConfiguration configuration, ExecutionActivityBroker broker,
    ILogger<ExecutionActivityListener> logger) : BackgroundService
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
                        var notification = JsonSerializer.Deserialize<ExecutionActivityNotification>(message.Payload);
                        if (notification is not null) broker.Publish(notification);
                    }
                    catch (JsonException) { /* Unrecognized notification is not a history row. */ }
                };
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using (var command = new NpgsqlCommand("LISTEN " + ExecutionActivityNotifications.ChannelName, connection))
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                broker.SetConnected(true);
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                broker.SetConnected(false);
                logger.LogWarning(exception, "Execution activity listener disconnected; viewers must reconcile after reconnect.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
            finally { broker.SetConnected(false); }
        }
    }
}
