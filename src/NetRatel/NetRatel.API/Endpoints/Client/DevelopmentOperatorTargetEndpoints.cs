using System.Security.Claims;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Development-only administration of explicit remote-operation targets. The
/// endpoints are absent in non-Development environments and accept only the
/// persisted V2 Agent identifier; they never classify targets from a hostname.
/// </summary>
public static class DevelopmentOperatorTargetEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentOperatorTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/dev-operator-targets")
            .WithTags("Development Operator Targets")
            .RequireAuthorization("Operator");

        group.MapGet("/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            IDevelopmentOperatorTargetAuthority targets,
            CancellationToken cancellationToken) =>
        {
            var grant = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            return grant is null ? Results.NotFound() : Results.Ok(grant);
        })
        .Produces<DevelopmentOperatorTargetGrantView>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/grants", async (
            DevelopmentOperatorTargetGrantRequestBody request,
            HttpContext http,
            ICorrelationContext correlation,
            IDevelopmentOperatorTargetAuthority targets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var granted = await targets.GrantAsync(new(
                    request.TenantId,
                    request.AgentId,
                    request.Classification,
                    request.AllowedOperations,
                    request.ExpiresAtUtc,
                    request.EvidenceReference,
                    OperatorIdentity(http.User),
                    correlation.GetOrCreate(),
                    request.FileFixtureRoot), cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/v2/dev-operator-targets/{granted.TenantId}/{granted.AgentId:D}", granted);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { code = "target_not_found" });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "target_grant_invalid", detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { code = "target_grant_rejected", detail = exception.Message });
            }
        })
        .Produces<DevelopmentOperatorTargetGrantView>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{tenantId:int}/{agentId:guid}/revoke", async (
            int tenantId,
            Guid agentId,
            RevokeDevelopmentOperatorTargetRequest request,
            HttpContext http,
            ICorrelationContext correlation,
            IDevelopmentOperatorTargetAuthority targets,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var revoked = await targets.RevokeAsync(
                    tenantId,
                    agentId,
                    request.Reason,
                    OperatorIdentity(http.User),
                    correlation.GetOrCreate(),
                    cancellationToken).ConfigureAwait(false);
                return revoked is null ? Results.NotFound() : Results.Ok(revoked);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { code = "target_revoke_invalid", detail = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { code = "target_revoke_rejected", detail = exception.Message });
            }
        })
        .Produces<DevelopmentOperatorTargetGrantView>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    internal static string OperatorIdentity(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ??
        user.FindFirstValue("client_id") ??
        user.FindFirstValue("azp") ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("preferred_username") ??
        string.Empty;
}

/// <summary>
/// Common admission boundary for every Development remote mutation. The gate
/// records only the accepted operation metadata; it never receives command,
/// terminal, file, or secret content.
/// </summary>
internal static class DevelopmentOperatorTargetGate
{
    public static async Task<IResult?> RequireAcceptedAsync(
        HttpContext http,
        int tenantId,
        Guid agentId,
        DevelopmentOperatorOperation operation,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var decision = await targets.EvaluateAsync(new(
            tenantId,
            agentId,
            operation,
            DevelopmentOperatorTargetEndpoints.OperatorIdentity(http.User),
            correlation.GetOrCreate()), cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return Rejected(decision.RejectionCode);
        }

        try
        {
            var audit = await targets.RecordAcceptedAsync(decision, cancellationToken).ConfigureAwait(false);
            http.Response.Headers["X-Development-Operation-Audit-Id"] = audit.AuditId.ToString("N");
            return null;
        }
        catch (InvalidOperationException)
        {
            return Rejected("target_authorization_changed");
        }
    }

    private static IResult Rejected(string? code) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Development target is not eligible for this operation.",
        extensions: new Dictionary<string, object?> { ["code"] = code ?? "target_not_authorized" });
}

public sealed record DevelopmentOperatorTargetGrantRequestBody(
    int TenantId,
    Guid AgentId,
    DevelopmentOperatorTargetClassification Classification,
    DevelopmentOperatorOperationScope AllowedOperations,
    DateTimeOffset ExpiresAtUtc,
    string EvidenceReference,
    string? FileFixtureRoot = null);

public sealed record RevokeDevelopmentOperatorTargetRequest(string Reason);
