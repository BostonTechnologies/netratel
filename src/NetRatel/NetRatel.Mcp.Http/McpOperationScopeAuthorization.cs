using ModelContextProtocol;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;
using System.Security.Claims;
using System.Text.Json;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Applies the catalog-declared OAuth scope to every HTTP operation. The Dev
/// compatibility bridge retains the deployed read/write/onboarding contract;
/// Production always enforces the exact configured scope class.
/// </summary>
public sealed class McpOperationScopeAuthorization(
    NetRatelMcpHostContext hostContext,
    NetRatelMcpHttpOptions options)
{
    public McpOperationScopeDecision Evaluate(
        ClaimsPrincipal? user,
        string tool,
        IDictionary<string, JsonElement>? arguments)
    {
        var operation = Operation(arguments);
        var descriptor = NetRatelMcpCatalog.FindOperation(tool, operation);
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (descriptor is null)
            return McpOperationScopeDecision.Denied("operation_not_catalogued");
        if (!descriptor.AvailableOverHttp)
            return McpOperationScopeDecision.Denied("operation_not_available_over_http");
        if (access is null)
            return McpOperationScopeDecision.Denied("operation_scope_unclassified");

        if (!string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal) &&
            descriptor.Safety == NetRatelMcpOperationSafety.DevelopmentMutation)
            return McpOperationScopeDecision.Denied("non_read_operations_are_development_only");
        if (!NetRatelMcpCatalog.IsAvailableIn(descriptor, hostContext))
            return McpOperationScopeDecision.Denied("operation_not_available_in_environment");
        if (!NetRatelMcpCatalog.IsAvailableOverHttpIn(descriptor, hostContext))
            return McpOperationScopeDecision.Denied("operation_not_available_over_http");

        if (string.Equals(hostContext.Target.Instance, "dev", StringComparison.Ordinal) && !hostContext.OperatorSurfaceEnabled)
        {
            if (access.RequiredScope == McpOperationAccessScope.Admin)
            {
                return Scopes(user).Contains(options.AdminScope)
                    ? McpOperationScopeDecision.Permit
                    : McpOperationScopeDecision.Denied("missing_operation_scope");
            }

            return LegacyDevelopmentDecision(user, access);
        }

        var requiredScope = options.RequiredScopeFor(access.RequiredScope);
        return Scopes(user).Contains(requiredScope)
            ? McpOperationScopeDecision.Permit
            : McpOperationScopeDecision.Denied("missing_operation_scope");
    }

    public void EnsureAuthorized(
        ClaimsPrincipal? user,
        string tool,
        IDictionary<string, JsonElement>? arguments)
    {
        var decision = Evaluate(user, tool, arguments);
        if (decision.Allowed)
            return;

        throw new McpException(decision.Code switch
        {
            "missing_development_write_scope" => $"This Development MCP operation requires the '{options.DevelopmentWriteScope}' OAuth scope.",
            "missing_development_onboarding_scope" => $"This Development MCP onboarding operation requires the '{options.DevelopmentOnboardingScope}' OAuth scope.",
            "missing_operation_scope" => $"This MCP operation requires the catalogued '{options.RequiredScopeFor(McpOperationAccessCatalog.Find(tool, Operation(arguments))!.RequiredScope)}' OAuth scope.",
            "operation_not_catalogued" => "This MCP operation is not in the approved operator catalog.",
            "operation_not_available_over_http" => "This MCP operation is not available on the remote HTTP transport.",
            "operation_not_available_in_environment" => "This MCP operation is not available on the selected NetRatel environment.",
            "operation_scope_unclassified" => "This MCP operation has no approved least-privilege OAuth scope classification.",
            _ => "Non-read MCP operations are available only on the Development instance."
        });
    }

    private static string? Operation(IDictionary<string, JsonElement>? arguments)
        => arguments is not null && arguments.TryGetValue("operation", out var operation) && operation.ValueKind == JsonValueKind.String
            ? operation.GetString()
            : null;

    private static HashSet<string> Scopes(ClaimsPrincipal? user)
        => user?.FindAll("scope").Concat(user.FindAll("scp"))
               .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
               .ToHashSet(StringComparer.Ordinal)
           ?? [];

    private McpOperationScopeDecision LegacyDevelopmentDecision(
        ClaimsPrincipal? user,
        McpOperationAccessDescriptor access)
    {
        var requirement = options.DevelopmentCompatibilityRequirementFor(access.DevelopmentCompatibilityScope);
        return requirement.Scope is null || Scopes(user).Contains(requirement.Scope)
            ? McpOperationScopeDecision.Permit
            : McpOperationScopeDecision.Denied(requirement.MissingScopeCode!);
    }

}

public sealed record McpOperationScopeDecision(bool Allowed, string? Code)
{
    public static McpOperationScopeDecision Permit { get; } = new(true, null);

    public static McpOperationScopeDecision Denied(string code) => new(false, code);
}
