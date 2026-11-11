using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Endpoints;

public static partial class InternalEndpoints
{
    private static async Task<IResult> ReadManagedExecution(
        int orchestrationRequestId, string orchestrationRunId,
        [FromHeader(Name = "X-RatelDesk-Request-Id")] string requestId,
        [FromHeader(Name = "X-RatelDesk-Task-Id")] string requestTaskId,
        [FromHeader(Name = "X-Correlation-Id")] string correlationId,
        HttpContext http, OrchestratorDbContext db, IRequestService requests,
        IJobRunService runs, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (orchestrationRequestId <= 0 ||
            !ulong.TryParse(orchestrationRunId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId) ||
            runId == 0 || runId > long.MaxValue || orchestrationRunId != runId.ToString(CultureInfo.InvariantCulture) ||
            new[] { requestId, requestTaskId, correlationId }.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)))
            return AutomationError(http, 400, "read-execution", "invalid_execution_identity");

        var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.ReadScope, ct);
        if (principal?.LinkId is not { Length: > 0 } mappingId)
            return AutomationError(http, 403, "read-execution", "access_denied");
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.RequestId == orchestrationRequestId && row.ExecutionId == orchestrationRunId &&
            row.ParentRequestId == requestId && row.RequestTaskId == requestTaskId && row.CorrelationId == correlationId &&
            row.ServicePrincipalId == principal.Id && row.TenantId == principal.TenantId &&
            row.LinkId == principal.LinkId && row.LinkRevision == principal.LinkRevision && row.GrantHash == principal.GrantHash &&
            row.PeerInstanceId == principal.PeerInstanceId && row.PeerTenantId == principal.PeerTenantId, ct);
        if (binding is null) return Results.NotFound();

        PairingBusinessProfile profile;
        try
        {
            profile = await http.RequestServices.GetRequiredService<PairingBusinessProfileService>()
                .ResolveAsync(principal.TenantId, mappingId, "rateldesk.orchestration.callback", ct);
        }
        catch (PairingException)
        {
            return AutomationError(http, 403, "read-execution", "access_denied");
        }
        if (profile.Revision != binding.LinkRevision || profile.AuthorityHash != binding.GrantHash ||
            profile.Peer.InstallationId != binding.PeerInstanceId || profile.Mapping.RatelDeskOrganizationId != binding.PeerTenantId)
            return AutomationError(http, 403, "read-execution", "access_denied");

        // Reads never invoke admission recovery, actor start, callback replay or
        // request projection. A partial/mismatched record remains unconfirmed.
        // Ordinary detail readers archive unprotected legacy data on read. Reject
        // such rows before calling them so reconciliation cannot mutate history
        // or turn a previously unproven terminal hash into an accepted result.
        if (!await db.Requests.AsNoTracking().AnyAsync(row => row.Id == orchestrationRequestId
                && row.ProtectedJobInputsJson != null, ct)
            || !await db.JobRuns.AsNoTracking().AnyAsync(row => row.Id == (long)runId
                && row.ProtectedExecutionJson != null, ct))
            return Results.NotFound();
        var request = await requests.GetAsync(orchestrationRequestId, ct);
        var details = await runs.GetDetailsAsync(runId, ct);
        if (request is null || details is null || !OrchestrationCallbackProjection.Matches(request, details.Run, binding))
            return Results.NotFound();
        var ready = await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct);
        var status = !ready ? "unknown" : details.Run.Status switch
        {
            JobRunState.Pending => "pending",
            JobRunState.Running => "running",
            JobRunState.Succeeded => "succeeded",
            JobRunState.Failed or JobRunState.TimedOut => "failed",
            JobRunState.Cancelled => "cancelled",
            JobRunState.Unknown => "unknown",
            _ => "unknown"
        };
        return Results.Ok(new
        {
            requestId = binding.ParentRequestId, requestTaskId = binding.RequestTaskId, correlationId = binding.CorrelationId,
            orchestrationRequestId = binding.RequestId.ToString(CultureInfo.InvariantCulture),
            orchestrationRunId = binding.ExecutionId, executionId = binding.ExecutionId,
            status, startedAtUtc = details.Run.StartedAtUtc,
            completedAtUtc = ready && status is not ("pending" or "running" or "unknown") ? details.Run.CompletedAtUtc : null,
            message = status switch
            {
                "succeeded" => "Provider confirmed successful completion.",
                "failed" when details.Run.Status == JobRunState.TimedOut => "Provider confirmed an execution timeout.",
                "failed" => "Provider confirmed an unsuccessful terminal result.",
                "cancelled" => "Provider confirmed cancellation.",
                "pending" => "Provider admission is pending execution.",
                "running" => "Provider execution is in progress.",
                _ => "Provider outcome has not been confirmed. Reconciliation is required."
            }
        });
    }
}
