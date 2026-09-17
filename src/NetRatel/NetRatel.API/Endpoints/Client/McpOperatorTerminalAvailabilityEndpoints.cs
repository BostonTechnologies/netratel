using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// First production-shaped V2 MCP adapter. It has no browser fallback: the
/// route requires the MCP service's M2M identity plus a valid signed caller
/// delegation or explicitly allowlisted local-agent identity, then records a
/// general policy admission before returning current terminal gateway facts.
/// </summary>
public static class McpOperatorTerminalAvailabilityEndpoints
{
    private const string Tool = "netratel_terminal";
    private const string Operation = "availability";
    private const string RequiredScope = "netratel.mcp.observe";

    public static IEndpointRouteBuilder MapMcpOperatorTerminalAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator")
            .WithTags("MCP Operator")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/agents/{tenantId:int}/{agentId:guid}/terminal/availability", async (
            int tenantId,
            Guid agentId,
            HttpContext http,
            IHostEnvironment environment,
            IMcpOperatorRouteAdmission admission,
            CancellationToken cancellationToken) =>
        {
            if (!TryCreateRequest(http, environment, tenantId, agentId, out var request, out var failure))
                return Failure(failure!, tenantId, agentId, http.TraceIdentifier);

            var terminals = http.RequestServices.GetService<IAgentTerminalSessionRegistry>();
            var availability = terminals?.GetAvailability(new ClientKey(tenantId, agentId));
            var readyRequest = request! with
            {
                TargetOnline = availability is not null,
                CapabilityAvailable = availability is not null
            };
            var evaluated = await admission.EvaluateAsync(readyRequest, cancellationToken).ConfigureAwait(false);
            if (!evaluated.Decision.IsAllowed)
                return Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", tenantId, agentId, readyRequest.CorrelationId);

            McpOperatorAcceptedAudit audit;
            try
            {
                // Re-evaluation in RecordAcceptedAsync closes the policy-change
                // window between the decision and the mandatory pre-response audit.
                audit = await admission.RecordAcceptedAsync(readyRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (McpOperatorAdmissionRejectedException rejection)
            {
                return Failure(rejection.FailureCode, tenantId, agentId, readyRequest.CorrelationId);
            }

            return Results.Ok(new McpOperatorTerminalAvailabilityResponse(
                tenantId,
                agentId,
                availability!.AvailableShells,
                availability.SupportsIdempotentClose,
                audit.AuditId,
                readyRequest.CorrelationId));
        })
        .Produces<McpOperatorTerminalAvailabilityResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static bool TryCreateRequest(
        HttpContext http,
        IHostEnvironment environment,
        int tenantId,
        Guid agentId,
        out McpOperatorRouteAccessRequest? request,
        out string? failure)
    {
        request = null;
        failure = null;
        var localAgents = http.RequestServices.GetService<McpOperatorLocalAgentOptions>() ?? new McpOperatorLocalAgentOptions();
        var hasSignedDelegation = http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null;
        if (!hasSignedDelegation && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool, Operation,
                tenantId, agentId, out delegation))
        {
            failure = environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required";
            return false;
        }
        var effectiveDelegation = delegation!;

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
        if (operatorEnvironment is null || expectedInstance is null ||
            !string.Equals(effectiveDelegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Operation, Operation, StringComparison.Ordinal) ||
            effectiveDelegation.TenantId != tenantId || effectiveDelegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(effectiveDelegation.Resource) || string.IsNullOrWhiteSpace(effectiveDelegation.CorrelationId))
        {
            failure = "delegated_identity_invalid";
            return false;
        }

        request = new McpOperatorRouteAccessRequest(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                effectiveDelegation.Identity.Subject,
                effectiveDelegation.Identity.ClientId,
                effectiveDelegation.Identity.AuthorizedParty,
                effectiveDelegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                effectiveDelegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                effectiveDelegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effectiveDelegation.ServicePrincipal,
            effectiveDelegation.Resource!,
            effectiveDelegation.Instance!,
            effectiveDelegation.Tool,
            effectiveDelegation.Operation,
            tenantId,
            agentId,
            new HashSet<string>([RequiredScope], StringComparer.Ordinal),
            effectiveDelegation.CorrelationId!,
            effectiveDelegation.RequestId,
            TargetOnline: false,
            CapabilityAvailable: false);
        return true;
    }

    private static IResult Failure(string code, int tenantId, Guid agentId, string correlationId)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_offline" or "capability_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "local_operator_identity_not_allowed" => "authentication",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" => "capability",
            _ => "policy"
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator terminal availability was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator terminal availability was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer,
                    retryable = code is "target_offline" or "capability_unavailable",
                    requiredScopes = new[] { RequiredScope },
                    requiredOperation = $"{Tool}/{Operation}",
                    target = code == "tenant_not_authorized" ? null : new { tenantId, agentId },
                    safeDetails = SafeDetails(code),
                    remediation = Remediation(code)
                },
                ["correlationId"] = correlationId
            });
    }

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact terminal availability operation.",
        "local_operator_identity_not_allowed" => "The direct local operator client identity is not enabled for this API deployment.",
        "oauth_scope_missing" => "The signed delegation does not include the required terminal observation scope.",
        "tenant_not_authorized" => "Target identifiers are withheld until tenant visibility is admitted.",
        "target_not_found" => "No persisted target matched the requested identifier after tenant visibility was admitted.",
        "target_disabled" => "The persisted target is disabled and cannot provide terminal availability.",
        "target_offline" => "The persisted target does not currently have an online terminal gateway session.",
        "capability_unavailable" => "The target does not currently advertise the required terminal capability.",
        _ => "The terminal availability request did not satisfy the current operator policy."
    };

    private static string Remediation(string code) => code switch
    {
        "delegated_identity_required" => "Invoke the route only through the authenticated MCP service so it can issue a signed delegation assertion.",
        "local_operator_identity_not_allowed" => "Ask an API owner to enable NetRatel:Mcp:LocalAgent only for this exact local client ID, then retry.",
        "delegated_identity_invalid" => "Obtain a fresh MCP delegation for this exact operation and target.",
        "oauth_scope_missing" => $"Request the '{RequiredScope}' OAuth scope and reauthorize the MCP consumer.",
        "tenant_not_authorized" => "Ask a policy administrator to grant a reviewed tenant policy for this operator.",
        "target_policy_missing" or "target_operation_not_authorized" => "Ask a policy administrator to review the target profile and terminal-observation policy.",
        "target_offline" or "capability_unavailable" => "Reconnect the target's terminal gateway capability, then retry the read operation.",
        _ => "Review the operator policy, target profile, and current gateway readiness."
    };
}

public sealed record McpOperatorTerminalAvailabilityResponse(
    int TenantId,
    Guid AgentId,
    IReadOnlyList<string> AvailableShells,
    bool SupportsIdempotentClose,
    Guid AuditId,
    string CorrelationId);
