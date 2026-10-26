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
    private Guid? _pendingRefreshRequest;
    private Guid? _collectionRefreshRequest;
    private ulong _streamGeneration;
    private ulong _collectionGeneration;
    private bool _collectingInventory;

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
        _pendingRefreshRequest = null;
        _collectionRefreshRequest = null;
        _nextInventoryAt = DateTimeOffset.MinValue;
        _nextWatchAt = DateTimeOffset.MinValue;
    }

    public bool ApplyPolicy(ClientServiceWatchPolicyDto policy) => ApplyPolicy(policy, out _);

    public bool ApplyPolicy(ClientServiceWatchPolicyDto policy, out string? rejectionReason)
    {
        var validatedAtUtc = timeProvider.GetUtcNow();
        if (!ClientServiceContractValidator.TryValidatePolicy(policy, validatedAtUtc, out rejectionReason))
        {
            // Keep the existing admission limits; distinguish timing failures without logging the selection.
            if (policy is not null && policy.ExpiresAtUtc <= validatedAtUtc) rejectionReason = "expired_watch_policy";
            else if (policy is not null && policy.ExpiresAtUtc - validatedAtUtc > ClientServicesLimits.MaximumPolicyLifetime)
                rejectionReason = "watch_policy_lifetime_exceeded";
            return false;
        }
        if (policy.Revision < (_policy?.Revision ?? 0))
        {
            rejectionReason = "stale_watch_policy";
            return false;
        }
        var changedSelection = _policy is null || policy.Revision > _policy.Revision;
        if (!changedSelection && _policy is { } current)
        {
            var sameConfiguration = policy.ServiceNames.SequenceEqual(current.ServiceNames, StringComparer.Ordinal) &&
                policy.WatchIntervalSeconds == current.WatchIntervalSeconds && policy.InventoryIntervalSeconds == current.InventoryIntervalSeconds;
            var freshRefresh = policy.RefreshRequestId is Guid requestId && requestId != _lastRefreshRequest;
            if (!sameConfiguration || policy.ExpiresAtUtc < current.ExpiresAtUtc ||
                (!freshRefresh && policy.ExpiresAtUtc == current.ExpiresAtUtc))
            {
                rejectionReason = "conflicting_or_replayed_watch_policy";
                return false;
            }
        }
        var now = timeProvider.GetUtcNow();
        _policy = policy;
        if (changedSelection) _nextWatchAt = now;
        if (policy.RefreshRequestId is Guid request && request != _lastRefreshRequest)
        {
            _lastRefreshRequest = request;
            // Bind a coalesced request explicitly to this in-flight inventory. A watch never satisfies it.
            if (_collection is { IsCompleted: false } && _collectingInventory && _collectionGeneration == _streamGeneration &&
                _collectionRefreshRequest is null)
                _collectionRefreshRequest = request;
            else
            {
                _pendingRefreshRequest = request;
                _nextInventoryAt = now > _lastInventoryStartedAt.Add(ClientServicesLimits.MinimumRefreshInterval)
                    ? now : _lastInventoryStartedAt.Add(ClientServicesLimits.MinimumRefreshInterval);
            }
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
        _collectingInventory = inventory;
        _collectionRefreshRequest = inventory ? _pendingRefreshRequest : null;
        if (inventory) _pendingRefreshRequest = null;
        _collection = CollectAsync(inventory, activePolicy, _collectionCancellation.Token, streamCancellation);
    }

    public bool TryTakeCompleted(out ServiceCollectionResult? result)
    {
        result = null;
        if (_collection is null || !_collection.IsCompleted) return false;
        try
        {
            result = _collection.GetAwaiter().GetResult();
            if (result.Kind == ServiceSnapshotKind.Inventory) result = result with { RefreshRequestId = _collectionRefreshRequest };
        }
        finally
        {
            _collection = null;
            _collectionRefreshRequest = null;
            _collectionCancellation?.Dispose();
            _collectionCancellation = null;
        }
        // A watch selected by an earlier server revision cannot leak into a new selection.
        if (_collectionGeneration != _streamGeneration) result = null;
        if (result?.Kind == ServiceSnapshotKind.Watch &&
            (result.WatchPolicyRevision != (_policy?.Revision ?? 0) || _policy?.ExpiresAtUtc <= timeProvider.GetUtcNow())) result = null;
        return true;
    }

    private async Task<ServiceCollectionResult> CollectAsync(bool inventory, ClientServiceWatchPolicyDto? policy,
        CancellationToken cancellationToken, CancellationToken streamCancellation)
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
            var reason = cancellationToken.IsCancellationRequested && !streamCancellation.IsCancellationRequested
                ? "collection-timeout" : "collection-cancelled";
            return new ServiceCollectionResult(Guid.NewGuid(), kind, ServiceCollectionStatus.Error, timeProvider.GetUtcNow(), [], reason, policy?.Revision ?? 0);
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
