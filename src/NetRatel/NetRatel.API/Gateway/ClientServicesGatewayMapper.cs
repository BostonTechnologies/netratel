using Google.Protobuf;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Gateway;

public static class ClientServicesGatewayMapper
{
    public static ClientServicesChunk Map(AgentTelemetryFrame envelope, ClientKey client, DateTimeOffset receivedAtUtc)
    {
        var chunk = envelope.ServicesChunk;
        if (chunk is null || !Guid.TryParse(chunk.CollectionId, out var collectionId))
            throw Invalid("The services collection metadata is invalid.");
        var bytes = envelope.CalculateSize();
        if (bytes > ClientServicesLimits.MaximumChunkPayloadBytes)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The services chunk exceeds its bounded envelope size."));
        var observed = Timestamp(chunk.ObservedAtUtc);
        var observations = new List<ClientServiceObservation>(chunk.Services.Count);
        foreach (var item in chunk.Services)
        {
            var platform = (ClientServicePlatform)(int)item.Platform;
            var state = (ClientServiceState)(int)item.State;
            observations.Add(new(item.Name, item.DisplayName, platform, state, item.RawState,
                Optional(item.StartMode), Optional(item.LoadState), Optional(item.ActiveState), Optional(item.SubState),
                Optional(item.UnitFileState), Timestamp(item.ObservedAtUtc), item.AuthoritativeMissing));
        }
        var mapped = new ClientServicesChunk(client, Guid.Parse(envelope.ConnectionId), checked((long)envelope.ConnectionEpoch), envelope.Sequence,
            collectionId, chunk.ChunkIndex, chunk.IsFinal, (ServiceSnapshotKind)(int)chunk.Kind,
            (ServiceCollectionStatus)(int)chunk.Status, observed, receivedAtUtc, chunk.WatchPolicyRevision,
            observations, bytes, Optional(chunk.ErrorCode));
        if (!ClientServicesValidation.IsValidChunk(mapped)) throw Invalid("The services collection violates its bounded evidence contract.");
        return mapped;
    }

    private static string? Optional(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static DateTimeOffset Timestamp(Google.Protobuf.WellKnownTypes.Timestamp? value)
    {
        if (value is null) throw Invalid("The services observation timestamp is required.");
        try { return value.ToDateTimeOffset(); }
        catch (InvalidOperationException) { throw Invalid("The services observation timestamp is invalid."); }
        catch (ArgumentOutOfRangeException) { throw Invalid("The services observation timestamp is invalid."); }
    }
    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));
}
