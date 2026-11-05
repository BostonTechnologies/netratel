using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static partial class FlowEndpoints
{
    internal static async Task<IResult> ExecutionDryRunAsync(int tenantId, FlowExecutionDryRunRequest request,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        denied = await PromptAccessAsync(tenantId, UsesPrompts(request.Graph), http, access, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        if (FlowGraphV2Validator.ValidateComplete(request.Graph).Valid &&
            !await PromptReferencesAvailableAsync(tenantId, request.Graph, http, ct).ConfigureAwait(false))
            return Results.BadRequest(new { code = "ai-prompt-unavailable" });
        return Results.Ok(FlowExecutionPreview.DryRun(request, tenantId));
    }
}
