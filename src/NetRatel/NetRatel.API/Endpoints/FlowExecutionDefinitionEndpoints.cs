using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Endpoints;

public static class FlowExecutionDefinitionEndpoints
{
    public static IEndpointRouteBuilder MapFlowExecutionDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId:int}/flows/execution-definitions")
            .RequireAuthorization().WithTags("Flows");
        group.MapGet("", ListAsync);
        group.MapGet("/targets", ListTargetsAsync);
        group.MapGet("/targets/selected", GetSelectedTargetsAsync);
        group.MapGet("/{sourceKind}/{definitionId}", GetAsync);
        return app;
    }
    private static FlowExecutionAuthorityDto? Authority(ClaimsPrincipal principal) =>
        principal.FindFirst("netratel_principal_id")?.Value is { Length: > 0 } id
            ? new(id, principal.FindFirst("netratel_integration_credential_id")?.Value) : null;

    internal static async Task<IResult> ListAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search, string? afterId, int? limit, HttpContext http, IEffectiveAccessService access,
        IFlowExecutionDefinitionCatalog catalog, CancellationToken ct)
    {
        if (Authority(http.User) is not { } authority ||
            !await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct)) return Results.Forbid();
        try { return Results.Ok(await catalog.ListAsync(tenantId, authority, sourceKind, search, afterId, limit ?? 50, ct)); }
        catch (ExecutionAdmissionDeniedException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "execution-definition-query-invalid" }); }
        catch (NotSupportedException) { return Results.UnprocessableEntity(new { code = "execution-definition-source-unsupported" }); }
    }


    internal static async Task<IResult> ListTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search, string? afterId, int? limit, HttpContext http, IEffectiveAccessService access,
        IFlowExecutionDefinitionCatalog catalog, CancellationToken ct)
    {
        if (Authority(http.User) is not { } authority ||
            !await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct)) return Results.Forbid();
        try { return Results.Ok(await catalog.ListTargetsAsync(tenantId, authority, sourceKind, search, afterId, limit ?? 50, ct)); }
        catch (ExecutionAdmissionDeniedException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "execution-target-query-invalid" }); }
        catch (NotSupportedException) { return Results.UnprocessableEntity(new { code = "execution-definition-source-unsupported" }); }
    }


    internal static async Task<IResult> GetSelectedTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        [FromQuery] Guid[] ids, HttpContext http, IEffectiveAccessService access,
        IFlowExecutionDefinitionCatalog catalog, CancellationToken ct)
    {
        if (Authority(http.User) is not { } authority ||
            !await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct)) return Results.Forbid();
        try { return Results.Ok(await catalog.GetSelectedTargetsAsync(tenantId, authority, sourceKind, ids, ct)); }
        catch (ExecutionAdmissionDeniedException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "execution-target-query-invalid" }); }
        catch (NotSupportedException) { return Results.UnprocessableEntity(new { code = "execution-definition-source-unsupported" }); }
    }

    internal static async Task<IResult> GetAsync(int tenantId, FlowExecutionSourceKind sourceKind, string definitionId,
        HttpContext http, IEffectiveAccessService access, IFlowExecutionDefinitionCatalog catalog, CancellationToken ct)
    {
        if (Authority(http.User) is not { } authority ||
            !await access.AuthorizeAsync(http.User, NetRatelPermissions.FlowRead, tenantId, ct)) return Results.Forbid();
        try
        {
            var definition = await catalog.GetAsync(tenantId, authority, sourceKind, definitionId, ct);
            return definition is null ? Results.NotFound() : Results.Ok(definition);
        }
        catch (ExecutionAdmissionDeniedException) { return Results.Forbid(); }
        catch (FlowExecutionPinException error) { return Results.UnprocessableEntity(new { code = error.Code }); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "execution-definition-unavailable" }); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "execution-definition-id-invalid" }); }
        catch (NotSupportedException) { return Results.UnprocessableEntity(new { code = "execution-definition-source-unsupported" }); }
    }
}
