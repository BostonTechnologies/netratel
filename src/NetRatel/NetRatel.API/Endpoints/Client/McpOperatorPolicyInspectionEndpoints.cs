using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Delegation-bound, read-only inspection of operator policy metadata. Policy
/// administration mutations remain on their separately confirmed API contract.
/// </summary>
public static class McpOperatorPolicyInspectionEndpoints
{
    private const string Tool = "netratel_policy";
    private const string AdminScope = "netratel.mcp.admin";
    private const string PolicyAdministratorRole = "PolicyAdministrator";

    public static IEndpointRouteBuilder MapMcpOperatorPolicyInspectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/policy")
            .WithTags("MCP Operator Policy Inspection")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/policies", async (
            string? environment,
            int? tenantId,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "policies", tenantId, null, out var context, out var failure))
                return Failure(failure!, tenantId, null, http.TraceIdentifier, $"{Tool}/policies");
            if (!TryEnvironment(environment, out var selectedEnvironment))
                return Failure("validation_error", tenantId, null, context!.CorrelationId, $"{Tool}/policies", StatusCodes.Status400BadRequest);

            try
            {
                return Results.Ok(await administration.ListAsync(selectedEnvironment, tenantId, cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Failure("validation_error", tenantId, null, context!.CorrelationId, $"{Tool}/policies", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<McpOperatorPolicyPage>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/policies/{policyId:guid}", async (
            Guid policyId,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "policy", null, null, out var context, out var failure))
                return Failure(failure!, null, null, http.TraceIdentifier, $"{Tool}/policy");

            var policy = await administration.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
            return policy is null
                ? Failure("target_not_found", null, null, context!.CorrelationId, $"{Tool}/policy", StatusCodes.Status404NotFound)
                : Results.Ok(policy);
        })
        .Produces<McpOperatorPolicy>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/change-audits", async (
            int? tenantId,
            Guid? policyId,
            Guid? agentId,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "change_audits", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, $"{Tool}/change_audits");

            try
            {
                return Results.Ok(await administration.ListChangeAuditsAsync(tenantId, policyId, agentId, cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Failure("validation_error", tenantId, agentId, context!.CorrelationId, $"{Tool}/change_audits", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<IReadOnlyList<McpOperatorPolicyChangeAudit>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/accepted-audits", async (
            int? tenantId,
            Guid? agentId,
            string? subject,
            int? limit,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "accepted_audits", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, $"{Tool}/accepted_audits");

            try
            {
                return Results.Ok(await administration.ListAcceptedAuditsAsync(
                    new McpOperatorAcceptedAuditFilter(tenantId, agentId, subject, limit),
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Failure("validation_error", tenantId, agentId, context!.CorrelationId, $"{Tool}/accepted_audits", StatusCodes.Status400BadRequest);
            }
        })
        .Produces<McpOperatorAcceptedAuditPage>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/targets/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "target", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, $"{Tool}/target");

            var target = await administration.GetTargetProfileAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            return target is null
                ? Failure("target_not_found", tenantId, agentId, context!.CorrelationId, $"{Tool}/target", StatusCodes.Status404NotFound)
                : Results.Ok(target);
        })
        .Produces<McpOperatorTargetProfile>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/matches/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorPolicyAdministration administration,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "matches", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, $"{Tool}/matches");

            var target = await administration.GetTargetProfileAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (target is null)
                return Failure("target_not_found", tenantId, agentId, context!.CorrelationId, $"{Tool}/matches", StatusCodes.Status404NotFound);

            var policies = await administration.ListAsync(context!.Environment, tenantId, cancellationToken).ConfigureAwait(false);
            var matches = policies.Items.Where(policy => MatchesTarget(policy, target)).ToArray();
            return Results.Ok(new McpOperatorPolicyTargetMatches(target, matches, context.CorrelationId));
        })
        .Produces<McpOperatorPolicyTargetMatches>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/evaluate/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            string? tool,
            string? operation,
            HttpContext http,
            IHostEnvironment hostEnvironment,
            IMcpOperatorAccessEvaluation access,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, hostEnvironment, "evaluate", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, $"{Tool}/evaluate");
            if (McpOperatorOperationCatalog.Find(tool ?? string.Empty, operation) is null)
                return Failure("operation_not_catalogued", tenantId, agentId, context!.CorrelationId, $"{Tool}/evaluate", StatusCodes.Status400BadRequest);

            var evaluation = await access.EvaluateForPolicyAdministratorAsync(
                context!.Environment,
                context.Principal,
                tenantId,
                agentId,
                tool!,
                operation!,
                context.CorrelationId,
                context.RequestId,
                cancellationToken).ConfigureAwait(false);
            return evaluation is null
                ? Failure("target_not_found", tenantId, agentId, context.CorrelationId, $"{Tool}/evaluate", StatusCodes.Status404NotFound)
                : Results.Ok(new { evaluation, correlationId = context.CorrelationId });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static bool TryContext(
        HttpContext http,
        IHostEnvironment environment,
        string operation,
        int? tenantId,
        Guid? agentId,
        out McpOperatorPolicyInspectionContext? context,
        out string? failure)
    {
        context = null;
        failure = null;
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
        {
            failure = "delegated_identity_required";
            return false;
        }

        var operatorEnvironment = environment.IsDevelopment()
            ? McpOperatorEnvironment.Development
            : environment.IsProduction()
                ? McpOperatorEnvironment.Production
                : (McpOperatorEnvironment?)null;
        var expectedInstance = operatorEnvironment switch
        {
            McpOperatorEnvironment.Development => "dev",
            McpOperatorEnvironment.Production => "prod",
            _ => null
        };
        var targetMatches = tenantId.HasValue || agentId.HasValue
            ? delegation.TenantId == tenantId && delegation.AgentId == agentId
            : delegation.TenantId is null && delegation.AgentId is null;
        if (operatorEnvironment is null || expectedInstance is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, operation, StringComparison.Ordinal) ||
            !targetMatches || string.IsNullOrWhiteSpace(delegation.Resource) ||
            string.IsNullOrWhiteSpace(delegation.CorrelationId))
        {
            failure = "delegated_identity_invalid";
            return false;
        }

        if (!delegation.Identity.Scopes.Contains(AdminScope, StringComparer.Ordinal))
        {
            failure = "oauth_scope_missing";
            return false;
        }

        if (!delegation.Identity.Roles.Contains(PolicyAdministratorRole, StringComparer.Ordinal))
        {
            failure = "oauth_role_missing";
            return false;
        }

        context = new McpOperatorPolicyInspectionContext(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                delegation.Identity.Subject,
                delegation.Identity.ClientId,
                delegation.Identity.AuthorizedParty,
                delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal),
                delegation.ServicePrincipal),
            delegation.CorrelationId!,
            delegation.RequestId);
        return true;
    }

    private static bool TryEnvironment(string? value, out McpOperatorEnvironment? environment)
    {
        environment = value switch
        {
            null => null,
            "Development" => McpOperatorEnvironment.Development,
            "Production" => McpOperatorEnvironment.Production,
            _ => null
        };
        return value is null or "Development" or "Production";
    }

    private static bool MatchesTarget(McpOperatorPolicy policy, McpOperatorTargetProfile target) =>
        (policy.TargetClassification is null || policy.TargetClassification == target.Classification) &&
        policy.TargetSelector.Kind switch
        {
            McpOperatorTargetSelectorKind.ExactAgent => policy.TargetSelector.AgentId == target.AgentId,
            McpOperatorTargetSelectorKind.Tenant => policy.TargetSelector.TenantId == target.TenantId,
            McpOperatorTargetSelectorKind.ClientTag => policy.TargetSelector.TenantId == target.TenantId &&
                policy.TargetSelector.ClientTag is { Length: > 0 } tag && target.Tags.Contains(tag, StringComparer.Ordinal),
            _ => false
        };

    private static IResult Failure(
        string code,
        int? tenantId,
        Guid? agentId,
        string correlationId,
        string requiredOperation,
        int? statusOverride = null)
    {
        var status = statusOverride ?? code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status403Forbidden
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator policy inspection was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator policy inspection was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer = Layer(code),
                    retryable = false,
                    requiredScopes = new[] { AdminScope },
                    requiredOperation,
                    target = tenantId.HasValue ? new { tenantId, agentId } : null,
                    safeDetails = SafeDetails(code),
                    remediation = Remediation(code)
                },
                ["correlationId"] = correlationId
            });
    }

    private static string Layer(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
        "oauth_scope_missing" => "oauth_scope",
        "oauth_role_missing" => "oauth_role",
        "target_not_found" => "target",
        "operation_not_catalogued" => "catalog",
        "validation_error" => "input",
        _ => "policy"
    };

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact policy inspection operation.",
        "oauth_scope_missing" => "The signed delegation does not include the required administrator scope.",
        "oauth_role_missing" => "The signed delegation does not include the PolicyAdministrator role.",
        "target_not_found" => "No persisted policy or target profile matched the requested identifier.",
        "operation_not_catalogued" => "The requested tool and operation are not published by the NetRatel MCP catalog.",
        "validation_error" => "The request did not meet the published bounded policy-inspection contract.",
        _ => "The policy inspection request did not satisfy the current operator authorization boundary."
    };

    private static string Remediation(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "Invoke through the authenticated MCP service so it can issue a fresh signed delegation assertion.",
        "oauth_scope_missing" => "Request the netratel.mcp.admin OAuth scope, then obtain a fresh delegation assertion.",
        "oauth_role_missing" => "Ask an identity administrator to assign the dedicated PolicyAdministrator role, then reauthorize.",
        "target_not_found" => "Verify the persisted policy or V2 target identifier.",
        "operation_not_catalogued" => "Choose one tool and operation published by netratel_capabilities.",
        "validation_error" => "Use netratel_capabilities to select a documented policy-inspection operation and request shape.",
        _ => "Review the delegated administrator scope and role assignment."
    };
}

internal sealed record McpOperatorPolicyInspectionContext(
    McpOperatorEnvironment Environment,
    McpOperatorPrincipal Principal,
    string CorrelationId,
    string RequestId);

public sealed record McpOperatorPolicyTargetMatches(
    McpOperatorTargetProfile Target,
    IReadOnlyList<McpOperatorPolicy> MatchingPolicies,
    string CorrelationId);
