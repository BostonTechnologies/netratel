using System.Diagnostics;
using System.Text.Json.Nodes;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Core;

/// <summary>Stable transport-neutral envelope for every NetRatel MCP operation.</summary>
public record NetRatelToolError(
    string Code,
    bool Retryable,
    int? UpstreamStatus = null,
    IReadOnlyList<string>? AllowedOperations = null);

/// <summary>
/// Stable, secret-safe explanation of an unsuccessful MCP operation. The
/// legacy <see cref="NetRatelToolError"/> remains available for compatibility;
/// this shape is the actionable failure contract for new consumers.
/// </summary>
public record NetRatelToolFailure(
    string Code,
    string Layer,
    bool Retryable,
    IReadOnlyList<string> RequiredScopes,
    string RequiredOperation,
    JsonNode? Target,
    string SafeDetails,
    string Remediation)
{
    internal static NetRatelToolFailure From(string status, NetRatelToolError? error)
    {
        var code = error?.Code ?? status;
        return new NetRatelToolFailure(
            code,
            LayerFor(code),
            error?.Retryable ?? false,
            [],
            "not_available",
            null,
            SafeDetailsFor(code),
            RemediationFor(code));
    }

    private static string LayerFor(string code) => code switch
    {
        "oauth_scope_missing" or "oauth_role_missing" => "oauth_scope",
        "tenant_not_authorized" => "tenant",
        "target_not_found" or "target_disabled" or "target_offline" => "target",
        "target_policy_missing" or "target_policy_denied" or "target_operation_not_authorized" or "target_classification_denied" or "target_policy_expired" => "policy",
        "path_not_authorized" or "shell_not_authorized" or "fanout_limit_exceeded" => "constraint",
        "confirmation_required" or "confirmation_plan_expired" or "confirmation_plan_stale" => "confirmation",
        "idempotency_conflict" => "idempotency",
        "delegated_identity_invalid" or "delegated_identity_required" => "delegation",
        "validation_error" or "unsupported_operation" or "search_query_too_broad" => "input",
        "search_visibility_limit_exceeded" => "policy",
        _ => "upstream"
    };

    private static string SafeDetailsFor(string code) => code switch
    {
        "search_query_too_broad" => "The client search exceeds the bounded candidate limit.",
        "search_visibility_limit_exceeded" => "Tenant visibility exceeds the bounded discovery limit.",
        "oauth_role_missing" => "The delegated operator lacks the required role.",
        "validation_error" => "The request did not meet the published bounded input contract.",
        "unsupported_operation" => "The selected operation is not published by this MCP tool.",
        "confirmation_required" => "The operation has not been dispatched; it requires explicit confirmation.",
        "oauth_scope_missing" => "The caller does not have the catalogued OAuth scope for this operation.",
        "tenant_not_authorized" => "The caller does not have visibility of the requested tenant.",
        "target_policy_missing" or "target_policy_denied" or "target_operation_not_authorized" or "target_classification_denied" or "target_policy_expired" => "The requested target operation did not satisfy the current operator policy.",
        _ => "The operation did not complete. No upstream response content is exposed."
    };

    private static string RemediationFor(string code) => code switch
    {
        "search_query_too_broad" => "Narrow request.q with a client name or exact agent ID.",
        "search_visibility_limit_exceeded" => "Use exact target reads or ask a policy administrator to narrow discovery visibility.",
        "oauth_role_missing" => "Ask an administrator to review the operator role assignment.",
        "validation_error" or "unsupported_operation" => "Use netratel_capabilities to select a documented operation and request shape.",
        "confirmation_required" => "Review the preview, then repeat the exact operation with the required confirmation input.",
        "oauth_scope_missing" => "Request the required OAuth scope, then obtain a fresh authorization result.",
        "tenant_not_authorized" => "Ask a policy administrator to grant reviewed tenant visibility.",
        "target_policy_missing" or "target_policy_denied" or "target_operation_not_authorized" or "target_classification_denied" or "target_policy_expired" => "Use netratel_access to inspect the target policy and ask a policy administrator for a reviewed change.",
        "delegated_identity_invalid" or "delegated_identity_required" => "Invoke through the authenticated MCP host to obtain a fresh signed delegation assertion.",
        "idempotency_conflict" => "Reuse the original request payload for this idempotency key or start a new preview.",
        _ => "Retry only when the failure is marked retryable; otherwise inspect the safe failure details and correlation ID."
    };
}

public record NetRatelConfirmation(
    string ConfirmField,
    bool RequiredValue,
    string Operation,
    IReadOnlyList<string> AffectedIds);

public record NetRatelToolResponse(
    bool Success,
    string Status,
    string Summary,
    JsonNode? Data = null,
    IReadOnlyList<string>? AffectedIds = null,
    NetRatelToolError? Error = null,
    bool RequiresConfirmation = false,
    NetRatelConfirmation? Confirmation = null)
{
    /// <summary>Actionable failure metadata for unsuccessful responses.</summary>
    public NetRatelToolFailure? Failure => Success ? null : FailureContext ?? NetRatelToolFailure.From(Status, Error);

    /// <summary>
    /// Trace correlation when the host has an active activity. Stdio callers
    /// that have no activity receive the explicit non-sensitive sentinel.
    /// </summary>
    public string CorrelationId { get; init; } = Activity.Current?.TraceId.ToString() ?? "not_available";

    internal NetRatelToolFailure? FailureContext { get; init; }

    /// <summary>
    /// Adds catalog-derived operation context without changing the legacy
    /// primary constructor used by existing MCP consumers.
    /// </summary>
    public NetRatelToolResponse WithFailureContext(
        string tool,
        string operation,
        JsonNode? target = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (Success)
        {
            return this;
        }

        var access = McpOperationAccessCatalog.Find(tool, operation);
        var failure = Failure ?? NetRatelToolFailure.From(Status, Error);
        return this with
        {
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? CorrelationId : correlationId,
            FailureContext = failure with
            {
                RequiredScopes = access is null ? failure.RequiredScopes : [McpOperationAccessScopeNames.Canonical(access.RequiredScope)],
                RequiredOperation = $"{tool}/{operation}",
                Target = target?.DeepClone()
            }
        };
    }
}
