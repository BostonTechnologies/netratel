using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static partial class FlowEndpoints
{
    internal static async Task<IResult> QueryAsync(int tenantId, [AsParameters] FlowQueryDto query,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        if (!query.IsValid) return Results.BadRequest(new { code = "flow-query-invalid" });
        return Results.Ok(await flows.QueryAsync(tenantId, query, ct).ConfigureAwait(false));
    }

    internal static async Task<IResult> MetadataAsync(int tenantId, Guid flowId, HttpContext http,
        IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        var metadata = await flows.GetMetadataAsync(tenantId, flowId, ct).ConfigureAwait(false);
        return metadata is null ? Results.NotFound() : Results.Ok(metadata);
    }

    internal static async Task<IResult> DeleteAsync(int tenantId, Guid flowId, [FromBody] FlowDeleteRequest request,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowDelete, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        var result = await flows.DeleteAsync(tenantId, flowId, request, Actor(http), ct, http.TraceIdentifier).ConfigureAwait(false);
        if (result.Disposition == FlowDeleteDisposition.ActiveWork &&
            await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct).ConfigureAwait(false))
            result = result with { HistoryPath = $"/flows?tenantId={tenantId}&flowId={flowId:D}" };
        return Results.Json(result, statusCode: result.Disposition switch
        {
            FlowDeleteDisposition.Deleted or FlowDeleteDisposition.AlreadyDeleted => 200,
            FlowDeleteDisposition.NotFound => 404,
            FlowDeleteDisposition.Conflict or FlowDeleteDisposition.ActiveWork => 409,
            _ => 400
        });
    }

    // Called only after managed admission, using its current delegated actor. No graph is loaded.
    internal static async Task<IResult> DeletePreviewAsync(int tenantId, Guid flowId, FlowDeleteRequest request,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowDelete, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        var metadata = await flows.GetMetadataAsync(tenantId, flowId, ct).ConfigureAwait(false);
        if (metadata is null) return Results.NotFound(new { code = "flow-not-found" });
        var readable = await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            requiresConfirm = true, tool = "netratel_flows", operation = "delete", tenantId, flowId,
            expectedRevision = request.ExpectedRevision, currentRevision = metadata.Revision,
            deleted = metadata.DeletedAtUtc is not null, name = readable ? metadata.Name : null,
            summary = "Deletion atomically disables future processing and releases active capacity. History remains under existing retention (normally 90 days, with protected unknown-delivery evidence). Active work conflicts. Confirm only this exact target and revision; after an uncertain response reconcile this same intent before retrying."
        });
    }
}
