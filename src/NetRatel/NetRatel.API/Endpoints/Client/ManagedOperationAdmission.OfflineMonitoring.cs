using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

internal sealed partial class ManagedOperationAdmission
{
    private static bool IsOfflineMonitoringOperation(ManagedOperationDescriptor descriptor) =>
        descriptor.Tool == "netratel_monitoring" && descriptor.Operation is "acknowledge_offline" or "clear_offline";

    private async Task<ManagedOperationAdmissionResult> AdmitOfflineOccurrenceAsync(JsonElement request,
        ManagedOperationAdmissionResult admitted, CancellationToken ct)
    {
        var data = request.GetProperty("data").Deserialize<MonitoringOperatorActionDto>(ManagedOperationCatalog.JsonOptions)!;
        var occurrence = new ManagedOfflineOccurrence(admitted.Decision!.Request.TenantId,
            request.GetProperty("ruleId").GetGuid(), data.OccurrenceId);
        return await OfflineOccurrenceExistsAsync(occurrence, ct).ConfigureAwait(false)
            ? admitted with { OfflineOccurrence = occurrence }
            : Denied("monitoring_occurrence_unavailable");
    }

    private Task<bool> OfflineOccurrenceExistsAsync(ManagedOfflineOccurrence target, CancellationToken ct) =>
        db.MonitoringSeries.AsNoTracking().AnyAsync(series =>
            series.TenantId == target.TenantId && series.RuleId == target.RuleId &&
            series.AgentId == Guid.Empty && series.ResourceKey == MonitoringOfflinePresentation.ResourceKey &&
            series.LatestOccurrenceId == target.OccurrenceId && target.OccurrenceId != Guid.Empty &&
            db.MonitoringRules.Any(rule => rule.TenantId == target.TenantId && rule.RuleId == target.RuleId) &&
            db.MonitoringOccurrences.Any(occurrence => occurrence.TenantId == target.TenantId &&
                occurrence.RuleId == target.RuleId && occurrence.OccurrenceId == target.OccurrenceId &&
                occurrence.AgentId == Guid.Empty && occurrence.ResourceKey == MonitoringOfflinePresentation.ResourceKey), ct);
}

// The canonical store still decides whether an occurrence is open and checks
// its revisions. LatestOccurrenceId deliberately preserves safe clear retries.
internal sealed record ManagedOfflineOccurrence(int TenantId, Guid RuleId, Guid OccurrenceId);
