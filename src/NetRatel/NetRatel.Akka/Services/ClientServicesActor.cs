using Akka.Actor;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Akka.Services;

/// <summary>Sequential durable projection for a tenant/agent; unfinished chunk buffers are bounded and disposable.</summary>
public sealed class ClientServicesActor : ReceiveActor
{
    private readonly ClientKey _client;
    private readonly IClientServicesStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private ClientServicesState _state;
    private bool _loaded;
    private Assembly? _assembly;
    private ICancelable? _assemblyExpiry;
    private long _lastRouteGeneration;

    public ClientServicesActor(ClientKey client, IClientServicesStore store, TimeProvider timeProvider, TimeSpan idleTimeout)
    {
        if (!client.IsValid) throw new ArgumentException("A valid authenticated client is required.", nameof(client));
        _client = client;
        _store = store;
        _timeProvider = timeProvider;
        _state = ClientServicesState.Empty(client);
        Context.SetReceiveTimeout(idleTimeout);
        ReceiveAsync<RoutedServicesMessage>(async message =>
        {
            _lastRouteGeneration = message.Generation;
            var result = await ExecuteAsync(message.Message);
            Context.Parent.Tell(new RoutedServicesReply(Self, message.ReplyTo, result));
        });
        ReceiveAsync<RecordClientServicesChunk>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ExecuteAsync(message));
        });
        ReceiveAsync<GetClientServices>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ExecuteAsync(message));
        });
        ReceiveAsync<UpdateClientServiceWatchPolicy>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await ExecuteAsync(message));
        });
        ReceiveAsync<ExpireAssembly>(ExpireAssemblyAsync);
        Receive<ReceiveTimeout>(_ =>
        {
            if (_assembly is null) Context.Parent.Tell(new RequestServicesPassivation(Self, _lastRouteGeneration));
        });
    }

    public static Props Props(ClientKey client, IClientServicesStore store, TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null) => global::Akka.Actor.Props.Create(() =>
        new ClientServicesActor(client, store, timeProvider ?? TimeProvider.System, idleTimeout ?? TimeSpan.FromMinutes(15)));

    protected override void PostStop()
    {
        _assemblyExpiry?.Cancel();
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    private async Task<object> ExecuteAsync(IClientServicesMessage message)
    {
        if (message.Client != _client)
            return message is RecordClientServicesChunk
                ? Result(ClientServicesMessageDisposition.Invalid)
                : new Status.Failure(new ArgumentException("Services message reached a different client actor."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(ClientServicesLimits.CollectionTimeout);
        try
        {
            if (!_loaded)
            {
                _state = await _store.LoadAsync(_client, timeout.Token) ?? ClientServicesState.Empty(_client);
                _loaded = true;
            }
            return message switch
            {
                RecordClientServicesChunk record => await RecordAsync(record.Chunk, timeout.Token),
                UpdateClientServiceWatchPolicy update => await UpdatePolicyAsync(update.Policy, timeout.Token),
                GetClientServices => Clone(_state),
                _ => new Status.Failure(new ArgumentException("Unknown services message."))
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A timeout may have happened after the database commit. Reload the durable fence before
            // another write and discard incomplete buffers; never acknowledge unconfirmed persistence.
            _loaded = false;
            ClearAssembly();
            return message is RecordClientServicesChunk
                ? Result(ClientServicesMessageDisposition.PersistenceUnavailable)
                : new Status.Failure(new InvalidOperationException("Services cache is temporarily unavailable.", exception));
        }
    }

    private async Task<ClientServicesMessageResult> RecordAsync(ClientServicesChunk chunk, CancellationToken cancellationToken)
    {
        if (!ClientServicesValidation.IsValidChunk(chunk)) return Result(ClientServicesMessageDisposition.Invalid);
        var fence = GetFenceDisposition(chunk);
        if (fence != ClientServicesMessageDisposition.Accepted) return Result(fence);

        if (_assembly is not null && _timeProvider.GetElapsedTime(_assembly.StartedAt) >= ClientServicesLimits.AssemblyTimeout)
        {
            var expiredCollectionId = _assembly.First.CollectionId;
            await PersistTimeoutAsync(cancellationToken);
            if (chunk.ChunkIndex != 0 || chunk.CollectionId == expiredCollectionId)
                return Result(ClientServicesMessageDisposition.Invalid);
        }

        Assembly next;
        if (chunk.ChunkIndex == 0)
        {
            // A new epoch or explicit collection supersedes an abandoned incomplete attempt.
            next = new(chunk, _timeProvider.GetTimestamp(), 1, chunk.PayloadBytes, chunk.Services.ToArray());
        }
        else
        {
            if (_assembly is null || !Matches(_assembly.First, chunk) || chunk.ChunkIndex != _assembly.NextIndex)
                return Result(ClientServicesMessageDisposition.Invalid);
            next = _assembly with
            {
                NextIndex = _assembly.NextIndex + 1,
                Bytes = checked(_assembly.Bytes + chunk.PayloadBytes),
                Services = _assembly.Services.Concat(chunk.Services).ToArray()
            };
        }
        if (next.Bytes > ClientServicesLimits.MaximumInventoryBytes || next.Services.Count > ClientServicesLimits.MaximumServices)
            return Result(ClientServicesMessageDisposition.CapacityExceeded);
        // Revalidate the entire assembly to reject duplicates (SCM names are case insensitive),
        // mixed platforms, and invalid evidence split across otherwise valid individual chunks.
        if (chunk.Status is ServiceCollectionStatus.Complete or ServiceCollectionStatus.Partial &&
            !ClientServiceContractValidator.TryValidateResult(new ServiceCollectionResult(chunk.CollectionId, chunk.Kind,
                chunk.Status, chunk.ObservedAtUtc, next.Services, chunk.ErrorCode, chunk.WatchPolicyRevision), out _))
            return Result(ClientServicesMessageDisposition.Invalid);

        if (chunk.Kind == ServiceSnapshotKind.Watch &&
            (chunk.WatchPolicyRevision != _state.WatchPolicyRevision ||
             next.Services.Any(service => !_state.MonitoredServiceNames.Any(name => MatchesName(service, name))) ||
             next.Services.Count > ClientServicesLimits.MaximumWatchServices ||
             chunk.IsFinal && chunk.Status == ServiceCollectionStatus.Complete &&
             _state.MonitoredServiceNames.Any(name => !next.Services.Any(service => MatchesName(service, name)))))
            return Result(ClientServicesMessageDisposition.Invalid);

        var attemptStatus = chunk.IsFinal ? chunk.Status : ServiceCollectionStatus.Partial;
        var candidate = _state with
        {
            ConnectionId = chunk.ConnectionId,
            ConnectionEpoch = chunk.ConnectionEpoch,
            LastAcceptedSequence = chunk.Sequence,
            Revision = checked(_state.Revision + 1),
            LatestAttempt = new(chunk.CollectionId, chunk.Kind, attemptStatus, chunk.ConnectionEpoch, chunk.Sequence,
                chunk.ObservedAtUtc, chunk.ReceivedAtUtc, chunk.WatchPolicyRevision, chunk.ErrorCode)
        };
        if (chunk.IsFinal && chunk.Status == ServiceCollectionStatus.Complete && chunk.Kind == ServiceSnapshotKind.Inventory)
            candidate = candidate with { LastCompleteInventory = new(chunk.CollectionId, chunk.ConnectionEpoch, chunk.Sequence,
                chunk.ObservedAtUtc, chunk.ReceivedAtUtc, next.Services.ToArray()) };
        if (chunk.IsFinal && chunk.Kind == ServiceSnapshotKind.Watch)
            candidate = candidate with { WatchedServices = chunk.Status == ServiceCollectionStatus.Complete
                ? next.Services.ToArray() : UnknownWatchEvidence(next.Services, chunk.ObservedAtUtc) };

        var saved = await _store.SaveAsync(candidate, _state.Revision, cancellationToken);
        _state = saved.State;
        if (saved.Disposition != ClientServicesStoreWriteDisposition.Stored)
        {
            ClearAssembly();
            var disposition = GetFenceDisposition(chunk);
            return Result(disposition == ClientServicesMessageDisposition.Accepted ? ClientServicesMessageDisposition.Invalid : disposition);
        }
        if (chunk.IsFinal) ClearAssembly();
        else
        {
            if (_assembly is null || _assembly.First.CollectionId != next.First.CollectionId)
            {
                _assemblyExpiry?.Cancel();
                _assemblyExpiry = Context.System.Scheduler.ScheduleTellOnceCancelable(ClientServicesLimits.AssemblyTimeout,
                    Self, new ExpireAssembly(next.First.CollectionId), Self);
            }
            _assembly = next;
        }
        return Result(ClientServicesMessageDisposition.Accepted);
    }

    private async Task<ClientServicesState> UpdatePolicyAsync(ClientServiceWatchPolicy update, CancellationToken cancellationToken)
    {
        var policy = update.Policy;
        if (!ClientServicesValidation.ValidatePolicy(policy, _timeProvider.GetUtcNow())) throw new ArgumentException("Invalid services watch policy.");
        if (policy.Revision < _state.WatchPolicyRevision || policy.Revision == _state.WatchPolicyRevision &&
            !policy.ServiceNames.SequenceEqual(_state.MonitoredServiceNames, StringComparer.Ordinal))
            throw new ArgumentException("Services watch policy revision is stale.");
        if (policy.Revision == _state.WatchPolicyRevision) return Clone(_state);
        var candidate = _state with
        {
            Revision = checked(_state.Revision + 1),
            WatchPolicyRevision = policy.Revision,
            MonitoredServiceNames = policy.ServiceNames.ToArray(),
            WatchedServices = _state.WatchedServices.Where(service => policy.ServiceNames.Any(name => MatchesName(service, name))).ToArray()
        };
        var saved = await _store.SaveAsync(candidate, _state.Revision, cancellationToken);
        _state = saved.State;
        if (saved.Disposition != ClientServicesStoreWriteDisposition.Stored)
            throw new InvalidOperationException("Services watch policy changed concurrently.");
        if (_assembly?.First.Kind == ServiceSnapshotKind.Watch) ClearAssembly();
        return Clone(_state);
    }

    private ClientServicesMessageDisposition GetFenceDisposition(ClientServicesChunk chunk)
    {
        if (chunk.ConnectionEpoch < _state.ConnectionEpoch) return ClientServicesMessageDisposition.StaleEpoch;
        if (chunk.ConnectionEpoch > _state.ConnectionEpoch) return ClientServicesMessageDisposition.Accepted;
        if (_state.ConnectionId is not null && chunk.ConnectionId != _state.ConnectionId) return ClientServicesMessageDisposition.ConnectionMismatch;
        if (chunk.Sequence == _state.LastAcceptedSequence) return ClientServicesMessageDisposition.Duplicate;
        return chunk.Sequence < _state.LastAcceptedSequence ? ClientServicesMessageDisposition.StaleSequence : ClientServicesMessageDisposition.Accepted;
    }

    private IReadOnlyList<ClientServiceObservation> UnknownWatchEvidence(IReadOnlyList<ClientServiceObservation> services, DateTimeOffset observedAt) =>
        _state.MonitoredServiceNames.Select(name =>
        {
            var previous = services.FirstOrDefault(service => MatchesName(service, name))
                ?? _state.WatchedServices.FirstOrDefault(service => MatchesName(service, name))
                ?? _state.LastCompleteInventory?.Services.FirstOrDefault(service => MatchesName(service, name));
            // Explicit Unknown placeholders account for selected names never successfully observed.
            // They are server cache state, not claims of successful collector evidence or Missing.
            return previous is null
                ? new ClientServiceObservation(name, name, ClientServicePlatform.Unknown, ClientServiceState.Unknown, "unknown",
                    null, null, null, null, null, observedAt)
                : previous with { State = ClientServiceState.Unknown, ObservedAtUtc = observedAt, AuthoritativeMissing = false };
        }).ToArray();

    private async Task ExpireAssemblyAsync(ExpireAssembly message)
    {
        if (_assembly?.First.CollectionId != message.CollectionId) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(ClientServicesLimits.CollectionTimeout);
        try { await PersistTimeoutAsync(timeout.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _loaded = false; ClearAssembly(); }
    }

    private async Task PersistTimeoutAsync(CancellationToken cancellationToken)
    {
        if (_assembly is null || _state.LatestAttempt is null) return;
        var candidate = _state with
        {
            Revision = checked(_state.Revision + 1),
            LatestAttempt = _state.LatestAttempt with { Status = ServiceCollectionStatus.Error, ErrorCode = "assembly_timeout" },
            WatchedServices = _assembly.First.Kind == ServiceSnapshotKind.Watch
                ? UnknownWatchEvidence([], _timeProvider.GetUtcNow()) : _state.WatchedServices
        };
        var saved = await _store.SaveAsync(candidate, _state.Revision, cancellationToken);
        _state = saved.State;
        ClearAssembly();
    }

    private void ClearAssembly() { _assembly = null; _assemblyExpiry?.Cancel(); _assemblyExpiry = null; }

    private ClientServicesMessageResult Result(ClientServicesMessageDisposition disposition) =>
        new(_client, disposition, _state.LastAcceptedSequence, Clone(_state));

    private static ClientServicesState Clone(ClientServicesState state) => state with
    {
        LastCompleteInventory = state.LastCompleteInventory is null ? null : state.LastCompleteInventory with
            { Services = state.LastCompleteInventory.Services.ToArray() },
        WatchedServices = state.WatchedServices.ToArray(),
        MonitoredServiceNames = state.MonitoredServiceNames.ToArray()
    };

    private static bool Matches(ClientServicesChunk first, ClientServicesChunk next) =>
        first.CollectionId == next.CollectionId && first.Kind == next.Kind && first.ConnectionEpoch == next.ConnectionEpoch &&
        first.ConnectionId == next.ConnectionId && first.ObservedAtUtc == next.ObservedAtUtc && first.WatchPolicyRevision == next.WatchPolicyRevision &&
        first.Status == next.Status && first.ErrorCode == next.ErrorCode;

    private static bool MatchesName(ClientServiceObservation observation, string name) =>
        string.Equals(observation.Name, name, observation.Platform == ClientServicePlatform.Windows
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Assembly(ClientServicesChunk First, long StartedAt, uint NextIndex, int Bytes, IReadOnlyList<ClientServiceObservation> Services);
    private sealed record ExpireAssembly(Guid CollectionId);
}

internal sealed record RoutedServicesMessage(IClientServicesMessage Message, IActorRef ReplyTo, long Generation);
internal sealed record RoutedServicesReply(IActorRef Entity, IActorRef ReplyTo, object Result);
internal sealed record RequestServicesPassivation(IActorRef Entity, long Generation);
