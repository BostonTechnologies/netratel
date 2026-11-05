using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static partial class FlowEndpoints
{
    internal static async Task<IResult> TemplatesAsync(int tenantId, HttpContext http,
        IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        return denied ?? Results.Ok(FlowOverhaulTemplates.Descriptors);
    }

    internal static async Task<IResult> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db,
        IFlowTemplateService templates, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        var authority = Authority(http);
        if (authority is null) return Results.BadRequest(new { code = "canonical-principal-required" });
        return Write(await templates.CreateFromTemplateAsync(tenantId, request, authority.PrincipalId, ct).ConfigureAwait(false));
    }
}
