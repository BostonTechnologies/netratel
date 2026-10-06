using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Application.Services;

public static class ClientServicesValidation
{
    public static bool IsValidChunk(ClientServicesChunk chunk) => TryValidateChunk(chunk, out _);

    public static bool ValidateObservation(ClientServiceObservation observation) =>
        ClientServiceContractValidator.TryValidateObservation(observation, out _);

    public static bool ValidatePolicy(ClientServiceWatchPolicyDto policy, DateTimeOffset now) =>
        ClientServiceContractValidator.IsValidPolicy(policy, now);

    public static bool TryValidateChunk(ClientServicesChunk? chunk, out string? error)
    {
        error = null;
        if (chunk is null || !chunk.Client.IsValid || chunk.ConnectionId == Guid.Empty || chunk.ConnectionEpoch <= 0 ||
            chunk.Sequence == 0 || chunk.ReceivedAtUtc == default || chunk.ChunkIndex >= ClientServicesLimits.MaximumChunks ||
            chunk.PayloadBytes <= 0 || chunk.PayloadBytes > ClientServicesLimits.MaximumChunkPayloadBytes)
        {
            error = "invalid_service_chunk";
            return false;
        }

        if (!chunk.IsFinal && (chunk.Status is ServiceCollectionStatus.Error or ServiceCollectionStatus.Unsupported))
        {
            error = "invalid_collection_completion";
            return false;
        }

        if (chunk.Kind == ServiceSnapshotKind.Watch && chunk.WatchPolicyRevision == 0)
        {
            error = "invalid_watch_revision";
            return false;
        }

        return ClientServiceContractValidator.TryValidateResult(new ServiceCollectionResult(
            chunk.CollectionId, chunk.Kind, chunk.Status, chunk.ObservedAtUtc, chunk.Services,
            chunk.ErrorCode, chunk.WatchPolicyRevision), out error);
    }
}
