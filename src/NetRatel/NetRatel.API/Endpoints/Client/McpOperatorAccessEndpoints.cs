using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Delegation-bound read surface that explains the caller's policy state
/// without dispatching a target operation. Tenant visibility is established
/// before a target can be resolved or described.
/// </summary>
public static class McpOperatorAccessEndpoints
{
    private const string Tool = "netratel_access";
    private const string ReadScope = "netratel.mcp.read";

    public static IEndpointRouteBuilder MapMcpOperatorAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/access")
            .WithTags("MCP Operator")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/whoami", async (
            HttpContext http,
            IHostEnvironment environment,
            IMcpOperatorAccessEvaluation access,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, environment, "whoami", null, null, out var context, out var failure))
                return Failure(failure!, null, null, http.TraceIdentifier, requiredOperation: $"{Tool}/whoami");

            var caller = await access.WhoAmIAsync(context!.Environment, context.Principal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { caller, correlationId = context.CorrelationId });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/agents/{tenantId:int}/{agentId:guid}", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IHostEnvironment environment,
            IMcpOperatorAccessEvaluation access,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, environment, "target", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, requiredOperation: $"{Tool}/target");

            var resolved = await access.ResolveTargetAsync(context!.Environment, context.Principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (!resolved.TenantVisible)
                return Failure("tenant_not_authorized", null, null, context.CorrelationId, requiredOperation: $"{Tool}/target");
            if (resolved.Target is null)
                return Failure("target_not_found", tenantId, agentId, context.CorrelationId, requiredOperation: $"{Tool}/target");

            return Results.Ok(new { target = resolved.Target, matchingPolicies = resolved.RelevantPolicies, correlationId = context.CorrelationId });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/agents/{tenantId:int}/{agentId:guid}/effective", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IHostEnvironment environment,
            IMcpOperatorAccessEvaluation access,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, environment, "effective", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, requiredOperation: $"{Tool}/effective");

            var resolved = await access.ResolveTargetAsync(context!.Environment, context.Principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (!resolved.TenantVisible)
                return Failure("tenant_not_authorized", null, null, context.CorrelationId, requiredOperation: $"{Tool}/effective");
            if (resolved.Target is null)
                return Failure("target_not_found", tenantId, agentId, context.CorrelationId, requiredOperation: $"{Tool}/effective");

            var effective = await access.EffectiveAsync(context.Environment, context.Principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (effective is null)
                return Failure("tenant_not_authorized", null, null, context.CorrelationId, requiredOperation: $"{Tool}/effective");
            return Results.Ok(new { effective, correlationId = context.CorrelationId });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/agents/{tenantId:int}/{agentId:guid}/evaluate", async (
            int tenantId,
            Guid agentId,
            string? tool,
            string? operation,
            HttpContext http,
            IHostEnvironment environment,
            IMcpOperatorAccessEvaluation access,
            CancellationToken cancellationToken) =>
        {
            if (!TryContext(http, environment, "evaluate", tenantId, agentId, out var context, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier, requiredOperation: $"{Tool}/evaluate");
            if (McpOperatorOperationCatalog.Find(tool ?? string.Empty, operation) is null)
                return Failure("operation_not_catalogued", tenantId, agentId, context!.CorrelationId, StatusCodes.Status400BadRequest, $"{Tool}/evaluate");

            var resolved = await access.ResolveTargetAsync(context!.Environment, context.Principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (!resolved.TenantVisible)
                return Failure("tenant_not_authorized", null, null, context.CorrelationId, requiredOperation: $"{Tool}/evaluate");
            if (resolved.Target is null)
                return Failure("target_not_found", tenantId, agentId, context.CorrelationId, requiredOperation: $"{Tool}/evaluate");

            var evaluation = await access.EvaluateAsync(
                context.Environment,
                context.Principal,
                tenantId,
                agentId,
                tool!,
                operation!,
                context.CorrelationId,
                context.RequestId,
                cancellationToken).ConfigureAwait(false);
            if (evaluation is null)
                return Failure("tenant_not_authorized", null, null, context.CorrelationId, requiredOperation: $"{Tool}/evaluate");
            return Results.Ok(new { evaluation, correlationId = context.CorrelationId });
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
        out McpOperatorAccessContext? context,
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
        var targetMatches = tenantId.HasValue
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

        if (!delegation.Identity.Scopes.Contains(ReadScope, StringComparer.Ordinal))
        {
            failure = "oauth_scope_missing";
            return false;
        }

        context = new McpOperatorAccessContext(
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

    private static IResult Failure(
        string code,
        int? tenantId,
        Guid? agentId,
        string correlationId,
        int? statusOverride = null,
        string? requiredOperation = null)
    {
        var status = statusOverride ?? code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" => "target",
            "operation_not_catalogued" => "catalog",
            _ => "policy"
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator access inspection was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator access inspection was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer,
                    retryable = false,
                    requiredScopes = new[] { ReadScope },
                    requiredOperation = requiredOperation ?? Tool,
                    target = tenantId.HasValue ? new { tenantId, agentId } : null,
                    safeDetails = SafeDetails(code),
                    remediation = Remediation(code)
                },
                ["correlationId"] = correlationId
            });
    }

    private static string Remediation(string code) => code switch
    {
        "delegated_identity_required" => "Invoke through the authenticated MCP service so it can issue a signed delegation assertion.",
        "delegated_identity_invalid" => "Obtain a fresh delegation assertion for this exact access operation and target.",
        "oauth_scope_missing" => "Request the netratel.mcp.read OAuth scope, then obtain a fresh delegation assertion.",
        "tenant_not_authorized" => "Ask a policy administrator to grant reviewed tenant visibility.",
        "target_not_found" => "Verify the persisted tenant and V2 agent identifier after tenant visibility is granted.",
        "operation_not_catalogued" => "Choose one tool and operation published by netratel_capabilities.",
        _ => "Review the caller's scopes, target profile, and operator policy."
    };

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact access operation.",
        "oauth_scope_missing" => "The signed delegation does not include the required read scope.",
        "tenant_not_authorized" => "Target identifiers are withheld until tenant visibility is admitted.",
        "target_not_found" => "No persisted target matched the requested identifier after tenant visibility was admitted.",
        "operation_not_catalogued" => "The requested tool and operation are not published by the NetRatel MCP catalog.",
        _ => "The access request did not satisfy the current operator policy."
    };
}

internal sealed record McpOperatorAccessContext(
    McpOperatorEnvironment Environment,
    McpOperatorPrincipal Principal,
    string CorrelationId,
    string RequestId);
