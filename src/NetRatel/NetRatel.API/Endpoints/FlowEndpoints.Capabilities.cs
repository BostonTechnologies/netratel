using NetRatel.Infrastructure.Identity.Authorization;
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
}
