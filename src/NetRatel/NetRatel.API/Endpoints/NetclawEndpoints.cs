using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Events;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.API.Endpoints;

public static class NetclawEndpoints
{
    public static IEndpointRouteBuilder MapNetclawEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ai/tenants", TenantsAsync).RequireAuthorization(NetclawAuthorization.DiscoveryPolicy).WithTags("AI investigations");
        var group = app.MapGroup("/api/v1/tenants/{tenantId:int}/ai").WithTags("AI investigations")
            .AddEndpointFilter<NetclawErrorFilter>();
        group.MapGet("/connection", ConnectionAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapGet("/connection/diagnostics", ConnectionDiagnosticsAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapPost("/connection/pair", PairAsync).RequireAuthorization(NetclawAuthorization.ConnectionAdminPolicy);
        group.MapPost("/connection/test", TestAsync).RequireAuthorization(NetclawAuthorization.ConnectionAdminPolicy);
        group.MapPut("/connection/enabled", EnabledAsync).RequireAuthorization(NetclawAuthorization.ConnectionAdminPolicy);
        group.MapGet("/clients/search", SearchClientsAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapGet("/clients", ClientsAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapGet("/investigations", ListAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapPost("/investigations", CreateAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        group.MapGet("/investigations/{investigationId:guid}", GetAsync).RequireAuthorization(NetclawAuthorization.ReadPolicy);
        group.MapPost("/investigations/{investigationId:guid}/messages", SendAsync)
            .RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy, NetclawAuthorization.SecretUsePolicy);
        group.MapPost("/investigations/{investigationId:guid}/approvals/{callId}", ApprovalAsync)
            .RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy, NetclawAuthorization.SecretUsePolicy);
        group.MapPost("/investigations/{investigationId:guid}/reconcile", ReconcileAsync)
            .RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy, NetclawAuthorization.SecretUsePolicy);
        group.MapPost("/investigations/{investigationId:guid}/archive", ArchiveAsync)
            .RequireAuthorization(NetclawAuthorization.ReadPolicy, NetclawAuthorization.ManagePolicy);
        return app;
    }

    internal static async Task<IResult> TenantsAsync(HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var ids = MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.TelemetryRead,
            await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.TelemetryRead, ct));
        var query = db.Tenants.AsNoTracking(); if (ids is not null) query = query.Where(x => ids.Contains(x.Id));
        var tenants = await query.OrderBy(x => x.Name).Take(256).Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        var result = new List<NetclawTenantDto>();
        foreach (var tenant in tenants) result.Add(new(tenant.Id, tenant.Name,
            await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, tenant.Id, ct) &&
            await access.AuthorizeAsync(http.User, NetRatelPermissions.SecretUse, tenant.Id, ct),
            await access.AuthorizeAsync(http.User, NetRatelPermissions.TenantAdministration, tenant.Id, ct)));
        return Results.Ok(MonitoringFlowPermissionAuthorization.RestrictDiscoveryRows(http,
            NetRatelPermissions.TelemetryRead, result, tenant => tenant.TenantId));
    }

    internal static async Task<IResult> ConnectionAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        NetclawProfileService profiles, CancellationToken ct) =>
        await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct) ? Results.Ok(await profiles.GetAsync(tenantId, ct)) : Results.Forbid();

    internal static async Task<IResult> ConnectionDiagnosticsAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        NetclawProfileService profiles, NetclawInvestigationWorker worker, OrchestratorDbContext db, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)) return Results.Forbid();
        var observation = worker.ConnectionDiagnostics(await profiles.GetAsync(tenantId, ct));
        if (observation.Sessions.Count == 0) return Results.Ok(observation);
        var includeAlerts = await AllowedAsync(tenantId, NetRatelPermissions.MonitoringRead, http, access, ct);
        var ids = observation.Sessions.Select(session => session.InvestigationId).ToArray();
        var readable = await db.Set<AiInvestigationRecord>().AsNoTracking()
            .Where(row => row.TenantId == tenantId && ids.Contains(row.Id) && (includeAlerts || row.OccurrenceId == null))
            .Select(row => row.Id).ToListAsync(ct);
        return Results.Ok(observation with { Sessions = observation.Sessions.Where(session => readable.Contains(session.InvestigationId)).ToArray() });
    }

    internal static async Task<IResult> PairAsync(int tenantId, NetclawPairRequest request, HttpContext http,
        IEffectiveAccessService access, NetclawProfileService profiles, IEventRecorder audit, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.TenantAdministration, http, access, ct)) return Results.Forbid();
        var result = await profiles.PairAsync(tenantId, request, Actor(http), ct);
        await AuditAsync(audit, tenantId, Actor(http), "netclaw.paired", result.Connection.Id.ToString(), ct);
        return Results.Ok(result);
    }

    internal static async Task<IResult> TestAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        NetclawProfileService profiles, CancellationToken ct) =>
        await AllowedAsync(tenantId, NetRatelPermissions.TenantAdministration, http, access, ct)
            ? Results.Ok(await profiles.TestAsync(tenantId, ct: ct)) : Results.Forbid();

    internal static async Task<IResult> EnabledAsync(int tenantId, NetclawEnabledRequest request, HttpContext http,
        IEffectiveAccessService access, NetclawProfileService profiles, IEventRecorder audit, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.TenantAdministration, http, access, ct)) return Results.Forbid();
        var result = await profiles.SetEnabledAsync(tenantId, request, ct);
        await AuditAsync(audit, tenantId, Actor(http), request.Enabled ? "netclaw.enabled" : "netclaw.disabled", result.Id.ToString(), ct);
        return Results.Ok(result);
    }

    internal static async Task<IResult> ClientsAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        InvestigationStore store, CancellationToken ct) =>
        await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct) ? Results.Ok(await store.ClientsAsync(tenantId, null, ct)) : Results.Forbid();

    internal static async Task<IResult> SearchClientsAsync(int tenantId, string? search, int? offset, HttpContext http,
        IEffectiveAccessService access, InvestigationStore store, CancellationToken ct) =>
        await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)
            ? Results.Ok(await store.SearchClientsAsync(tenantId, search, offset ?? 0, ct)) : Results.Forbid();

    internal static async Task<IResult> ListAsync(int tenantId, int? skip, HttpContext http, IEffectiveAccessService access,
        InvestigationStore store, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)) return Results.Forbid();
        var includeAlerts = await AllowedAsync(tenantId, NetRatelPermissions.MonitoringRead, http, access, ct);
        return Results.Ok(await store.ListAsync(tenantId, skip ?? 0, ct, includeAlerts));
    }

    internal static async Task<IResult> CreateAsync(int tenantId, InvestigationCreateRequest request, HttpContext http,
        IEffectiveAccessService access, InvestigationStore store, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.ClientManagement, http, access, ct) ||
            !await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)) return Results.Forbid();
        if (request.OccurrenceId is not null && !await AllowedAsync(tenantId, NetRatelPermissions.MonitoringRead, http, access, ct)) return Results.Forbid();
        var row = await store.CreateAsync(tenantId, request, Actor(http), ct);
        return Results.Ok(await store.LoadAsync(tenantId, row.Id, 0, ct));
    }

    internal static async Task<IResult> GetAsync(int tenantId, Guid investigationId, long? after, HttpContext http,
        IEffectiveAccessService access, InvestigationStore store, NetclawInvestigationWorker worker, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)) return Results.Forbid();
        var snapshot = await store.LoadAsync(tenantId, investigationId, after ?? 0, ct);
        if (snapshot.Investigation.OccurrenceId is not null && !await AllowedAsync(tenantId, NetRatelPermissions.MonitoringRead, http, access, ct)) return Results.Forbid();
        return Results.Ok(snapshot with { Draft = snapshot.Investigation.State == InvestigationState.Processing ? worker.Draft(investigationId) : null });
    }

    internal static async Task<IResult> SendAsync(int tenantId, Guid investigationId, InvestigationMessageRequest request,
        HttpContext http, NetclawAuthority authority, InvestigationStore store, CancellationToken ct)
    {
        if (!await authority.CanInvokeAsync(tenantId, Actor(http), Credential(http), false, ct)) return Results.Forbid();
        if (!await ReadableAsync(tenantId, investigationId, http, authority, store, ct)) return Results.Forbid();
        await store.AcceptMessageAsync(tenantId, investigationId, request, Actor(http), Credential(http), false, ct);
        return Results.Ok(await store.LoadAsync(tenantId, investigationId, 0, ct));
    }

    internal static async Task<IResult> ApprovalAsync(int tenantId, Guid investigationId, string callId, InvestigationApprovalRequest request,
        HttpContext http, NetclawAuthority authority, InvestigationStore store, NetclawInvestigationWorker worker, CancellationToken ct)
    {
        if (!await authority.CanInvokeAsync(tenantId, Actor(http), Credential(http), false, ct)) return Results.Forbid();
        if (!await ReadableAsync(tenantId, investigationId, http, authority, store, ct)) return Results.Forbid();
        var messageId = await store.AcceptApprovalAsync(tenantId, investigationId, callId, request.SelectedKey, Actor(http), ct);
        await worker.RespondAsync(tenantId, investigationId, messageId, callId, request.SelectedKey, Actor(http), Credential(http), ct);
        return Results.Ok(await store.LoadAsync(tenantId, investigationId, 0, ct));
    }

    internal static async Task<IResult> ReconcileAsync(int tenantId, Guid investigationId, HttpContext http,
        NetclawAuthority authority, InvestigationStore store, NetclawInvestigationWorker worker, CancellationToken ct)
    {
        if (!await authority.CanInvokeAsync(tenantId, Actor(http), Credential(http), false, ct)) return Results.Forbid();
        if (!await ReadableAsync(tenantId, investigationId, http, authority, store, ct)) return Results.Forbid();
        await worker.ReconcileAsync(tenantId, investigationId, Actor(http), ct);
        return Results.Ok(await store.LoadAsync(tenantId, investigationId, 0, ct));
    }

    internal static async Task<IResult> ArchiveAsync(int tenantId, Guid investigationId, InvestigationArchiveRequest request,
        HttpContext http, IEffectiveAccessService access, InvestigationStore store, NetclawInvestigationWorker worker, CancellationToken ct)
    {
        if (!await AllowedAsync(tenantId, NetRatelPermissions.ClientManagement, http, access, ct) ||
            !await AllowedAsync(tenantId, NetRatelPermissions.TelemetryRead, http, access, ct)) return Results.Forbid();
        if (await store.HasAlertAsync(tenantId, investigationId, ct) && !await AllowedAsync(tenantId, NetRatelPermissions.MonitoringRead, http, access, ct)) return Results.Forbid();
        await store.ArchiveAsync(tenantId, investigationId, request, Actor(http), ct); await worker.RetireAsync(investigationId);
        return Results.Ok(await store.LoadAsync(tenantId, investigationId, 0, ct));
    }

    private static Task<bool> AllowedAsync(int tenant, string permission, HttpContext http, IEffectiveAccessService access, CancellationToken ct) =>
        tenant > 0 ? access.AuthorizeAsync(http.User, permission, tenant, ct) : Task.FromResult(false);
    private static async Task<bool> ReadableAsync(int tenant, Guid id, HttpContext http, NetclawAuthority authority, InvestigationStore store, CancellationToken ct) =>
        !await store.HasAlertAsync(tenant, id, ct) || await authority.CanReadAlertAsync(tenant, Actor(http), Credential(http), ct);
    internal static string Actor(HttpContext http) => http.User.FindFirst("netratel_principal_id")?.Value ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private static string? Credential(HttpContext http) => http.User.FindFirst("netratel_integration_credential_id")?.Value;
    private static Task AuditAsync(IEventRecorder audit, int tenantId, string actor, string action, string id, CancellationToken ct) =>
        audit.RecordAsync(new DomainEvent { EventType = action, Source = "Netclaw", EntityId = id, CorrelationId = Guid.NewGuid().ToString("N"), TenantId = tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Message = action, Payload = new { Actor = actor, TenantId = tenantId } }, ct);
    internal static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action, CancellationToken requestAborted)
    {
        try { return await action(); }
        catch (InvestigationConflictException exception) { return Results.Conflict(new { code = "investigation-conflict", message = exception.Message }); }
        catch (NetclawPairingException exception) { return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode); }
        catch (KeyNotFoundException) { return Results.NotFound(new { code = "investigation-not-found" }); }
        catch (ArgumentException exception) { return Results.BadRequest(new { code = "invalid-investigation-request", message = exception.Message }); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "connection-changed", message = "The connection changed. Refresh it before trying again." }); }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        { return Results.Json(new { code = "netclaw-operation-timeout", message = "The operation timed out. Reload its saved state before retrying." }, statusCode: 504); }
    }
}

public sealed class NetclawErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        await NetclawEndpoints.ExecuteAsync(async () => (IResult)(await next(context))!, context.HttpContext.RequestAborted);
}
