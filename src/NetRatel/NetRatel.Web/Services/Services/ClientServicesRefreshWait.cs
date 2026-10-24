using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Services;

/// <summary>Use exact persisted request outcomes when supported; legacy collections remain explicitly unattributed.</summary>
public sealed class ClientServicesRefreshWait(ClientServicesReadModelDto? baseline, DateTimeOffset startedAt, Guid? requestId = null, bool correlationSupported = false)
{
    public DateTimeOffset Deadline { get; } = startedAt + ClientServicesLimits.RefreshTimeout;

    public string? Outcome(ClientServicesReadModelDto? model, DateTimeOffset now)
    {
        if (correlationSupported)
        {
            var request = model?.RefreshRequests?.FirstOrDefault(item => item.RequestId == requestId);
            if (request is { Outcome: not ClientServicesRefreshOutcome.Pending })
                return $"Refresh {request.RequestId:D} finished: {request.Outcome}. {ClientServicesPresentation.Reason(request.ErrorCode)}".Trim();
            return now >= Deadline ? $"Timed out waiting for the recorded outcome of refresh {requestId:D}. Available observations remain visible." : null;
        }
        if (model is { Connected: false }) return "The client went offline while waiting for inventory.";
        if (model is { Connected: true, SupportsServices: false }) return "Services collection is unsupported by this client.";
        var attempt = InventoryAttempt(model);
        if (attempt is not null && IsNewer(attempt.ConnectionEpoch, attempt.Sequence) &&
            (attempt.IsFinal || attempt.Status is ServiceCollectionStatus.Complete or ServiceCollectionStatus.Error or ServiceCollectionStatus.Unsupported))
            return $"Subsequent inventory collection finished: {attempt.Status}. {ClientServicesPresentation.Reason(attempt.ErrorCode)}".Trim();
        // Complete snapshots remain compatible with servers predating explicit final-attempt metadata.
        if (model?.LastCompleteInventory is { } complete && complete.CollectionId != baseline?.LastCompleteInventory?.CollectionId &&
            IsNewer(complete.ConnectionEpoch, complete.Sequence))
            return "Subsequent complete inventory received.";
        return now >= Deadline ? "Timed out waiting for a subsequent inventory outcome. Available observations remain visible." : null;
    }

    public static ServiceCollectionAttemptDto? InventoryAttempt(ClientServicesReadModelDto? model) =>
        model?.LatestInventoryAttempt ?? (model?.LatestAttempt?.Kind == ServiceSnapshotKind.Inventory ? model.LatestAttempt : null);

    private bool IsNewer(long epoch, ulong sequence)
    {
        var attempt = InventoryAttempt(baseline);
        var cursors = new[]
        {
            (Epoch: attempt?.ConnectionEpoch ?? 0, Sequence: attempt?.Sequence ?? 0),
            (Epoch: baseline?.LastCompleteInventory?.ConnectionEpoch ?? 0, Sequence: baseline?.LastCompleteInventory?.Sequence ?? 0),
            (Epoch: baseline?.LastPartialInventory?.ConnectionEpoch ?? 0, Sequence: baseline?.LastPartialInventory?.Sequence ?? 0)
        };
        return cursors.All(cursor => epoch > cursor.Epoch || epoch == cursor.Epoch && sequence > cursor.Sequence);
    }
}

public static class ClientServicesPresentation
{
    // Fixed public explanations never render untrusted OS diagnostics.
    public static string Reason(string? code) => code switch
    {
        "inventory-limit" or "inventory_limit" => "The inventory exceeded a collection or transport capacity limit.",
        "invalid-observation" => "Some service observations were invalid and were omitted.",
        "duplicate-observation" => "Duplicate service observations were omitted.",
        "systemd-output-truncated" => "Systemd output exceeded the collection output limit.",
        "systemd-output-malformed" => "Some systemd output could not be read.",
        "systemd-query-partial" => "Some systemd service properties could not be collected.",
        "systemd-query-failed" => "Systemd service enumeration failed.",
        "systemd-unavailable" or "platform-unsupported" => "The operating system service collector is unavailable.",
        "collection-timeout" or "assembly_timeout" or "refresh-timeout" => "Collection or inventory delivery timed out.",
        "refresh-connection-ended" => "The telemetry connection ended before this refresh completed.",
        "refresh-dispatch-failed" => "The refresh could not be queued to the client.",
        "client-correlation-unsupported" => "The client cannot attribute this collection to the refresh request.",
        "collection-cancelled" => "The service collection was interrupted.",
        "collection-failed" => "The operating system service collection failed.",
        null or "" => "",
        _ => "The collector reported that service inventory could not be fully obtained."
    };
}
