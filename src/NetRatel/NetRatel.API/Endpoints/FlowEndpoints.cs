using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static class FlowEndpoints
{
    public static IEndpointRouteBuilder MapFlowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/flows/tenants", TenantsAsync).RequireAuthorization().WithTags("Flows");
        var group = app.MapGroup("/api/v1/tenants/{tenantId:int}/flows").RequireAuthorization().WithTags("Flows");
        group.MapGet("", ListAsync); group.MapPost("", CreateAsync);
        group.MapGet("/template", TemplateAsync); group.MapGet("/connectors", ConnectorsAsync);
        group.MapGet("/versions/{versionId:guid}", VersionByIdAsync); group.MapGet("/runs/{runId:guid}", RunByIdAsync);
        group.MapPost("/validate", ValidateAsync); group.MapPost("/dry-run", DryRunAsync);
        group.MapGet("/{flowId:guid}", GetAsync);
        group.MapPut("/{flowId:guid}/draft", SaveAsync); group.MapPost("/{flowId:guid}/clone", CloneAsync);
        group.MapPut("/{flowId:guid}/enabled", EnabledAsync); group.MapPost("/{flowId:guid}/publish", PublishAsync);
        group.MapGet("/{flowId:guid}/versions", VersionsAsync); group.MapGet("/{flowId:guid}/runs", RunsAsync);
        group.MapGet("/{flowId:guid}/runs/{runId:guid}", RunAsync);
        return app;
    }
    private static async Task<IResult> TenantsAsync(HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var ids = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.FlowRead, ct).ConfigureAwait(false);
        var query = db.Tenants.AsNoTracking(); if (ids is not null) query = query.Where(row => ids.Contains(row.Id));
        var tenants = await query.OrderBy(row => row.Name).ThenBy(row => row.Id).Take(256).Select(row => new { row.Id, row.Name }).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<FlowTenantAccessDto>();
        foreach (var tenant in tenants)
            result.Add(new(tenant.Id, tenant.Name, await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowEdit, tenant.Id, ct).ConfigureAwait(false),
                await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowPublish, tenant.Id, ct).ConfigureAwait(false),
                await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowExecute, tenant.Id, ct).ConfigureAwait(false)));
        return Results.Ok(result);
    }
    private static async Task<IResult?> AdmitAsync(int tenantId, string permission, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        if (tenantId <= 0 || !await access.AuthorizeAsync(http.User, permission, tenantId, ct).ConfigureAwait(false)) return Results.Forbid();
        return await db.Tenants.AnyAsync(row => row.Id == tenantId, ct).ConfigureAwait(false) ? null : Results.NotFound();
    }
    private static string Actor(HttpContext http) => http.User.FindFirst("netratel_principal_id")?.Value ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub") ?? "authenticated-operator";
    private static FlowExecutionAuthorityDto? Authority(HttpContext http)
    {
        var principal = http.User.FindFirst("netratel_principal_id")?.Value;
        return string.IsNullOrWhiteSpace(principal) ? null : new(principal, http.User.FindFirst("netratel_integration_credential_id")?.Value);
    }
    private static IResult Write(FlowDefinitionWriteResult result) => result.Disposition == FlowWriteDisposition.Stored && result.Definition is not null
        ? Results.Ok(result.Definition) : Failure(result.Disposition, result.Code);
    private static IResult Failure(FlowWriteDisposition disposition, string? code) => Results.Json(new { code = SafeCode(code) }, statusCode: disposition switch
    { FlowWriteDisposition.NotFound => 404, FlowWriteDisposition.Conflict => 409, FlowWriteDisposition.CapacityExceeded => 429,
        FlowWriteDisposition.ConnectorDenied => 422, _ => 400 });
    private static string SafeCode(string? code) => code is { Length: > 0 and <= 128 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? code : "flow-request-rejected";

    private static async Task<IResult> ListAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); return denied ?? Results.Ok(await flows.ListAsync(tenantId, ct).ConfigureAwait(false)); }
    private static async Task<IResult> GetAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; var flow = await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false); return flow is null ? Results.NotFound() : Results.Ok(flow); }
    private static async Task<IResult> TemplateAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); return denied ?? Results.Ok(FlowGraphTemplates.IncidentFromAlert()); }
    private static async Task<IResult> ConnectorsAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowConnectorCatalog catalog, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var authority = Authority(http); if (authority is null) return Results.Ok(Array.Empty<FlowConnectorReferenceDto>());
        var result = await catalog.ListAsync(tenantId, authority, ct).ConfigureAwait(false);
        return Results.Ok(result.Where(connector => connector.TenantId == tenantId).Take(128).ToArray());
    }
    private static async Task<IResult> CreateAsync(int tenantId, FlowCreateRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); return denied ?? Write(await flows.CreateAsync(tenantId, request, Actor(http), ct).ConfigureAwait(false)); }
    private static async Task<IResult> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); return denied ?? Write(await flows.SaveDraftAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false)); }
    private static async Task<IResult> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); return denied ?? Write(await flows.CloneAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false)); }
    private static async Task<IResult> EnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); return denied ?? Write(await flows.SetEnabledAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false)); }
    private static async Task<IResult> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowPublish, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var authority = Authority(http); if (authority is null) return Results.BadRequest(new { code = "canonical-principal-required" });
        var result = await flows.PublishAsync(tenantId, flowId, request, authority, ct).ConfigureAwait(false);
        return result.Disposition == FlowWriteDisposition.Stored && result.Version is not null ? Results.Ok(result.Version) : Failure(result.Disposition, result.Code);
    }
    private static async Task<IResult> ValidateAsync(int tenantId, FlowGraphDto graph, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); return denied ?? Results.Ok(FlowGraphValidator.ValidateComplete(graph)); }
    private static async Task<IResult> DryRunAsync(int tenantId, FlowDryRunRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        try { return Results.Ok(FlowPureEvaluation.DryRun(request)); } catch (InvalidOperationException) { return Results.BadRequest(new { code = "mapping-output-limit" }); }
    }
    private static async Task<IResult> VersionsAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; return await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false) is null ? Results.NotFound() : Results.Ok(await flows.GetVersionsAsync(tenantId, flowId, ct).ConfigureAwait(false)); }
    private static async Task<IResult> RunsAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; return await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false) is null ? Results.NotFound() : Results.Ok(await flows.GetRunsAsync(tenantId, flowId, ct).ConfigureAwait(false)); }
    private static async Task<IResult> RunAsync(int tenantId, Guid flowId, Guid runId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; var run = await flows.GetRunAsync(tenantId, flowId, runId, ct).ConfigureAwait(false); return run is null ? Results.NotFound() : Results.Ok(run); }
    private static async Task<IResult> VersionByIdAsync(int tenantId, Guid versionId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; var version = await flows.GetVersionAsync(tenantId, versionId, ct).ConfigureAwait(false); return version is null ? Results.NotFound() : Results.Ok(version); }
    private static async Task<IResult> RunByIdAsync(int tenantId, Guid runId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; var run = await flows.GetRunByIdAsync(tenantId, runId, ct).ConfigureAwait(false); return run is null ? Results.NotFound() : Results.Ok(run); }
}
