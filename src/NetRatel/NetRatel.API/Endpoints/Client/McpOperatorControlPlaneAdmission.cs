using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Common admission for operator operations that deliberately have no tenant
/// or agent target.  A fixed digest makes these ControlPlane policies distinct
/// from any agent-target policy.
/// </summary>
internal static class McpOperatorControlPlaneAdmission
{
    public static async Task<McpOperatorControlPlaneAdmissionResult> TryAuthorizeAsync(
        string tool,
        string operation,
        string controlPlaneDigest,
        HttpContext http,
        IHostEnvironment environment,
        IMcpOperatorAuthorization authorization,
        CancellationToken cancellationToken,
        string? delegatedOperation = null)
    {
        var fallback = MinimalContext(tool, operation, http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var effective) || effective is null)
        {
            return new(null, "delegated_identity_required", fallback);
        }

        var access = McpOperationAccessCatalog.Find(tool, operation);
        var descriptor = McpOperatorOperationCatalog.Find(tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || descriptor is null ||
            !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, tool, StringComparison.Ordinal) ||
            !string.Equals(effective.Operation, delegatedOperation ?? operation, StringComparison.Ordinal) ||
            effective.TenantId is not null || effective.AgentId is not null ||
            string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
        {
            return new(null, "delegated_identity_invalid", fallback);
        }

        var principal = new McpOperatorPrincipal(
            effective.Identity.Subject,
            effective.Identity.ClientId,
            effective.Identity.AuthorizedParty,
            effective.Identity.Groups.ToHashSet(StringComparer.Ordinal),
            effective.Identity.Roles.ToHashSet(StringComparer.Ordinal),
            effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal),
            effective.ServicePrincipal);
        var request = new McpOperatorAccessRequest(
            operatorEnvironment,
            principal,
            0,
            null,
            null,
            descriptor.OperationFamily,
            $"{tool}/{operation}",
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            descriptor.ConfirmationClass,
            effective.CorrelationId!,
            effective.RequestId,
            controlPlaneDigest,
            null,
            effective.Resource,
            effective.Instance,
            tool,
            true,
            true,
            true,
            true,
            true);
        var decision = await authorization.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorControlPlaneContext(
            decision,
            effective.ServicePrincipal,
            effective.Identity.Subject,
            McpOperationAccessScopeNames.Canonical(access.RequiredScope),
            tool,
            operation,
            effective.CorrelationId!);
        return decision.IsAllowed
            ? new(context, null, fallback)
            : new(context, decision.FailureCode ?? "target_policy_missing", fallback);
    }

    public static McpOperatorControlPlaneContext MinimalContext(string tool, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(tool, operation)
            ?? throw new InvalidOperationException($"MCP operation '{tool}/{operation}' is not catalogued.");
        return new McpOperatorControlPlaneContext(
            null!,
            string.Empty,
            string.Empty,
            McpOperationAccessScopeNames.Canonical(access.RequiredScope),
            tool,
            operation,
            correlationId);
    }
}

internal sealed record McpOperatorControlPlaneContext(
    McpOperatorDecision Decision,
    string ServicePrincipal,
    string Subject,
    string RequiredScope,
    string Tool,
    string Operation,
    string CorrelationId);

internal sealed record McpOperatorControlPlaneAdmissionResult(
    McpOperatorControlPlaneContext? Context,
    string? FailureCode,
    McpOperatorControlPlaneContext Fallback);
