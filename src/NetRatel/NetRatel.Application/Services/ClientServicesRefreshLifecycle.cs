using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Application.Services;

/// <summary>Bounded durable request history. Only matching authenticated ingress can record collection completion.</summary>
public static class ClientServicesRefreshLifecycle
{
    public static IReadOnlyList<ClientServicesRefreshRequestDto> Requests(ClientServicesState state) => state.RefreshRequests ?? [];

    public static ClientServicesState Expire(ClientServicesState state, DateTimeOffset now) => Replace(state,
        Requests(state).Select(request => request.Outcome == ClientServicesRefreshOutcome.Pending && now >= request.DeadlineUtc
            ? request with { Outcome = ClientServicesRefreshOutcome.TimedOut, CompletedAtUtc = now, ErrorCode = "refresh-timeout" }
            : request));

    public static ClientServicesState Update(ClientServicesState state, ClientServicesRefreshRequestDto request, DateTimeOffset now)
    {
        var requests = Requests(state);
        var existing = requests.FirstOrDefault(item => item.RequestId == request.RequestId);
        if (existing is not null)
        {
            if (existing.Outcome != ClientServicesRefreshOutcome.Pending) return state;
            if (request.Outcome is not (ClientServicesRefreshOutcome.Offline or ClientServicesRefreshOutcome.Error) ||
                request.CompletedAtUtc is null || request.CompletedAtUtc < existing.RequestedAtUtc || request.CompletedAtUtc > now ||
                request.CollectionId is not null || !ClientServiceContractValidator.IsValidErrorCode(request.ErrorCode) ||
                (request with { Outcome = existing.Outcome, CompletedAtUtc = existing.CompletedAtUtc, ErrorCode = existing.ErrorCode }) != existing)
                throw new ArgumentException("Invalid services refresh terminal transition.");
            return Replace(state, requests.Select(item => item.RequestId == request.RequestId ? request : item));
        }
        if (request.RequestId == Guid.Empty || request.ConnectionId == Guid.Empty || request.RegistrationId == Guid.Empty ||
            request.ConnectionEpoch <= 0 || request.RequestedAtUtc == default || request.RequestedAtUtc > now ||
            request.DeadlineUtc != request.RequestedAtUtc + ClientServicesLimits.RefreshTimeout ||
            request.Outcome != ClientServicesRefreshOutcome.Pending || request.CollectionId is not null ||
            request.CompletedAtUtc is not null || request.ErrorCode is not null)
            throw new ArgumentException("Invalid services refresh admission.");
        if (requests.Any(item => item.Outcome == ClientServicesRefreshOutcome.Pending) ||
            requests.LastOrDefault() is { } previous && now < previous.RequestedAtUtc + ClientServicesLimits.MinimumRefreshInterval)
            return state;
        return Replace(state, requests.TakeLast(ClientServicesLimits.MaximumRefreshHistory - 1).Append(request));
    }

    public static ClientServicesState Observe(ClientServicesState state, ClientServicesChunk chunk, DateTimeOffset now)
    {
        if (!chunk.IsFinal || chunk.Kind != ServiceSnapshotKind.Inventory) return state;
        return Replace(state, Requests(state).Select(request =>
        {
            if (request.Outcome != ClientServicesRefreshOutcome.Pending || now >= request.DeadlineUtc ||
                request.ConnectionId != chunk.ConnectionId || request.ConnectionEpoch != chunk.ConnectionEpoch ||
                request.RegistrationId != chunk.RegistrationId) return request;
            if (!request.CorrelationSupported && chunk.RefreshRequestId is null)
                return request with { Outcome = ClientServicesRefreshOutcome.Unattributed, CollectionId = chunk.CollectionId,
                    CompletedAtUtc = now, ErrorCode = "client-correlation-unsupported" };
            if (!request.CorrelationSupported || request.RequestId != chunk.RefreshRequestId) return request;
            var outcome = chunk.Status switch
            {
                ServiceCollectionStatus.Complete => ClientServicesRefreshOutcome.Complete,
                ServiceCollectionStatus.Partial => ClientServicesRefreshOutcome.Partial,
                ServiceCollectionStatus.Error => ClientServicesRefreshOutcome.Error,
                ServiceCollectionStatus.Unsupported => ClientServicesRefreshOutcome.Unsupported,
                _ => ClientServicesRefreshOutcome.Pending
            };
            return request with { Outcome = outcome, CollectionId = chunk.CollectionId, CompletedAtUtc = now, ErrorCode = chunk.ErrorCode };
        }));
    }

    public static ClientServicesState AssemblyTimedOut(ClientServicesState state, ServiceCollectionAttemptDto attempt, DateTimeOffset now) =>
        Replace(state, Requests(state).Select(request => request.Outcome == ClientServicesRefreshOutcome.Pending &&
            request.CorrelationSupported && request.RequestId == attempt.RefreshRequestId &&
            request.ConnectionEpoch == attempt.ConnectionEpoch && state.ConnectionId == request.ConnectionId && request.RegistrationId == attempt.RegistrationId
                ? request with { Outcome = ClientServicesRefreshOutcome.Error, CollectionId = attempt.CollectionId,
                    CompletedAtUtc = now, ErrorCode = "assembly_timeout" } : request));

    public static bool IsValidServerTransition(ClientServicesState current, ClientServicesState candidate, DateTimeOffset now)
    {
        var before = Requests(current);
        var after = Requests(candidate);
        if (after.Count > ClientServicesLimits.MaximumRefreshHistory || after.Select(item => item.RequestId).Distinct().Count() != after.Count)
            return false;
        // Admission and timeout/offline writes never add or alter physical evidence.
        var expected = current;
        foreach (var request in after)
        {
            var previous = Requests(expected).FirstOrDefault(item => item.RequestId == request.RequestId);
            if (previous == request) continue;
            if (previous is { Outcome: ClientServicesRefreshOutcome.Pending } && request.Outcome == ClientServicesRefreshOutcome.TimedOut)
            {
                if (request.CompletedAtUtc is not { } completed || completed < previous.DeadlineUtc || completed > now ||
                    request != (previous with { Outcome = ClientServicesRefreshOutcome.TimedOut, CompletedAtUtc = completed, ErrorCode = "refresh-timeout" }))
                    return false;
                expected = Replace(expected, Requests(expected).Select(item => item.RequestId == request.RequestId ? request : item));
                continue;
            }
            try { expected = Update(expected, request, now); }
            catch (ArgumentException) { return false; }
        }
        return Requests(expected).SequenceEqual(after) && (before.Count == 0 || after.Count != 0);
    }

    private static ClientServicesState Replace(ClientServicesState state, IEnumerable<ClientServicesRefreshRequestDto> requests)
    {
        var next = requests.ToArray();
        return Requests(state).SequenceEqual(next) ? state : state with { RefreshRequests = next };
    }
}
