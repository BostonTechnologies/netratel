using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Administrative, audited PostgreSQL authority for the one-to-one bridge
/// between gateway AgentId records and historical primary-client identities.
/// These endpoints never derive identity from hostname or gateway hello
/// diagnostics.
/// </summary>
public static class PrimaryClientAgentBindingEndpoints
{
    /// <summary>
    /// Publishes the authenticated, read-only binding authority. These routes
    /// are safe to expose before the separate Dev operational policy is ready.
    /// </summary>
    public static IEndpointRouteBuilder MapPrimaryClientAgentBindingReadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:int}/primary-client-bindings")
            .WithTags("Primary Client Bindings")
            .RequireAuthorization("Operator");

        group.MapGet("diagnostics", async (
            int tenantId,
            IPrimaryClientAgentBindingService bindings,
            CancellationToken ct) =>
            Results.Ok(await bindings.GetDiagnosticsAsync(tenantId, ct).ConfigureAwait(false)))
            .Produces<PrimaryClientAgentBindingDiagnosticsDto>(StatusCodes.Status200OK);

        group.MapGet("agents/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            IPrimaryClientAgentBindingService bindings,
            CancellationToken ct) =>
        {
            var binding = await bindings.GetByAgentAsync(tenantId, agentId, ct).ConfigureAwait(false);
            return binding is null ? Results.NotFound() : Results.Ok(binding);
        })
        .Produces<PrimaryClientAgentBindingDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Registers binding-management routes only when a caller has deliberately
    /// composed the Dev operational authority. They remain unregistered by the
    /// normal API bootstrap until that policy and its audit contract exist.
    /// </summary>
    public static IEndpointRouteBuilder MapPrimaryClientAgentBindingManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/tenants/{tenantId:int}/primary-client-bindings")
            .WithTags("Primary Client Bindings")
            .RequireAuthorization("Operator");

        group.MapPost("issue-pending", async (
            int tenantId,
            IssuePrimaryClientEnrollmentRequest request,
            HttpContext http,
            IEnrollmentCodeIssueService enrollmentCodes,
            CancellationToken ct) =>
        {
            var validation = ValidatePrimaryClient(request.PrimaryClientIdentity);
            if (validation.Error is not null)
            {
                return validation.Error;
            }

            try
            {
                var issued = await enrollmentCodes.IssueForPrimaryClientBindingAsync(
                    new(
                        tenantId,
                        validation.CanonicalIdentity!,
                        request.ValidForMinutes,
                        OperatorName(http),
                        request.Notes),
                    ct).ConfigureAwait(false);
                return Results.Ok(issued);
            }
            catch (AgentAuthException ex)
            {
                return Results.Problem(
                    ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: ex.Code is null ? null : new Dictionary<string, object?> { ["code"] = ex.Code });
            }
        })
        .Produces<PrimaryClientEnrollmentIssueResult>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("pending", async (
            int tenantId,
            CreatePendingPrimaryClientAgentBindingRequest request,
            HttpContext http,
            IPrimaryClientAgentBindingService bindings,
            CancellationToken ct) =>
        {
            var validation = ValidatePrimaryClient(request.PrimaryClientIdentity);
            if (validation.Error is not null)
            {
                return validation.Error;
            }

            var result = await bindings.CreatePendingAsync(
                new(
                    tenantId,
                    request.EnrollmentCodeId,
                    validation.CanonicalIdentity!,
                    OperatorName(http),
                    request.Notes),
                ct).ConfigureAwait(false);
            return ToResult(result);
        })
        .Produces<PrimaryClientAgentBindingDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("manual-repair", async (
            int tenantId,
            CreateManualPrimaryClientAgentBindingRequest request,
            HttpContext http,
            IPrimaryClientAgentBindingService bindings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { code = "manual_repair_reason_required", message = "A reason is required for an audited manual repair." });
            }

            var validation = ValidatePrimaryClient(request.PrimaryClientIdentity);
            if (validation.Error is not null)
            {
                return validation.Error;
            }

            var result = await bindings.CreateManualRepairAsync(
                new(
                    tenantId,
                    request.AgentId,
                    validation.CanonicalIdentity!,
                    OperatorName(http),
                    request.Reason),
                ct).ConfigureAwait(false);
            return ToResult(result);
        })
        .Produces<PrimaryClientAgentBindingDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("{bindingId:guid}/revoke", async (
            int tenantId,
            Guid bindingId,
            RevokePrimaryClientAgentBindingRequest request,
            HttpContext http,
            IPrimaryClientAgentBindingService bindings,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { code = "binding_revoke_reason_required", message = "A revoke reason is required." });
            }

            var revoked = await bindings.RevokeAsync(tenantId, bindingId, OperatorName(http), request.Reason, ct).ConfigureAwait(false);
            return revoked ? Results.NoContent() : Results.NotFound();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static IResult ToResult(PrimaryClientAgentBindingResult result) => result.Disposition switch
    {
        PrimaryClientAgentBindingDisposition.Created or PrimaryClientAgentBindingDisposition.AlreadyBound => Results.Ok(result),
        PrimaryClientAgentBindingDisposition.EnrollmentCodeNotFound or PrimaryClientAgentBindingDisposition.AgentNotFound =>
            Results.NotFound(new { code = ToCode(result.Disposition), binding = result.Binding }),
        _ => Results.Conflict(new { code = ToCode(result.Disposition), binding = result.Binding })
    };

    private static PrimaryClientValidation ValidatePrimaryClient(string primaryClientIdentity)
    {
        try
        {
            return new(PrimaryClientAgentBindingService.NormalizePrimaryClientIdentity(primaryClientIdentity), null);
        }
        catch (ArgumentException)
        {
            return new(null, Results.BadRequest(new { code = "invalid_primary_client_identity", message = "Primary client identity must be a valid NetRatel client identity." }));
        }
    }

    private static string OperatorName(HttpContext http) =>
        http.User.Identity?.Name
        ?? http.User.FindFirst("preferred_username")?.Value
        ?? http.User.FindFirst("sub")?.Value
        ?? "unknown";

    private static string ToCode(PrimaryClientAgentBindingDisposition disposition) => disposition switch
    {
        PrimaryClientAgentBindingDisposition.EnrollmentCodeMustBeSingleUse => "enrollment_code_must_be_single_use",
        PrimaryClientAgentBindingDisposition.AgentAlreadyBound => "agent_already_bound",
        PrimaryClientAgentBindingDisposition.PrimaryClientAlreadyBound => "primary_client_already_bound",
        PrimaryClientAgentBindingDisposition.EnrollmentCodeAlreadyBound => "enrollment_code_already_bound",
        PrimaryClientAgentBindingDisposition.BindingNotPending => "binding_not_pending",
        PrimaryClientAgentBindingDisposition.TenantMismatch => "tenant_mismatch",
        _ => disposition.ToString().ToLowerInvariant()
    };

    private sealed record PrimaryClientValidation(string? CanonicalIdentity, IResult? Error);
}

public sealed record CreatePendingPrimaryClientAgentBindingRequest(
    Guid EnrollmentCodeId,
    string PrimaryClientIdentity,
    string? Notes);

public sealed record IssuePrimaryClientEnrollmentRequest(
    string PrimaryClientIdentity,
    int ValidForMinutes,
    string? Notes);

public sealed record CreateManualPrimaryClientAgentBindingRequest(
    Guid AgentId,
    string PrimaryClientIdentity,
    string Reason);

public sealed record RevokePrimaryClientAgentBindingRequest(string Reason);
