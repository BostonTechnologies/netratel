using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

internal sealed partial class ManagedOperationAdmission
{
    private const int MaximumInvestigationClients = 20;
    private const int InvestigationPageSize = 100;
    private const int MaximumInvestigationOffset = 100_000;

    internal async Task<IResult> FilterAiResponseAsync(ManagedOperationDescriptor descriptor,
        JsonElement request, ManagedOperationAdmissionResult admitted, IResult result, CancellationToken ct)
    {
        if (descriptor.Tool != "netratel_ai" || descriptor.Operation is not ("clients" or "list") ||
            result is not IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK } ||
            result is not IValueHttpResult value)
            return result;
        // Use the exact bounded page returned by the canonical application
        // handler. A conversation is visible only if its entire context is.
        if (descriptor.Operation == "clients" && value.Value is IReadOnlyList<InvestigationClientDto> clients)
            return Results.Ok(await FilterAiClientsAsync(descriptor, admitted, clients, ct).ConfigureAwait(false));
        if (descriptor.Operation == "list" && value.Value is IReadOnlyList<InvestigationSummaryDto> investigations)
        {
            var offset = request.TryGetProperty("skip", out var skip) ? skip.GetInt32() : 0;
            var hasMore = investigations.Count == InvestigationPageSize && offset <= MaximumInvestigationOffset - InvestigationPageSize;
            var visible = await FilterAiInvestigationsAsync(descriptor, admitted, investigations, ct).ConfigureAwait(false);
            return Results.Ok(new ManagedAiInvestigationPage(visible, hasMore, hasMore ? offset + InvestigationPageSize : null));
        }
        throw new InvalidOperationException("The AI discovery response does not match its typed contract.");
    }

    private async Task<ManagedOperationAdmissionResult> AdmitAiContextAsync(ManagedOperationDescriptor descriptor,
        JsonElement request, ManagedOperationAdmissionResult admitted, CancellationToken ct)
    {
        IReadOnlyList<Guid> agentIds;
        bool includesAlert;
        if (descriptor.Operation == "create")
        {
            var data = request.GetProperty("data").Deserialize<InvestigationCreateRequest>(ManagedOperationCatalog.JsonOptions)!;
            agentIds = data.AgentIds;
            includesAlert = data.OccurrenceId is not null;
        }
        else if (descriptor.IdProperty == "investigationId")
        {
            var tenantId = admitted.Decision!.Request.TenantId;
            var investigationId = request.GetProperty("investigationId").GetGuid();
            var context = await db.Set<AiInvestigationRecord>().AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.Id == investigationId)
                .Select(row => new { row.AgentIds, row.OccurrenceId }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (context is null)
                return Denied("target_not_found");
            agentIds = context.AgentIds;
            includesAlert = context.OccurrenceId is not null;
        }
        else
        {
            // Discovery is filtered after its bounded page is read. Connection
            // administration has no client context to resolve.
            return admitted;
        }

        if (!IsValidAiContext(agentIds))
            return Denied("target_not_found");
        if (includesAlert && !await access.AuthorizeAsync(admitted.User!, NetRatelPermissions.MonitoringRead,
                admitted.Decision!.Request.TenantId, ct).ConfigureAwait(false))
            return Denied("permission_denied");

        var contextRoutes = new List<McpOperatorRouteAccessRequest>(agentIds.Count);
        foreach (var agentId in agentIds)
        {
            var route = await AiContextRouteAsync(descriptor, admitted, agentId, ct).ConfigureAwait(false);
            var result = await routes.EvaluateAsync(route, ct).ConfigureAwait(false);
            if (!result.Decision.IsAllowed)
                return Denied(result.Decision.FailureCode ?? "policy_denied");
            contextRoutes.Add(route);
        }
        return admitted with { ContextRoutes = contextRoutes };
    }

    internal async Task<IReadOnlyList<InvestigationClientDto>> FilterAiClientsAsync(
        ManagedOperationDescriptor descriptor, ManagedOperationAdmissionResult admitted,
        IReadOnlyList<InvestigationClientDto> clients, CancellationToken ct)
    {
        var visible = new List<InvestigationClientDto>(clients.Count);
        foreach (var client in clients)
        {
            var route = await AiContextRouteAsync(descriptor, admitted, client.AgentId, ct).ConfigureAwait(false);
            if ((await routes.EvaluateAsync(route, ct).ConfigureAwait(false)).Decision.IsAllowed)
                visible.Add(client);
        }
        return visible;
    }

    internal async Task<IReadOnlyList<InvestigationSummaryDto>> FilterAiInvestigationsAsync(
        ManagedOperationDescriptor descriptor, ManagedOperationAdmissionResult admitted,
        IReadOnlyList<InvestigationSummaryDto> investigations, CancellationToken ct)
    {
        var visible = new List<InvestigationSummaryDto>(investigations.Count);
        var decisions = new Dictionary<Guid, bool>();
        foreach (var investigation in investigations)
        {
            if (!IsValidAiContext(investigation.AgentIds))
                continue;
            var allowed = true;
            foreach (var agentId in investigation.AgentIds)
            {
                if (!decisions.TryGetValue(agentId, out var targetAllowed))
                {
                    var route = await AiContextRouteAsync(descriptor, admitted, agentId, ct).ConfigureAwait(false);
                    targetAllowed = (await routes.EvaluateAsync(route, ct).ConfigureAwait(false)).Decision.IsAllowed;
                    decisions.Add(agentId, targetAllowed);
                }
                if (!targetAllowed)
                {
                    allowed = false;
                    break;
                }
            }
            if (allowed)
                visible.Add(investigation);
        }
        return visible;
    }

    private async Task<McpOperatorRouteAccessRequest> AiContextRouteAsync(ManagedOperationDescriptor descriptor,
        ManagedOperationAdmissionResult admitted, Guid agentId, CancellationToken ct)
    {
        var source = admitted.Decision!.Request;
        // Cached context does not need a live client. Preserve the observed
        // readiness fact; the operation catalog determines whether it matters.
        var snapshot = await presence.GetClientSnapshotAsync(new ClientKey(source.TenantId, agentId), ct).ConfigureAwait(false);
        return new(source.Environment, source.Principal, admitted.ServicePrincipal!, source.McpResource!, source.McpInstance!,
            descriptor.Tool, descriptor.Operation, source.TenantId, agentId, source.RequiredScopes,
            source.CorrelationId, source.RequestId, snapshot?.Status == ClientPresenceStatus.Online, source.CapabilityAvailable);
    }

    private static bool IsValidAiContext(IReadOnlyList<Guid>? agentIds) =>
        agentIds is { Count: > 0 and <= MaximumInvestigationClients } &&
        agentIds.All(agentId => agentId != Guid.Empty) && agentIds.Distinct().Count() == agentIds.Count;
}

internal sealed record ManagedAiInvestigationPage(IReadOnlyList<InvestigationSummaryDto> Items, bool HasMore, int? NextSkip);
