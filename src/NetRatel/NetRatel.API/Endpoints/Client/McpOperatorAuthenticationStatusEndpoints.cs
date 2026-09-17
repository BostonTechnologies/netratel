using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Returns the delegation-bound authentication facts for the current MCP
/// caller without routing the HTTP MCP service token through the legacy
/// AI-agent endpoint.
/// </summary>
public static class McpOperatorAuthenticationStatusEndpoints
{
    private const string Tool = "netratel_auth";
    private const string Operation = "status";
    private const string ReadScope = "netratel.mcp.read";

    public static IEndpointRouteBuilder MapMcpOperatorAuthenticationStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/auth")
            .WithTags("MCP Operator")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/status", (HttpContext http, IHostEnvironment environment) =>
        {
            if (!TryGetDelegation(http, environment, out var delegation, out var failure))
                return Failure(failure!, http.TraceIdentifier);

            return Results.Ok(new
            {
                authenticated = true,
                name = delegation!.Identity.Subject,
                authMode = "mcp_operator_delegation",
                identityProvider = "oidc",
                groups = delegation.Identity.Groups.Order(StringComparer.Ordinal).ToArray(),
                roles = delegation.Identity.Roles.Order(StringComparer.Ordinal).ToArray(),
                scopes = delegation.Identity.Scopes.Order(StringComparer.Ordinal).ToArray(),
                correlationId = delegation.CorrelationId
            });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static bool TryGetDelegation(
        HttpContext http,
        IHostEnvironment environment,
        out McpOperatorDelegation? delegation,
        out string? failure)
    {
        delegation = null;
        failure = null;
        if (!http.TryGetMcpOperatorDelegation(out delegation) || delegation is null)
        {
            failure = "delegated_identity_required";
            return false;
        }

        var expectedInstance = environment.IsDevelopment()
            ? "dev"
            : environment.IsProduction()
                ? "prod"
                : null;
        if (expectedInstance is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, Operation, StringComparison.Ordinal) ||
            delegation.TenantId is not null ||
            delegation.AgentId is not null ||
            string.IsNullOrWhiteSpace(delegation.Resource) ||
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

        return true;
    }

    private static IResult Failure(string code, string correlationId)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            _ => "authorization"
        };
        var safeDetails = code switch
        {
            "delegated_identity_required" => "A valid signed operator delegation was not available for this authentication status request.",
            "delegated_identity_invalid" => "The signed operator delegation was not valid for netratel_auth/status.",
            "oauth_scope_missing" => "The signed delegation does not include the required read scope.",
            _ => "The authentication status request was not admitted."
        };
        var remediation = code switch
        {
            "delegated_identity_required" => "Invoke through the authenticated MCP service so it can issue a signed delegation assertion.",
            "delegated_identity_invalid" => "Obtain a fresh delegation assertion for netratel_auth/status.",
            "oauth_scope_missing" => "Request the netratel.mcp.read OAuth scope, then obtain a fresh delegation assertion.",
            _ => "Review the caller's signed delegation."
        };

        return Results.Problem(
            statusCode: status,
            title: "MCP authentication status was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP authentication status was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer,
                    retryable = false,
                    requiredScopes = new[] { ReadScope },
                    requiredOperation = $"{Tool}/{Operation}",
                    safeDetails,
                    remediation
                },
                ["correlationId"] = correlationId
            });
    }
}
