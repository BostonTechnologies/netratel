using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Application.Services;

/// <summary>Transport-neutral chunk after authenticated envelope and encoded-size validation.</summary>
public sealed record ClientServicesChunk(
    ClientKey Client,
    Guid ConnectionId,
    long ConnectionEpoch,
    ulong Sequence,
    Guid CollectionId,
    uint ChunkIndex,
    bool IsFinal,
    ServiceSnapshotKind Kind,
    ServiceCollectionStatus Status,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    ulong WatchPolicyRevision,
    IReadOnlyList<ClientServiceObservation> Services,
    int PayloadBytes,
    string? ErrorCode = null);

public interface IClientServicesMessage
{
    ClientKey Client { get; }
}

public sealed record RecordClientServicesChunk(ClientServicesChunk Chunk) : IClientServicesMessage
{
    public ClientKey Client => Chunk.Client;
}

public sealed record GetClientServices(ClientKey Client) : IClientServicesMessage;

public sealed record ClientServiceWatchPolicy(ClientKey Client, ClientServiceWatchPolicyDto Policy);

public sealed record UpdateClientServiceWatchPolicy(ClientServiceWatchPolicy Policy) : IClientServicesMessage
{
    public ClientKey Client => Policy.Client;
}

public enum ClientServicesMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleEpoch = 2,
    StaleSequence = 3,
    ConnectionMismatch = 4,
    Invalid = 5,
    CapacityExceeded = 6,
    PersistenceUnavailable = 7
}

public sealed record ClientServicesMessageResult(
    ClientKey Client,
    ClientServicesMessageDisposition Disposition,
    ulong LastAcceptedSequence,
    ClientServicesState? State = null);

/// <summary>The durable last complete inventory and latest attempt; an unfinished assembly is never persisted as complete.</summary>
public sealed record ClientServicesState(
    ClientKey Client,
    long ConnectionEpoch,
    ulong LastAcceptedSequence,
    long Revision,
    ClientServicesSnapshotDto? LastCompleteInventory,
    ServiceCollectionAttemptDto? LatestAttempt,
    IReadOnlyList<ClientServiceObservation> WatchedServices,
    IReadOnlyList<string> MonitoredServiceNames,
    ulong WatchPolicyRevision,
    Guid? ConnectionId = null)
{
    public static ClientServicesState Empty(ClientKey client) =>
        new(client, 0, 0, 0, null, null, [], [], 0);
}

public interface IClientServicesRouter
{
    Task<ClientServicesMessageResult> RecordAsync(RecordClientServicesChunk message, CancellationToken cancellationToken);
    Task<ClientServicesState> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken);
    Task<ClientServicesState> UpdateWatchPolicyAsync(ClientServiceWatchPolicy policy, CancellationToken cancellationToken);
}

public enum ClientServicesStoreWriteDisposition
{
    Stored = 0,
    Conflict = 1
}

public sealed record ClientServicesStoreWriteResult(ClientServicesStoreWriteDisposition Disposition, ClientServicesState State);

/// <summary>Every operation owns a scoped DbContext; writes fence revision and connection/sequence.</summary>
public interface IClientServicesStore
{
    Task<ClientServicesState?> LoadAsync(ClientKey client, CancellationToken cancellationToken);
    Task<ClientServicesStoreWriteResult> SaveAsync(ClientServicesState candidate, long expectedRevision, CancellationToken cancellationToken);
}

/// <summary>Selected names are derived from authorized server rules, never from unchecked agent input.</summary>
public interface IClientServiceWatchPolicySource
{
    Task<ClientServiceWatchPolicy> GetPolicyAsync(ClientKey client, CancellationToken cancellationToken);
}
