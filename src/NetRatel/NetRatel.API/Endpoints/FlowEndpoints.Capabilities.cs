using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Application.Flows;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static partial class FlowEndpoints
{
    internal static async Task<IResult> SchemaCapabilitiesAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        return denied ?? Results.Ok(FlowSchemaCapabilities.Current);
    }

    internal static async Task<IResult> ExecutionConnectorsAsync(int tenantId, [FromQuery] FlowExecutionSourceKind[] sources,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db,
        IServiceProvider services, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false);
        if (denied is not null) return denied;
        if (sources.Length is < 1 or > 3 || sources.Distinct().Count() != sources.Length || !sources.All(Enum.IsDefined))
            return Results.BadRequest(new { code = "execution-source-filter" });
        var authority = Authority(http);
        var catalog = services.GetService<IFlowExecutionConnectorCatalog>();
        if (authority is null || catalog is null) return Results.Ok(Array.Empty<FlowConnectorReferenceDto>());
        return Results.Ok((await catalog.ListForExecutionAsync(tenantId, authority, sources, ct).ConfigureAwait(false))
            .Where(connector => connector.TenantId == tenantId).Take(128).ToArray());
    }
}
