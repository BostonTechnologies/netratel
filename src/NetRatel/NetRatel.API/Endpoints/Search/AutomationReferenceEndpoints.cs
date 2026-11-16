using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Search;

public static class AutomationReferenceEndpoints
{
    public static void MapAutomationReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/global-search/automation", async (string? q, int? page, int? pageSize, HttpContext http,
            OrchestratorDbContext db, IEffectiveAccessService access, CancellationToken ct) =>
        {
            var term = (q ?? "").Trim().ToUpperInvariant();
            if (term.Length is < 2 or > 128) return Results.Ok(new PagedResult<AutomationResourceSearchDto>([], 1, 25, 0));
            var jobs = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.JobManagement, ct);
            var flows = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.FlowRead, ct);
            var query = Visible(db, jobs, flows).Where(x => x.NormalizedReference.StartsWith(term));
            return Results.Ok(await PageAsync(db, query, term, page, pageSize, ct));
        }).RequireAuthorization("InteractiveAccount").WithTags("Global Search");
        app.MapGet("/api/v1/tenants/{tenantId:int}/automation-resources/{kind}/{id}", async (int tenantId, string kind,
            string id, int? page, int? pageSize, HttpContext http, OrchestratorDbContext db, IEffectiveAccessService access, CancellationToken ct) =>
        {
            if (kind is not ("request" or "job-run" or "flow-run") || id.Length > 256) return Results.BadRequest();
            var jobs = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.JobManagement, ct);
            var flows = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.FlowRead, ct);
            var visible = Visible(db, jobs, flows);
            var seeds = visible.Where(x => x.TenantId == tenantId && x.ResourceKind == kind && x.ResourceId == id);
            if (!await seeds.AnyAsync(ct)) return Results.Ok(new PagedResult<AutomationResourceSearchDto>([], 1, 25, 0));
            var related = visible.Where(target => seeds.Any(source => source.TenantId == target.TenantId &&
                source.PeerInstanceId == target.PeerInstanceId && source.PeerOrganizationId == target.PeerOrganizationId &&
                source.ReferenceKind == target.ReferenceKind && source.ExternalId == target.ExternalId));
            return Results.Ok(await PageAsync(db, related, "", page, pageSize, ct));
        }).RequireAuthorization().WithTags("Automation related resources");
        app.MapGet("/api/v1/tenants/{tenantId:int}/automation-resources/{kind}/{id}/origins", async (int tenantId,
            string kind, string id, HttpContext http, OrchestratorDbContext db, IEffectiveAccessService access, CancellationToken ct) =>
        {
            if (kind is not ("request" or "job-run" or "flow-run") || id.Length > 256) return Results.BadRequest();
            var jobs = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.JobManagement, ct);
            var flows = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.FlowRead, ct);
            var seeds = Visible(db, jobs, flows).Where(x => x.TenantId == tenantId && x.ResourceKind == kind && x.ResourceId == id);
            var bindings = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking()
                .Where(binding => binding.TenantId == tenantId && binding.OriginContextJson != null && seeds.Any(seed =>
                    seed.NativeRequestId == binding.RequestId && seed.PeerInstanceId == binding.PeerInstanceId && seed.PeerOrganizationId == binding.PeerTenantId))
                .OrderBy(x => x.RequestId).Take(25).ToArrayAsync(ct);
            var origins = new List<AutomationTrace.Contracts.AutomationOriginContext>();
            foreach (var binding in bindings)
            {
                try
                {
                    var origin = System.Text.Json.JsonSerializer.Deserialize<AutomationTrace.Contracts.AutomationOriginContext>(
                        binding.OriginContextJson!, AutomationTrace.AutomationTraceValidation.Json);
                    if (AutomationTrace.AutomationTraceValidation.Origin(origin) && origin!.SourceInstanceId == binding.PeerInstanceId &&
                        origin.Organization.Id == binding.PeerTenantId && origin.Request.Id == binding.ParentRequestId &&
                        origin.Task.Id == binding.RequestTaskId && origin.AttemptId == binding.CorrelationId) origins.Add(origin);
                }
                catch (System.Text.Json.JsonException) { return Results.Problem(statusCode: 503, title: "Retained source context is unavailable."); }
            }
            return Results.Ok(origins.DistinctBy(x => new { x.SourceInstanceId, x.Task.Id, x.AttemptId }));
        }).RequireAuthorization().WithTags("Automation related resources");
    }
    internal static IQueryable<ExternalResourceReferenceRecord> Visible(OrchestratorDbContext db, int[]? jobTenants, int[]? flowTenants) =>
        db.Set<ExternalResourceReferenceRecord>().AsNoTracking().Where(row =>
            row.ResourceKind == "request" && (jobTenants == null || jobTenants.Contains(row.TenantId)) &&
                db.Requests.Any(request => request.Id == row.NativeRequestId && request.TargetTenantId == row.TenantId) ||
            row.ResourceKind == "job-run" && (jobTenants == null || jobTenants.Contains(row.TenantId)) &&
                db.JobRuns.Any(run => run.Id == row.JobRunId && run.TenantId == row.TenantId && run.ParentRunId == null) ||
            row.ResourceKind == "flow-run" && (flowTenants == null || flowTenants.Contains(row.TenantId)) &&
                db.FlowRuns.Any(run => run.Id == row.FlowRunId && run.TenantId == row.TenantId &&
                    db.FlowDefinitions.Any(definition => definition.Id == run.FlowId && definition.TenantId == row.TenantId && definition.DeletedAtUtc == null)));

    private static async Task<PagedResult<AutomationResourceSearchDto>> PageAsync(OrchestratorDbContext db,
        IQueryable<ExternalResourceReferenceRecord> query, string term, int? page, int? pageSize, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? 6, 1, 25); var number = Math.Clamp(page ?? 1, 1, 4000);
        var grouped = query.GroupBy(x => new { x.TenantId, x.PeerInstanceId, x.PeerOrganizationId, x.ResourceKind, x.ResourceId });
        var count = await grouped.CountAsync(ct);
        var winners = grouped.Select(g => g.OrderBy(x => x.NormalizedReference == term ? 0 : 1)
            .ThenBy(x => x.ReferenceKind).ThenBy(x => x.Reference).ThenBy(x => x.Id).Select(x => x.Id).First());
        var rows = await query.Where(x => winners.Contains(x.Id))
            .OrderBy(x => x.NormalizedReference == term ? 0 : 1).ThenBy(x => x.NormalizedReference)
            .ThenBy(x => x.TenantId).ThenBy(x => x.ResourceKind).ThenBy(x => x.ResourceId)
            .Skip((number - 1) * size).Take(size).ToArrayAsync(ct);
        var items = rows.Select(row => new AutomationResourceSearchDto(row.TenantId, row.ResourceKind, row.ResourceId,
            row.ResourceKind switch
            {
                "request" => "Request " + row.ResourceId + " — " + row.Reference,
                "job-run" => "Job execution — " + row.SafeTitle,
                _ => "Flow run — " + row.Reference
            }, row.Reference, row.ReferenceKind, row.PeerInstanceId, row.PeerOrganizationId,
            row.ResourceKind switch
            {
                "request" => "/requests?search=" + row.ResourceId,
                "job-run" => "/jobs/" + row.JobDefinitionId + "/runs/" + row.ResourceId,
                _ => "/flows?tenantId=" + row.TenantId + "&run=" + row.ResourceId
            }, row.ExternalUrl)).ToArray();
        return new(items, number, size, count);
    }
}
