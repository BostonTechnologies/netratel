using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Versioned administrative surface for the environment-specific MCP operator
/// policy and server-owned target-profile model. This is deliberately
/// registered in every environment; the policy selected in a request still
/// determines whether it applies to Development or Production operations.
/// </summary>
public static class McpOperatorPolicyAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapMcpOperatorPolicyAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp-operator")
            .WithTags("MCP Operator Administration")
            .RequireAuthorization("McpOperatorPolicyAdmin");

        group.MapGet("/policies", async (
            McpOperatorEnvironment? environment,
            int? tenantId,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
            Results.Ok(await administration.ListAsync(environment, tenantId, cancellationToken).ConfigureAwait(false)))
            .Produces<McpOperatorPolicyPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/policies/{policyId:guid}", async (
            Guid policyId,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            var policy = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        })
        .Produces<McpOperatorPolicy>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/audits", async (
            int? tenantId,
            Guid? policyId,
            Guid? agentId,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await administration.ListChangeAuditsAsync(tenantId, policyId, agentId, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Rejected("operator_audit_filter_invalid", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<IReadOnlyList<McpOperatorPolicyChangeAudit>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/accepted-audits", async (
            int? tenantId,
            Guid? agentId,
            string? subject,
            int? limit,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await administration.ListAcceptedAuditsAsync(
                    new McpOperatorAcceptedAuditFilter(tenantId, agentId, subject, limit),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Rejected("operator_accepted_audit_filter_invalid", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<McpOperatorAcceptedAuditPage>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/policies", async (
            CreateMcpOperatorPolicyRequest request,
            HttpContext http,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(http.User, out var actor))
                return IdentityMissing();
            if (WouldEscalateCaller(request.Policy, http.User, actor))
                return Rejected("operator_policy_self_escalation", StatusCodes.Status409Conflict);

            try
            {
                var created = await administration.CreateAsync(request.Policy, actor, cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/v2/mcp-operator/policies/{created.PolicyId:D}", created);
            }
            catch (KeyNotFoundException)
            {
                return Rejected("operator_target_not_found", StatusCodes.Status404NotFound);
            }
            catch (ArgumentException)
            {
                return Rejected("operator_policy_invalid", StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Rejected("operator_policy_rejected", StatusCodes.Status409Conflict);
            }
        })
        .Produces<McpOperatorPolicy>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/policies/{policyId:guid}", async (
            Guid policyId,
            ReplaceMcpOperatorPolicyRequest request,
            HttpContext http,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(http.User, out var actor))
                return IdentityMissing();
            if (WouldEscalateCaller(request.Policy, http.User, actor))
                return Rejected("operator_policy_self_escalation", StatusCodes.Status409Conflict);

            try
            {
                return Results.Ok(await administration.ReplaceAsync(
                    policyId,
                    request.ExpectedVersion,
                    request.Policy,
                    actor,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (KeyNotFoundException)
            {
                return Rejected("operator_policy_not_found", StatusCodes.Status404NotFound);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Rejected("operator_policy_version_conflict", StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Rejected("operator_policy_invalid", StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Rejected("operator_policy_rejected", StatusCodes.Status409Conflict);
            }
        })
        .Produces<McpOperatorPolicy>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/policies/{policyId:guid}/disable", async (
            Guid policyId,
            DisableMcpOperatorPolicyRequest request,
            HttpContext http,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(http.User, out var actor))
                return IdentityMissing();

            try
            {
                var disabled = await administration.DisableAsync(policyId, request.ExpectedVersion, actor, cancellationToken)
                    .ConfigureAwait(false);
                return disabled is null ? Results.NotFound() : Results.Ok(disabled);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Rejected("operator_policy_version_conflict", StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Rejected("operator_policy_invalid", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<McpOperatorPolicy>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/targets/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            var profile = await administration.GetTargetProfileAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .Produces<McpOperatorTargetProfile>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/targets/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            UpsertMcpOperatorTargetProfileRequest request,
            HttpContext http,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(http.User, out var actor))
                return IdentityMissing();

            try
            {
                return Results.Ok(await administration.UpsertTargetProfileAsync(
                    tenantId,
                    agentId,
                    request.Classification,
                    request.Tags ?? [],
                    request.ExpectedVersion,
                    actor,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (KeyNotFoundException)
            {
                return Rejected("operator_target_not_found", StatusCodes.Status404NotFound);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Rejected("operator_target_profile_version_conflict", StatusCodes.Status409Conflict);
            }
            catch (ArgumentException)
            {
                return Rejected("operator_target_profile_invalid", StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return Rejected("operator_target_profile_rejected", StatusCodes.Status409Conflict);
            }
        })
        .Produces<McpOperatorTargetProfile>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    internal static bool WouldEscalateCaller(McpOperatorPolicyDraft draft, ClaimsPrincipal user, string actor)
    {
        if (draft.Effect != McpOperatorPolicyEffect.Allow)
            return false;

        var selector = draft.PrincipalSelector;
        return selector.Kind switch
        {
            McpOperatorPrincipalSelectorKind.OAuthSubject => Same(selector.Value, actor),
            McpOperatorPrincipalSelectorKind.OAuthClientId => HasClaim(user, selector.Value, "client_id", "azp"),
            McpOperatorPrincipalSelectorKind.OidcGroup => HasClaim(user, selector.Value, "groups", "group"),
            McpOperatorPrincipalSelectorKind.MappedRole => HasClaim(user, selector.Value, "roles", "role", ClaimTypes.Role),
            McpOperatorPrincipalSelectorKind.ServicePrincipal => HasClaim(user, selector.Value, "service_principal", "client_id", "azp"),
            _ => true
        };
    }

    private static bool TryGetActor(ClaimsPrincipal user, out string actor)
    {
        actor = user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("preferred_username")
            ?? user.Identity?.Name
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(actor);
    }

    private static bool HasClaim(ClaimsPrincipal user, string expected, params string[] types) =>
        user.Claims.Any(claim =>
            types.Contains(claim.Type, StringComparer.Ordinal) && Same(claim.Value, expected));

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static IResult IdentityMissing() => Rejected("operator_identity_missing", StatusCodes.Status401Unauthorized);

    private static IResult Rejected(string code, int statusCode) => Results.Problem(
        statusCode: statusCode,
        title: "MCP operator administration request was not accepted.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record CreateMcpOperatorPolicyRequest(McpOperatorPolicyDraft Policy);

public sealed record ReplaceMcpOperatorPolicyRequest(long ExpectedVersion, McpOperatorPolicyDraft Policy);

public sealed record DisableMcpOperatorPolicyRequest(long ExpectedVersion);

public sealed record UpsertMcpOperatorTargetProfileRequest(
    McpOperatorTargetClassification Classification,
    IReadOnlyCollection<string>? Tags,
    long? ExpectedVersion);
