using NetRatel.Shared.Contracts.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Services;

/// <summary>One bounded OS query at a time, shared by inventory and selected watches.</summary>
internal sealed class ServiceCollectionCoordinator(IServiceInventoryCollector collector, TimeProvider timeProvider) : IDisposable
{
    private Task<ServiceCollectionResult>? _collection;
    private CancellationTokenSource? _collectionCancellation;
    private ClientServiceWatchPolicyDto? _policy;
    private DateTimeOffset _nextInventoryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextWatchAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInventoryStartedAt = DateTimeOffset.MinValue;
    private Guid? _lastRefreshRequest;
    private ulong _streamGeneration;
    private ulong _collectionGeneration;

    public Task? PendingCollection => _collection;

    public TimeSpan? DelayUntilNextCollection()
    {
        if (_collection is not null) return null;
        var now = timeProvider.GetUtcNow();
        var due = _nextInventoryAt;
        if (_policy is { } policy && policy.ExpiresAtUtc > now && policy.ServiceNames.Count > 0 && _nextWatchAt < due) due = _nextWatchAt;
        return due > now ? due - now : TimeSpan.Zero;
    }

    public void BeginStream()
    {
        _collectionCancellation?.Cancel();
        _streamGeneration++;
        if (_collection is { IsCompleted: true }) TryTakeCompleted(out _);
        _policy = null;
        _lastRefreshRequest = null;
        _nextInventoryAt = DateTimeOffset.MinValue;
        _nextWatchAt = DateTimeOffset.MinValue;
    }

    public bool ApplyPolicy(ClientServiceWatchPolicyDto policy)
    {
        if (!ClientServiceContractValidator.IsValidPolicy(policy, timeProvider.GetUtcNow()) || policy.Revision < (_policy?.Revision ?? 0)) return false;
        var changedSelection = _policy is null || policy.Revision > _policy.Revision;
        if (!changedSelection && _policy is { } current)
        {
            var sameConfiguration = policy.ServiceNames.SequenceEqual(current.ServiceNames, StringComparer.Ordinal) &&
                policy.WatchIntervalSeconds == current.WatchIntervalSeconds && policy.InventoryIntervalSeconds == current.InventoryIntervalSeconds;
            var freshRefresh = policy.RefreshRequestId is Guid requestId && requestId != _lastRefreshRequest;
            if (!sameConfiguration || policy.ExpiresAtUtc < current.ExpiresAtUtc ||
                (!freshRefresh && policy.ExpiresAtUtc == current.ExpiresAtUtc)) return false;
        }
        var now = timeProvider.GetUtcNow();
        _policy = policy;
        if (changedSelection) _nextWatchAt = now;
        if (policy.RefreshRequestId is Guid request && request != _lastRefreshRequest)
        {
            _lastRefreshRequest = request;
            _nextInventoryAt = now > _lastInventoryStartedAt.Add(ClientServicesLimits.MinimumRefreshInterval)
                ? now : _lastInventoryStartedAt.Add(ClientServicesLimits.MinimumRefreshInterval);
        }
        return true;
    }

    public void StartDueCollection(CancellationToken streamCancellation)
    {
        if (_collectionGeneration != _streamGeneration && _collection is { IsCompleted: true }) TryTakeCompleted(out _);
        if (_collection is not null || streamCancellation.IsCancellationRequested) return;
        var now = timeProvider.GetUtcNow();
        var activePolicy = _policy is { } policy && policy.ExpiresAtUtc > now ? policy : null;
        var inventory = now >= _nextInventoryAt;
        if (!inventory && (activePolicy is null || activePolicy.ServiceNames.Count == 0 || now < _nextWatchAt)) return;
        _collectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(streamCancellation);
        _collectionCancellation.CancelAfter(ClientServicesLimits.CollectionTimeout);
        if (inventory)
        {
            _lastInventoryStartedAt = now;
            _nextInventoryAt = now.AddSeconds(activePolicy?.InventoryIntervalSeconds ?? ClientServicesLimits.DefaultInventoryIntervalSeconds);
        }
        else
        {
            _nextWatchAt = now.AddSeconds(activePolicy!.WatchIntervalSeconds * (0.9 + Random.Shared.NextDouble() * 0.2));
        }
        _collectionGeneration = _streamGeneration;
        _collection = CollectAsync(inventory, activePolicy, _collectionCancellation.Token);
    }

    public bool TryTakeCompleted(out ServiceCollectionResult? result)
    {
        result = null;
        if (_collection is null || !_collection.IsCompleted) return false;
        try { result = _collection.GetAwaiter().GetResult(); }
        finally
        {
            _collection = null;
            _collectionCancellation?.Dispose();
            _collectionCancellation = null;
        }
        // A watch selected by an earlier server revision cannot leak into a new selection.
        if (_collectionGeneration != _streamGeneration) result = null;
        if (result?.Kind == ServiceSnapshotKind.Watch &&
            (result.WatchPolicyRevision != (_policy?.Revision ?? 0) || _policy?.ExpiresAtUtc <= timeProvider.GetUtcNow())) result = null;
        return true;
    }

    private async Task<ServiceCollectionResult> CollectAsync(bool inventory, ClientServiceWatchPolicyDto? policy, CancellationToken cancellationToken)
    {
        var kind = inventory ? ServiceSnapshotKind.Inventory : ServiceSnapshotKind.Watch;
        try
        {
            return inventory
                ? await collector.CollectInventoryAsync(cancellationToken).ConfigureAwait(false)
                : await collector.CollectWatchAsync(policy!.ServiceNames, policy.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ServiceCollectionResult(Guid.NewGuid(), kind, ServiceCollectionStatus.Error, timeProvider.GetUtcNow(), [], "collection-cancelled", policy?.Revision ?? 0);
        }
        catch (Exception)
        {
            // Fixed diagnostics avoid leaking OS exception text or service command lines into telemetry.
            return new ServiceCollectionResult(Guid.NewGuid(), kind, ServiceCollectionStatus.Error, timeProvider.GetUtcNow(), [], "collection-failed", policy?.Revision ?? 0);
        }
    }

    public void Dispose()
    {
        _collectionCancellation?.Cancel();
        // Dispose linked registrations only after an in-flight adapter exits.
        if (_collection is { IsCompleted: false } pending && _collectionCancellation is { } cancellation)
            _ = pending.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else _collectionCancellation?.Dispose();
    }
}

internal static class ServiceInventoryCollectorFactory
{
    public static IServiceInventoryCollector Create(TimeProvider timeProvider) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsScmServiceInventoryCollector(timeProvider: timeProvider) :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Directory.Exists("/run/systemd/system") ? new LinuxSystemdServiceInventoryCollector(timeProvider: timeProvider) :
        new UnsupportedServiceInventoryCollector(timeProvider);

    private sealed class UnsupportedServiceInventoryCollector(TimeProvider timeProvider) : IServiceInventoryCollector
    {
        public Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken) => Result(ServiceSnapshotKind.Inventory, 0, cancellationToken);
        public Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken) => Result(ServiceSnapshotKind.Watch, policyRevision, cancellationToken);
        private Task<ServiceCollectionResult> Result(ServiceSnapshotKind kind, ulong revision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ServiceCollectionResult(Guid.NewGuid(), kind, ServiceCollectionStatus.Unsupported, timeProvider.GetUtcNow(), [], "platform-unsupported", revision));
        }
    }
}
