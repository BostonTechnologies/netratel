using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared.Contracts.Services;
using System;
using System.Collections.Generic;

namespace NetRatel.Client.Service.Services;

internal static class ServiceSnapshotSerializer
{
    /// <summary>Measure the encoded protobuf envelope, including worst-case sequence varint.</summary>
    public static IReadOnlyList<ServiceSnapshotChunk> CreateChunks(ServiceCollectionResult result, GatewayPresenceSession session, string protocolVersion)
    {
        var chunks = new List<ServiceSnapshotChunk>();
        var chunk = CreateChunk(result, 0);
        var totalBytes = 0;
        var truncated = result.Services.Count > ClientServicesLimits.MaximumServices;
        var maximumEntries = result.Kind == ServiceSnapshotKind.Watch ? ClientServicesLimits.MaximumWatchServices : ClientServicesLimits.MaximumServices;
        var acceptedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in result.Services)
        {
            if (acceptedNames.Count >= maximumEntries) { truncated = true; break; }
            if (!ClientServiceContractValidator.TryValidateObservation(observation, out _) || !acceptedNames.Add(observation.Name))
            {
                truncated = true;
                continue;
            }
            var service = ToWire(observation);
            chunk.Services.Add(service);
            if (EnvelopeSize(chunk, session, protocolVersion) > ClientServicesLimits.MaximumChunkPayloadBytes)
            {
                chunk.Services.RemoveAt(chunk.Services.Count - 1);
                if (chunk.Services.Count == 0 || chunks.Count >= ClientServicesLimits.MaximumChunks - 1)
                {
                    truncated = true;
                    break;
                }
                chunks.Add(chunk);
                totalBytes += EnvelopeSize(chunk, session, protocolVersion);
                chunk = CreateChunk(result, (uint)chunks.Count);
                chunk.Services.Add(service);
            }
            if (totalBytes + EnvelopeSize(chunk, session, protocolVersion) > ClientServicesLimits.MaximumInventoryBytes)
            {
                chunk.Services.RemoveAt(chunk.Services.Count - 1);
                truncated = true;
                break;
            }
        }
        chunks.Add(chunk);
        chunks[^1].IsFinal = true;
        if (truncated)
        {
            foreach (var item in chunks)
            {
                item.Status = (ServiceCollectionCompleteness)(int)ServiceCollectionStatus.Partial;
                item.ErrorCode = "inventory-limit";
                foreach (var service in item.Services)
                {
                    if (!service.AuthoritativeMissing) continue;
                    service.AuthoritativeMissing = false;
                    service.State = (ServiceState)(int)ClientServiceState.Unknown;
                }
            }
        }
        // Status/error/final flags affect encoded size too; reserve metadata in EnvelopeSize.
        return chunks;
    }

    public static AgentTelemetryFrame CreateEnvelope(ServiceSnapshotChunk chunk, GatewayPresenceSession session, string protocolVersion, ulong sequence) => new()
    {
        ProtocolVersion = protocolVersion,
        TenantId = session.TenantId,
        ClientId = session.AgentId.ToString("D"),
        ConnectionEpoch = session.ConnectionEpoch,
        ConnectionId = session.ConnectionId.ToString("D"),
        Sequence = sequence,
        ServicesChunk = chunk
    };

    private static int EnvelopeSize(ServiceSnapshotChunk chunk, GatewayPresenceSession session, string protocolVersion)
    {
        var measured = chunk.Clone();
        measured.IsFinal = true;
        // Reserve enough room for a bounded error and all possible enum encodings.
        measured.ErrorCode = new string('x', ClientServicesLimits.MaximumErrorCodeLength);
        if ((int)measured.Status == 0) measured.Status = (ServiceCollectionCompleteness)1;
        return CreateEnvelope(measured, session, protocolVersion, ulong.MaxValue).CalculateSize();
    }

    private static ServiceSnapshotChunk CreateChunk(ServiceCollectionResult result, uint index) => new()
    {
        CollectionId = result.CollectionId.ToString("D"),
        ChunkIndex = index,
        Kind = (ServiceSnapshotType)(int)result.Kind,
        Status = (ServiceCollectionCompleteness)(int)result.Status,
        ObservedAtUtc = Timestamp.FromDateTimeOffset(result.ObservedAtUtc),
        WatchPolicyRevision = result.WatchPolicyRevision,
        ErrorCode = result.ErrorCode is { } error ? error[..Math.Min(error.Length, ClientServicesLimits.MaximumErrorCodeLength)] : string.Empty
    };

    private static ServiceObservation ToWire(ClientServiceObservation value) => new()
    {
        Name = value.Name,
        DisplayName = value.DisplayName,
        Platform = (ServicePlatform)(int)value.Platform,
        State = (ServiceState)(int)value.State,
        RawState = value.RawState,
        StartMode = value.StartMode ?? string.Empty,
        LoadState = value.LoadState ?? string.Empty,
        ActiveState = value.ActiveState ?? string.Empty,
        SubState = value.SubState ?? string.Empty,
        UnitFileState = value.UnitFileState ?? string.Empty,
        ObservedAtUtc = Timestamp.FromDateTimeOffset(value.ObservedAtUtc),
        AuthoritativeMissing = value.AuthoritativeMissing
    };
}
