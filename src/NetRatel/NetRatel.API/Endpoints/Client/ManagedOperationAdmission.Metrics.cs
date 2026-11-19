using System.Text.Json;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Metrics;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

internal sealed partial class ManagedOperationAdmission
{
    private async Task<ManagedOperationAdmissionResult> AdmitMetricWatchTargetsAsync(ManagedOperationDescriptor descriptor,
        JsonElement request, ManagedOperationAdmissionResult admitted, IServiceProvider services, CancellationToken ct)
    {
        var data = request.GetProperty("data").Deserialize<MetricHistoryPolicyWrite>(ManagedOperationCatalog.JsonOptions)!;
        if (data.Watches.Length > MetricHistoryLimits.MaximumWatches || data.Watches.Any(watch => watch is null || watch.AgentId == Guid.Empty))
            return Denied("target_not_found");
        var current = await services.GetRequiredService<MetricHistoryStore>()
            .ReadPolicyAsync(admitted.Decision!.Request.TenantId, ct).ConfigureAwait(false);
        var context = new List<McpOperatorRouteAccessRequest>();
        foreach (var id in current.Watches.Concat(data.Watches).Select(watch => watch.AgentId).Distinct())
        {
            var route = await ContextRouteAsync(descriptor, admitted, id, ct).ConfigureAwait(false);
            var decision = (await routes.EvaluateAsync(route, ct).ConfigureAwait(false)).Decision;
            if (!decision.IsAllowed) return Denied(decision.FailureCode ?? "policy_denied");
            context.Add(route);
        }
        return admitted with { ContextRoutes = context };
    }

    internal async Task<IResult> FilterMetricsResponseAsync(ManagedOperationDescriptor descriptor, ManagedOperationAdmissionResult admitted,
        IResult result, CancellationToken ct)
    {
        if (descriptor.Tool != "netratel_monitoring" || descriptor.Operation is not ("metrics_settings" or "metrics_status") ||
            result is not IValueHttpResult { Value: MetricHistorySettings settings } || settings.Policy.Watches.Length == 0)
            return result;
        var visible = new HashSet<Guid>();
        foreach (var id in settings.Policy.Watches.Select(watch => watch.AgentId).Distinct())
        {
            var route = await ContextRouteAsync(descriptor, admitted, id, ct).ConfigureAwait(false);
            if ((await routes.EvaluateAsync(route, ct).ConfigureAwait(false)).Decision.IsAllowed) visible.Add(id);
        }
        return Results.Ok(settings with
        {
            Policy = settings.Policy with { Watches = settings.Policy.Watches.Where(watch => visible.Contains(watch.AgentId)).ToArray() },
            Activations = settings.Activations.Where(item => visible.Contains(item.Watch.AgentId)).ToArray()
        });
    }
}
