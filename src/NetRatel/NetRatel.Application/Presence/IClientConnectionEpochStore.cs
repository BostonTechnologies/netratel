namespace NetRatel.Application.Presence;

/// <summary>Atomically allocates a monotonically increasing connection epoch across API process restarts.</summary>
public interface IClientConnectionEpochStore
{
    Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken cancellationToken);
}
