using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Application.Netclaw;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.API.Endpoints;

/// <summary>Uses the existing AI Ops permission mapping: telemetry.read for read/use, client.manage for authoring.</summary>
public static class AiPromptEndpoints
{
    public static IEndpointRouteBuilder MapAiPromptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ai/prompt-tenants", TenantsAsync).RequireAuthorization(NetclawAuthorization.DiscoveryPolicy).WithTags("AI prompts");
        var group = app.MapGroup("/api/v1/tenants/{tenantId:int}/ai/prompts").WithTags("AI prompts").AddEndpointFilter<AiPromptErrorFilter>();
        group.MapGet("", ListAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapGet("/{promptId:guid}", GetAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapPost("", CreateAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        group.MapPut("/{promptId:guid}", UpdateAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        group.MapPost("/{promptId:guid}/archive", ArchiveAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        group.MapPut("/default", DefaultAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        return app;
    }

    internal static async Task<IResult> TenantsAsync(HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var ids = MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.TelemetryRead,
            await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.TelemetryRead, ct));
        var query = db.Tenants.AsNoTracking(); if (ids is not null) query = query.Where(x => ids.Contains(x.Id));
        var rows = await query.OrderBy(x => x.Name).Take(256).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var result = new List<AiPromptTenantDto>();
        foreach (var row in rows) result.Add(new(row.Id, row.Name, await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, row.Id, ct)));
        return Results.Ok(MonitoringFlowPermissionAuthorization.RestrictDiscoveryRows(http, NetRatelPermissions.TelemetryRead, result, x => x.TenantId));
    }
    internal static async Task<IResult> ListAsync(int tenantId, string? search, bool? includeArchived, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ReadableAsync(tenantId, http, access, ct) ? Results.Ok(await library.ListAsync(tenantId, search, includeArchived ?? false, ct)) : Results.Forbid();
    internal static async Task<IResult> GetAsync(int tenantId, Guid promptId, long? revision, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ReadableAsync(tenantId, http, access, ct) ? Results.Ok(await library.GetAsync(tenantId, promptId, revision, ct)) : Results.Forbid();
    internal static async Task<IResult> CreateAsync(int tenantId, AiPromptSaveRequest request, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ManageableAsync(tenantId, http, access, ct) ? Results.Ok(await library.CreateAsync(tenantId, request, NetclawEndpoints.Actor(http), ct)) : Results.Forbid();
    internal static async Task<IResult> UpdateAsync(int tenantId, Guid promptId, AiPromptSaveRequest request, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ManageableAsync(tenantId, http, access, ct) ? Results.Ok(await library.UpdateAsync(tenantId, promptId, request, NetclawEndpoints.Actor(http), ct)) : Results.Forbid();
    internal static async Task<IResult> ArchiveAsync(int tenantId, Guid promptId, AiPromptArchiveRequest request, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ManageableAsync(tenantId, http, access, ct) ? Results.Ok(await library.ArchiveAsync(tenantId, promptId, request, NetclawEndpoints.Actor(http), ct)) : Results.Forbid();
    internal static async Task<IResult> DefaultAsync(int tenantId, AiPromptSetDefaultRequest request, HttpContext http, IEffectiveAccessService access, PromptLibraryService library, CancellationToken ct) =>
        await ManageableAsync(tenantId, http, access, ct) ? Results.Ok(await library.SetDefaultAsync(tenantId, request, ct)) : Results.Forbid();
    private static Task<bool> ReadableAsync(int tenantId, HttpContext http, IEffectiveAccessService access, CancellationToken ct) =>
        tenantId > 0 ? access.AuthorizeAsync(http.User, NetRatelPermissions.TelemetryRead, tenantId, ct) : Task.FromResult(false);
    private static async Task<bool> ManageableAsync(int tenantId, HttpContext http, IEffectiveAccessService access, CancellationToken ct) =>
        await ReadableAsync(tenantId, http, access, ct) && await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, tenantId, ct);
}

public sealed class AiPromptErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (AiPromptConflictException exception) { return Results.Conflict(new { code = "prompt-changed", message = exception.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "prompt-changed", message = "The prompt or default changed. Refresh before saving." }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "prompt-not-found", message = "The prompt or revision is unavailable in this tenant." }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { code = "invalid-prompt", message = exception.Message }); }
    }
}
