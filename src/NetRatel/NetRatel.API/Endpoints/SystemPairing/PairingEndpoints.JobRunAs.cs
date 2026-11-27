using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Endpoints.SystemPairing;

public static partial class PairingEndpoints
{
    private static void MapJobRunAs(RouteGroupBuilder local)
    {
        var grants = local.MapGroup("/mappings/{mappingId:guid}/jobs/{jobId:long}/run-as");
        grants.MapGet("", (Guid mappingId, ulong jobId, HttpContext http, ManagedJobRunAsGrants approvals,
            JobAggregateOrchestrator jobs, CancellationToken ct) => ExecuteAsync(http, false, async () =>
        {
            var principal = await approvals.RequireAdministratorAsync(mappingId, http.User, ct);
            var plan = await jobs.PlanManagedRunAsAsync(jobId, principal, ct);
            return Results.Ok(await approvals.ReviewAsync(principal, plan, ct));
        }));

        grants.MapPut("", (Guid mappingId, ulong jobId, ApprovePairedJobRunAsRequest request, HttpContext http,
            ManagedJobRunAsGrants approvals, JobAggregateOrchestrator jobs, OrchestratorDbContext db,
            CancellationToken ct) => ExecuteAsync(http, true, async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var principal = await approvals.RequireAdministratorAsync(mappingId, http.User, ct);
            var plan = await jobs.PlanManagedRunAsAsync(jobId, principal, ct);
            await approvals.ApproveAsync(principal, plan, request.ScopeDigest, http.User, ct);
            var current = await approvals.RequireAdministratorAsync(mappingId, http.User, ct);
            if (current.Id != principal.Id || current.LinkRevision != principal.LinkRevision || current.GrantHash != principal.GrantHash)
                throw new PairingException(409, "run_as_review_changed", "The paired connection changed during review.");
            var result = await approvals.ReviewAsync(current, plan, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(result);
        }));

        grants.MapDelete("/{approvalId:guid}", (Guid mappingId, ulong jobId, Guid approvalId, HttpContext http,
            ManagedJobRunAsGrants approvals, CancellationToken ct) => ExecuteAsync(http, true, async () =>
        {
            var principal = await approvals.RequireAdministratorAsync(mappingId, http.User, ct);
            return await approvals.RevokeAsync(mappingId, jobId, principal.TenantId, approvalId, ct) == 1
                ? Results.NoContent() : Results.Conflict(new { code = "run_as_review_changed" });
        }));
    }
}
