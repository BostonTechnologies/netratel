using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

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
    internal static async Task<IResult> TenantsAsync(HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
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
    private static bool UsesPrompts(FlowGraphDto graph) => graph.Nodes?.Any(node =>
        node?.AiSettings?.OptionalPrompts?.Count > 0 || node?.AiSettings?.PinnedPrompts?.Count > 0) == true;
    private static bool ContainsPromptBodies(FlowGraphDto graph) => graph.Nodes?.Any(node => node?.AiSettings?.PinnedPrompts?.Count > 0) == true;
    private static async Task<IResult?> PromptAccessAsync(int tenantId, bool required, HttpContext http, IEffectiveAccessService access, CancellationToken ct) =>
        required && !await access.AuthorizeAsync(http.User, NetRatelPermissions.TelemetryRead, tenantId, ct).ConfigureAwait(false) ? Results.Forbid() : null;
    private static async Task<bool> PromptReferencesAvailableAsync(int tenantId, FlowGraphDto graph, HttpContext http, CancellationToken ct)
    {
        if (!UsesPrompts(graph)) return true;
        var library = http.RequestServices.GetRequiredService<IPromptLibraryService>();
        try
        {
            foreach (var reference in (graph.Nodes ?? []).Where(node => node is not null).SelectMany(node => node.AiSettings?.OptionalPrompts ?? []).Where(reference => reference is not null))
                await library.ResolveAsync(tenantId, reference, allowArchived: false, ct: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException) { return false; }
    }
    private static async Task<FlowRunDetailDto> ProjectAiResultsAsync(int tenantId, FlowRunDetailDto run,
        HttpContext http, IEffectiveAccessService access, CancellationToken ct)
    {
        if (run.Actions.All(action => action.AiResult is null)) return run;
        var readable = await access.AuthorizeAsync(http.User, NetRatelPermissions.TelemetryRead, tenantId, ct).ConfigureAwait(false) &&
            await access.AuthorizeAsync(http.User, NetRatelPermissions.MonitoringRead, tenantId, ct).ConfigureAwait(false);
        return run with { Actions = run.Actions.Select(action => action with
            { AiResult = readable && action.AiResult?.TenantId == tenantId ? action.AiResult : null }).ToArray() };
    }

    internal static async Task<IResult> ListAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var rows = await flows.ListAsync(tenantId, ct).ConfigureAwait(false);
        return await PromptAccessAsync(tenantId, rows.Any(row => ContainsPromptBodies(row.Draft)), http, access, ct).ConfigureAwait(false) ?? Results.Ok(rows);
    }
    internal static async Task<IResult> GetAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var flow = await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false); if (flow is null) return Results.NotFound();
        return await PromptAccessAsync(tenantId, ContainsPromptBodies(flow.Draft), http, access, ct).ConfigureAwait(false) ?? Results.Ok(flow);
    }
    internal static async Task<IResult> TemplateAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); return denied ?? Results.Ok(FlowGraphTemplates.IncidentFromAlert()); }
    internal static async Task<IResult> ConnectorsAsync(int tenantId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowConnectorCatalog catalog, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var authority = Authority(http); if (authority is null) return Results.Ok(Array.Empty<FlowConnectorReferenceDto>());
        var result = await catalog.ListAsync(tenantId, authority, ct).ConfigureAwait(false);
        return Results.Ok(result.Where(connector => connector.TenantId == tenantId).Take(128).ToArray());
    }
    internal static async Task<IResult> CreateAsync(int tenantId, FlowCreateRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); return denied ?? Write(await flows.CreateAsync(tenantId, request, Actor(http), ct).ConfigureAwait(false)); }
    internal static async Task<IResult> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        return await PromptAccessAsync(tenantId, UsesPrompts(request.Graph), http, access, ct).ConfigureAwait(false) ?? Write(await flows.SaveDraftAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false));
    }
    internal static async Task<IResult> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var flow = await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false); if (flow is null) return Results.NotFound();
        return await PromptAccessAsync(tenantId, UsesPrompts(flow.Draft), http, access, ct).ConfigureAwait(false) ?? Write(await flows.CloneAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false));
    }
    internal static async Task<IResult> EnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowEdit, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var flow = await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false); if (flow is null) return Results.NotFound();
        return await PromptAccessAsync(tenantId, ContainsPromptBodies(flow.Draft), http, access, ct).ConfigureAwait(false) ?? Write(await flows.SetEnabledAsync(tenantId, flowId, request, Actor(http), ct).ConfigureAwait(false));
    }
    internal static async Task<IResult> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowPublish, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var flow = await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false); if (flow is null) return Results.NotFound();
        denied = await PromptAccessAsync(tenantId, UsesPrompts(flow.Draft), http, access, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var authority = Authority(http); if (authority is null) return Results.BadRequest(new { code = "canonical-principal-required" });
        var result = await flows.PublishAsync(tenantId, flowId, request, authority, ct).ConfigureAwait(false);
        return result.Disposition == FlowWriteDisposition.Stored && result.Version is not null ? Results.Ok(result.Version) : Failure(result.Disposition, result.Code);
    }
    internal static async Task<IResult> ValidateAsync(int tenantId, FlowGraphDto graph, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        denied = await PromptAccessAsync(tenantId, UsesPrompts(graph), http, access, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var validation = FlowGraphValidator.ValidateComplete(graph);
        if (validation.Valid && !await PromptReferencesAvailableAsync(tenantId, graph, http, ct).ConfigureAwait(false))
            return Results.Ok(new FlowValidationResultDto(false, [new("ai-prompt-unavailable")]));
        return Results.Ok(validation);
    }
    internal static async Task<IResult> DryRunAsync(int tenantId, FlowDryRunRequest request, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        denied = await PromptAccessAsync(tenantId, UsesPrompts(request.Graph), http, access, ct).ConfigureAwait(false); if (denied is not null) return denied;
        if (FlowGraphValidator.ValidateComplete(request.Graph).Valid && !await PromptReferencesAvailableAsync(tenantId, request.Graph, http, ct).ConfigureAwait(false))
            return Results.BadRequest(new { code = "ai-prompt-unavailable" });
        try { return Results.Ok(FlowPureEvaluation.DryRun(request)); } catch (InvalidOperationException) { return Results.BadRequest(new { code = "mapping-output-limit" }); }
    }
    internal static async Task<IResult> VersionsAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        if (await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false) is null) return Results.NotFound();
        var versions = await flows.GetVersionsAsync(tenantId, flowId, ct).ConfigureAwait(false);
        return await PromptAccessAsync(tenantId, versions.Any(version => ContainsPromptBodies(version.Graph)), http, access, ct).ConfigureAwait(false) ?? Results.Ok(versions);
    }
    internal static async Task<IResult> RunsAsync(int tenantId, Guid flowId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    { var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied; return await flows.GetAsync(tenantId, flowId, ct).ConfigureAwait(false) is null ? Results.NotFound() : Results.Ok(await flows.GetRunsAsync(tenantId, flowId, ct).ConfigureAwait(false)); }
    internal static async Task<IResult> RunAsync(int tenantId, Guid flowId, Guid runId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var run = await flows.GetRunAsync(tenantId, flowId, runId, ct).ConfigureAwait(false); if (run is null) return Results.NotFound();
        return Results.Ok(await ProjectAiResultsAsync(tenantId, run, http, access, ct).ConfigureAwait(false));
    }
    internal static async Task<IResult> VersionByIdAsync(int tenantId, Guid versionId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var version = await flows.GetVersionAsync(tenantId, versionId, ct).ConfigureAwait(false); if (version is null) return Results.NotFound();
        return await PromptAccessAsync(tenantId, ContainsPromptBodies(version.Graph), http, access, ct).ConfigureAwait(false) ?? Results.Ok(version);
    }
    internal static async Task<IResult> RunByIdAsync(int tenantId, Guid runId, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, IFlowDefinitionService flows, CancellationToken ct)
    {
        var denied = await AdmitAsync(tenantId, NetRatelPermissions.FlowRead, http, access, db, ct).ConfigureAwait(false); if (denied is not null) return denied;
        var run = await flows.GetRunByIdAsync(tenantId, runId, ct).ConfigureAwait(false); if (run is null) return Results.NotFound();
        return Results.Ok(await ProjectAiResultsAsync(tenantId, run, http, access, ct).ConfigureAwait(false));
    }
}
