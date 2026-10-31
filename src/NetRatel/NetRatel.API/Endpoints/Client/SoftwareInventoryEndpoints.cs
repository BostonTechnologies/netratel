using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static class SoftwareInventoryEndpoints
{
    public static IEndpointRouteBuilder MapSoftwareInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/software-inventory/tenants", async (int? page, int? pageSize, HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var skip = page ?? 0;
            var size = pageSize ?? 100;
            if (skip < 0 || skip > 100_000 || size < 1 || size > 100) return Results.BadRequest();
            var scope = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.TelemetryRead, ct);
            scope = NetRatel.API.Security.Authorization.MonitoringFlowPermissionAuthorization.RestrictDiscoveryTenantIds(http, NetRatelPermissions.TelemetryRead, scope);
            var tenants = db.Tenants.AsNoTracking().Where(row => scope == null || scope.Contains(row.Id));
            var total = await tenants.CountAsync(ct);
            var items = await tenants.OrderBy(row => row.Name).ThenBy(row => row.Id).Skip(skip * size).Take(size)
                .Select(row => new SoftwareInventoryTenant(row.Id, row.Name)).ToListAsync(ct);
            return Results.Ok(new SoftwarePage<SoftwareInventoryTenant>(items, total, skip, size));
        }).RequireAuthorization("SoftwareInventoryDiscovery").WithTags("Software Inventory");
        var tenants = app.MapGroup("/api/v2/tenants/{tenantId:int}/software-inventory").WithTags("Software Inventory");
        tenants.MapGet("/policy", PolicyAsync).RequireAuthorization("TelemetryReader");
        tenants.MapPut("/policy", SetPolicyAsync).RequireAuthorization("TenantAdministrator");
        tenants.MapGet("/installations", InstallationsAsync).RequireAuthorization("TelemetryReader");
        tenants.MapGet("/aggregate", AggregateAsync).RequireAuthorization("TelemetryReader");
        tenants.MapGet("/changes", ChangesAsync).RequireAuthorization("TelemetryReader");
        tenants.MapGet("/export.csv", ExportAsync).RequireAuthorization("TelemetryReader");
        var clients = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/software-inventory").WithTags("Software Inventory");
        clients.MapGet("", StatusAsync).RequireAuthorization("TelemetryReader");
        clients.MapGet("/scans/{collectionId:guid}", ScanAsync).RequireAuthorization("TelemetryReader");
        clients.MapGet("/snapshot", SnapshotAsync).RequireAuthorization("TelemetryReader");
        clients.MapGet("/changes", ClientChangesAsync).RequireAuthorization("TelemetryReader");
        clients.MapPost("/refresh", RefreshAsync).RequireAuthorization("ClientManager");
        var agent = app.MapGroup("/api/v2/agent/software-inventory").WithTags("Agent Software Inventory").RequireAuthorization("AgentGatewayAccess");
        agent.MapPost("/work", WorkAsync);
        agent.MapPost("/chunks", UploadAsync).WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024));
        return app;
    }

    internal static Task<IResult> PolicyAsync(int tenantId, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, null, NetRatelPermissions.TelemetryRead, "policy.read", http, async () => Results.Ok(await service.PolicyAsync(tenantId, ct)), ct);

    internal static Task<IResult> SetPolicyAsync(int tenantId, SoftwareInventoryPolicy policy, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, null, NetRatelPermissions.TenantAdministration, "policy.update", http, async () =>
        {
            await service.SetPolicyAsync(tenantId, policy, ct);
            return Results.Ok(policy);
        }, ct);

    internal static Task<IResult> StatusAsync(int tenantId, Guid agentId, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, agentId, NetRatelPermissions.TelemetryRead, "status.read", http, async () =>
        {
            var model = await service.StatusAsync(tenantId, agentId, ct);
            var access = http.RequestServices.GetRequiredService<IEffectiveAccessService>();
            return Results.Ok(model with { CanRefresh = await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, tenantId, ct) });
        }, ct);

    internal static Task<IResult> ScanAsync(int tenantId, Guid agentId, Guid collectionId, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, agentId, NetRatelPermissions.TelemetryRead, "scan.read", http, async () =>
            await service.ScanAsync(tenantId, agentId, collectionId, ct) is { } scan ? Results.Ok(scan) : Results.NotFound(), ct);

    internal static Task<IResult> RefreshAsync(int tenantId, Guid agentId, SoftwareRefreshRequest request, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, agentId, NetRatelPermissions.ClientManagement, "scan.request", http,
            async () => Results.Ok((await service.RefreshAsync(tenantId, agentId, request.Force, ct)) with { CanRefresh = true }), ct);

    private static Task<IResult> SnapshotAsync(int tenantId, Guid agentId, [AsParameters] SoftwareInventoryQuery query,
        HttpContext http, SoftwareInventoryService service, CancellationToken ct) => SnapshotCoreAsync(tenantId, agentId, query, http, service, ct);

    private static Task<IResult> SnapshotCoreAsync(int tenantId, Guid agentId, SoftwareInventoryQuery query,
        HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, agentId, NetRatelPermissions.TelemetryRead, "snapshot.read", http,
            async () => Results.Ok(await service.InstallationsAsync(tenantId, query with { AgentId = agentId }, ct)), ct);

    private static Task<IResult> InstallationsAsync(int tenantId, [AsParameters] SoftwareInventoryQuery query,
        HttpContext http, SoftwareInventoryService service, CancellationToken ct) => InstallationsCoreAsync(tenantId, query, false, http, service, ct);

    private static Task<IResult> AggregateAsync(int tenantId, [AsParameters] SoftwareInventoryQuery query,
        HttpContext http, SoftwareInventoryService service, CancellationToken ct) => InstallationsCoreAsync(tenantId, query, true, http, service, ct);

    private static Task<IResult> InstallationsCoreAsync(int tenant, SoftwareInventoryQuery query, bool aggregate,
        HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenant, query.AgentId, NetRatelPermissions.TelemetryRead, aggregate ? "aggregate.read" : "installations.read", http,
            async () => aggregate ? Results.Ok(await service.AggregateAsync(tenant, query, ct)) : Results.Ok(await service.InstallationsAsync(tenant, query, ct)), ct);

    private static Task<IResult> ChangesAsync(int tenantId, int? page, int? pageSize, Guid? agentId, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ChangesCoreAsync(tenantId, agentId, page ?? 0, pageSize ?? 50, http, service, ct);

    private static Task<IResult> ClientChangesAsync(int tenantId, Guid agentId, int? page, int? pageSize, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ChangesCoreAsync(tenantId, agentId, page ?? 0, pageSize ?? 50, http, service, ct);

    private static Task<IResult> ChangesCoreAsync(int tenant, Guid? agent, int page, int size, HttpContext http, SoftwareInventoryService service, CancellationToken ct) =>
        ExecuteAsync(tenant, agent, NetRatelPermissions.TelemetryRead, "changes.read", http,
            async () => Results.Ok(await service.ChangesAsync(tenant, agent, page, size, ct)), ct);

    private static async Task ExportAsync(int tenantId, [AsParameters] SoftwareInventoryQuery query, HttpContext http,
        SoftwareInventoryService service, CancellationToken ct)
    {
        var authorization = await ExecuteAsync(tenantId, query.AgentId, NetRatelPermissions.TelemetryRead, "export", http,
            () => { query.Validate(); return Task.FromResult<IResult>(Results.Ok()); }, ct);
        if (authorization is not Microsoft.AspNetCore.Http.HttpResults.Ok)
        {
            await authorization.ExecuteAsync(http);
            return;
        }
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = "attachment; filename=software-inventory.csv";
        await http.Response.WriteAsync("Client,AgentId,Name,Version,Publisher,Architecture,Source,PackageId,OS,DetectedAtUtc,LastSuccessfulScanUtc\r\n", ct);
        await foreach (var row in service.ExportAsync(tenantId, query, ct))
                await http.Response.WriteAsync(string.Join(',', new[] { row.Client, row.AgentId.ToString(), row.Package.Name, row.Package.Version,
                    row.Package.Publisher, row.Package.Architecture, row.Package.Source, row.Package.PackageId, row.Os,
                    row.DetectedAtUtc.ToString("O"), row.LastSuccessfulScanUtc.ToString("O") }.Select(CsvCell)) + "\r\n", ct);
    }

    internal static string CsvCell(string value)
    {
        // Quoting alone does not prevent spreadsheet formula evaluation.
        var safe = value.TrimStart().StartsWithAnyFormulaCharacter() ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    private static bool StartsWithAnyFormulaCharacter(this string value) => value.Length > 0 && value[0] is '=' or '+' or '-' or '@';

    private static async Task<IResult> WorkAsync(HttpContext http, OrchestratorDbContext db, SoftwareInventoryService service, CancellationToken ct)
    {
        var identity = await AgentAsync(http, db, ct);
        return identity is null ? Results.Forbid() : Results.Ok(await service.WorkAsync(identity.TenantId, identity.AgentId, ct));
    }

    private static async Task<IResult> UploadAsync(SoftwareChunkUpload upload, HttpContext http, OrchestratorDbContext db, SoftwareInventoryService service, CancellationToken ct)
    {
        var identity = await AgentAsync(http, db, ct);
        if (identity is null) return Results.Forbid();
        try
        {
            var ack = await service.UploadAsync(identity.TenantId, identity.AgentId, upload, ct);
            return ack.Accepted ? Results.Ok(ack) : Results.Conflict(ack);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or JsonException)
        { return Results.Problem(statusCode: 400, title: "invalid_inventory_chunk"); }
    }

    private static async Task<AuthenticatedAgentIdentity?> AgentAsync(HttpContext http, OrchestratorDbContext db, CancellationToken ct)
    {
        if (!AgentGatewayIdentityResolver.TryResolve(http.User, out var identity, out _)) return null;
        return await db.Agents.AsNoTracking().AnyAsync(row => row.Id == identity!.AgentId && row.TenantId == identity.TenantId &&
            row.IsEnabled && row.Status == AgentStatus.Active && row.RevokedAtUtc == null, ct) ? identity : null;
    }

    private static async Task<IResult> ExecuteAsync(int tenant, Guid? agent, string permission, string action, HttpContext http,
        Func<Task<IResult>> execute, CancellationToken ct)
    {
        var access = http.RequestServices.GetRequiredService<IEffectiveAccessService>();
        if (tenant <= 0 || !await access.AuthorizeAsync(http.User, permission, tenant, ct)) return Results.Forbid();
        var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
        if (!await db.Tenants.AnyAsync(row => row.Id == tenant, ct) ||
            (agent.HasValue && !await db.Agents.AnyAsync(row => row.TenantId == tenant && row.Id == agent, ct))) return Results.NotFound();
        try
        {
            await http.RequestServices.GetRequiredService<IEventRecorder>().RecordAsync(new DomainEvent
            {
                EventType = "SoftwareInventory." + action, Source = "SoftwareInventory", TenantId = tenant.ToString(),
                EntityId = agent?.ToString() ?? tenant.ToString(), Severity = "Info", CorrelationId = http.TraceIdentifier,
                Message = "Software inventory " + action,
                Payload = new { actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"), agentId = agent }
            }, ct);
            return await execute();
        }
        catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "invalid_inventory_request", detail: error.Message); }
    }

    internal static Task<IResult> ManagedAsync(ManagedOperationDescriptor descriptor, JsonElement request, HttpContext http, CancellationToken ct)
    {
        var tenant = request.GetProperty("tenantId").GetInt32();
        var agent = descriptor.AgentTarget ? request.GetProperty("agentId").GetGuid() : (Guid?)null;
        var service = http.RequestServices.GetRequiredService<SoftwareInventoryService>();
        T Data<T>() => request.GetProperty("data").Deserialize<T>(ManagedOperationCatalog.JsonOptions)!;
        return descriptor.Operation switch
        {
            "policy" => PolicyAsync(tenant, http, service, ct),
            "set_policy" => SetPolicyAsync(tenant, Data<SoftwareInventoryPolicy>(), http, service, ct),
            "status" => StatusAsync(tenant, agent!.Value, http, service, ct),
            "scan_status" => ScanAsync(tenant, agent!.Value, request.GetProperty("collectionId").GetGuid(), http, service, ct),
            "refresh" => RefreshAsync(tenant, agent!.Value, Data<SoftwareRefreshRequest>(), http, service, ct),
            "snapshot" => SnapshotCoreAsync(tenant, agent!.Value, Data<SoftwareInventoryQuery>(), http, service, ct),
            "installations" or "aggregate" => InstallationsCoreAsync(tenant, Data<SoftwareInventoryQuery>(), descriptor.Operation == "aggregate", http, service, ct),
            "changes" => ChangesCoreAsync(tenant, agent, Data<SoftwareInventoryQuery>().Page, Data<SoftwareInventoryQuery>().PageSize, http, service, ct),
            "estate_changes" => ChangesCoreAsync(tenant, null, Data<SoftwareInventoryQuery>().Page, Data<SoftwareInventoryQuery>().PageSize, http, service, ct),
            _ => throw new InvalidOperationException("Unmapped software inventory operation.")
        };
    }
}
