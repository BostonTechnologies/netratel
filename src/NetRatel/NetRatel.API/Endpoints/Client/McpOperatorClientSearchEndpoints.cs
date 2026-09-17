using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Middleware;
using NetRatel.API.Security.M2M;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Read-only client and tenant discovery using the caller's signed identity and current target policies.</summary>
public static class McpOperatorClientSearchEndpoints
{
    private const string Tool = "netratel_search";
    private const string Operation = "clients";
    private const string ReadScope = "netratel.mcp.read";
    private const int MaximumCandidates = 100;
    private const int DefaultPageSize = 25;

    public static IEndpointRouteBuilder MapMcpOperatorClientSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/mcp/operator/search/clients", SearchAsync)
            .WithTags("MCP Operator").RequireAuthorization("M2MOnly");
        app.MapGet("/api/v2/mcp/operator/search/tenants", SearchTenantsAsync)
            .WithTags("MCP Operator").RequireAuthorization("M2MOnly");
        return app;
    }

    private static async Task<IResult> SearchAsync(
        HttpContext http, IHostEnvironment environment, IOptions<M2MOptions> m2m,
        [FromServices] OrchestratorDbContext db,
        IMcpOperatorAccessEvaluation access,
        IMcpOperatorSearchAuthorization authorization,
        [FromQuery] string? q, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var admission = Authenticate(Operation, http, environment, m2m, q);
        if (admission.Failure is { } failure) return failure;
        var (delegation, principal, operatorEnvironment) = admission.Context!;
        var correlation = delegation.CorrelationId!;
        var start = offset ?? 0;
        var size = limit ?? DefaultPageSize;
        if (start is < 0 or > 1_000_000 || size is < 1 or > MaximumCandidates)
            return Failure("search_pagination_invalid", correlation, delegation.Operation);
        var instance = delegation.Instance;

        var caller = await access.WhoAmIAsync(operatorEnvironment, principal, cancellationToken).ConfigureAwait(false);
        if (caller.TenantVisibilityTruncated) return Failure("search_visibility_limit_exceeded", correlation);
        var tenantIds = caller.VisibleTenantIds.Where(id => id > 0).ToArray();
        if (tenantIds.Length == 0) return Failure("tenant_not_authorized", correlation);

        var candidates = await CandidateQuery(db, q?.Trim(), tenantIds)
            .Skip(start).Take(MaximumCandidates + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMoreCandidates = candidates.Count > MaximumCandidates;
        var window = candidates.Take(MaximumCandidates).ToArray();
        var requests = window.Select(candidate => new McpOperatorAccessRequest(
            operatorEnvironment, principal, candidate.Agent.TenantId, candidate.Agent.AgentId,
            candidate.Classification, McpOperatorOperationFamily.Observability, $"{Tool}/{Operation}",
            new HashSet<string>([ReadScope], StringComparer.Ordinal), McpOperatorConfirmationClass.None,
            correlation, delegation.RequestId, TargetTags: ReadTags(candidate.TagsJson),
            McpResource: delegation.Resource, McpInstance: instance, Tool: Tool,
            TargetEnabled: candidate.Enabled, TargetOnline: true, CapabilityAvailable: true)).ToArray();
        var decisions = await authorization.EvaluateAsync(requests, cancellationToken).ConfigureAwait(false);
        var items = window.Zip(decisions).Select((pair, index) => new { pair.First, pair.Second, Index = index })
            .Where(pair => pair.Second.IsAllowed && (pair.Second.DevelopmentEnvironmentAccess ||
                (pair.First.Classification is not null && ReadTags(pair.First.TagsJson) is not null)))
            .Select(pair => new { Item = GlobalSearchEndpoints.MapAgent(pair.First.Agent), pair.Index }).ToArray();
        if (start == 0 && !hasMoreCandidates && candidates.Count > 0 && items.Length == 0)
            return Failure("target_operation_not_authorized", correlation);
        var selected = items.Take(size).ToArray();
        var page = selected.Select(item => item.Item).ToArray();
        var hasMore = items.Length > size || hasMoreCandidates;
        // Continue after the last returned candidate so authorized results beyond
        // this response are never silently skipped. An empty scan can still advance.
        int? nextOffset = hasMore ? start + (selected.Length > 0 ? selected[^1].Index + 1 : window.Length) : null;
        return Results.Ok(new { items = page, page = 1, pageSize = size,
            totalCount = page.Length, hasMore, nextOffset });
    }

    private static async Task<IResult> SearchTenantsAsync(
        HttpContext http, IHostEnvironment environment, IOptions<M2MOptions> m2m,
        [FromServices] OrchestratorDbContext db, IMcpOperatorAccessEvaluation access,
        [FromQuery] string? q, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        const string operation = "tenants";
        var admission = Authenticate(operation, http, environment, m2m, q);
        if (admission.Failure is { } failure) return failure;
        var (delegation, principal, operatorEnvironment) = admission.Context!;
        var correlation = delegation.CorrelationId!;
        var start = offset ?? 0;
        var size = limit ?? DefaultPageSize;
        if (start is < 0 or > 1_000_000 || size is < 1 or > MaximumCandidates)
            return Failure("search_pagination_invalid", correlation, delegation.Operation);
        var caller = await access.WhoAmIAsync(operatorEnvironment, principal, cancellationToken).ConfigureAwait(false);
        if (caller.TenantVisibilityTruncated) return Failure("search_visibility_limit_exceeded", correlation, operation);
        var tenantIds = caller.VisibleTenantIds.Where(id => id > 0).ToArray();
        if (tenantIds.Length == 0) return Failure("tenant_not_authorized", correlation, operation);
        var query = db.Tenants.AsNoTracking().Where(tenant => tenantIds.Contains(tenant.Id));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim().Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            var hasId = int.TryParse(q, out var id);
            query = query.Where(tenant => EF.Functions.ILike(tenant.Name, like) || (hasId && tenant.Id == id));
        }
        // Discovery exposes only the identifiers and names of already-visible tenants.
        // Administrative contact, domain, and update settings stay behind tenant administration.
        var items = await query.OrderBy(tenant => tenant.Name).ThenBy(tenant => tenant.Id)
            .Skip(start).Take(size + 1).Select(tenant => new McpOperatorTenantDiscovery(tenant.Id, tenant.Name))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = items.Take(size).ToArray();
        var hasMore = items.Length > size;
        return Results.Ok(new { items = page, page = 1, pageSize = size,
            totalCount = page.Length, hasMore, nextOffset = hasMore ? (int?)(start + page.Length) : null });
    }

    private static (DiscoveryContext? Context, IResult? Failure) Authenticate(
        string operation, HttpContext http, IHostEnvironment environment, IOptions<M2MOptions> m2m, string? q)
    {
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
            return (null, Failure("delegated_identity_required", http.TraceIdentifier, operation));
        var correlation = delegation.CorrelationId ?? http.TraceIdentifier;
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var instance) ||
            delegation.Instance != instance || delegation.Tool != Tool || delegation.Operation != operation ||
            delegation.TenantId is not null || delegation.AgentId is not null ||
            string.IsNullOrWhiteSpace(delegation.CorrelationId) ||
            !string.Equals(delegation.Resource, $"{m2m.Value.Authority.TrimEnd('/')}/mcp", StringComparison.Ordinal))
            return (null, Failure("delegated_identity_invalid", correlation, operation));
        if (!delegation.Identity.Scopes.Contains(ReadScope, StringComparer.Ordinal))
            return (null, Failure("oauth_scope_missing", correlation, operation));
        var principal = new McpOperatorPrincipal(
            delegation.Identity.Subject, delegation.Identity.ClientId, delegation.Identity.AuthorizedParty,
            delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
            delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
            delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal), delegation.ServicePrincipal);
        if (!McpOperationRoleRequirements.IsSatisfied(Tool, operation, principal.Roles))
            return (null, Failure("oauth_role_missing", correlation, operation));
        if (q is { Length: > 512 }) return (null, Failure("validation_error", correlation, operation));

        return (new DiscoveryContext(delegation, principal, operatorEnvironment), null);
    }

    private sealed record DiscoveryContext(McpOperatorDelegation Delegation, McpOperatorPrincipal Principal, McpOperatorEnvironment Environment);

    internal static IQueryable<ClientSearchCandidate> CandidateQuery(OrchestratorDbContext db, string? term, int[] tenantIds) =>
        from agent in AgentDirectorySearch.MatchingAgents(db, term)
        where tenantIds.Contains(agent.TenantId)
        join tenant in db.Tenants.AsNoTracking() on agent.TenantId equals tenant.Id
        join profile in db.McpOperatorTargetProfiles.AsNoTracking()
            on new { agent.TenantId, AgentId = agent.Id } equals new { profile.TenantId, profile.AgentId } into profiles
        from profile in profiles.DefaultIfEmpty()
        orderby tenant.Name, agent.Name, agent.Id
        select new ClientSearchCandidate(
            new AgentDirectoryRow(agent.TenantId, agent.Id, agent.Name, agent.IsEnabled, agent.DeviceInfoJson, tenant.Name),
            agent.IsEnabled && agent.Status != AgentStatus.Disabled,
            profile == null ? null : profile.Classification, profile == null ? null : profile.TagsJson);

    private static IReadOnlySet<string>? ReadTags(string? json)
    {
        if (json is null) return null;
        try
        {
            var tags = JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.StringArray);
            return tags is null || tags.Any(string.IsNullOrWhiteSpace) ? null : tags.ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException) { return null; }
    }

    private static IResult Failure(string code, string correlationId, string operation = Operation)
    {
        var (status, layer, details, remediation) = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => (401, "delegation",
                "A valid signed delegation for this discovery operation was not available.", "Invoke through the authenticated MCP host for this resource and operation."),
            "oauth_scope_missing" => (403, "oauth_scope", "The signed delegation lacks the read scope.", "Request netratel.mcp.read and obtain a fresh delegation."),
            "oauth_role_missing" => (403, "oauth_scope", "The delegated operator lacks the required role.", "Ask an administrator to review the operator role assignment."),
            "tenant_not_authorized" => (403, "tenant", "The operator has no visible client tenants.", "Ask a policy administrator to grant reviewed tenant visibility."),
            "target_operation_not_authorized" => (403, "policy", "No matching client is authorized for this search operation.", "Ask a policy administrator to review netratel_search/clients target policies and profiles."),
            "search_visibility_limit_exceeded" => (403, "policy", "Tenant visibility exceeds the bounded discovery limit.", "Use exact target reads or ask a policy administrator to narrow discovery visibility."),
            "search_pagination_invalid" => (400, "input", "Discovery pagination is outside the supported bounds.", "Use offset from 0 to 1000000 and limit from 1 to 100."),
            "search_query_too_broad" => (400, "input", "The search exceeds the bounded candidate limit.", "Narrow request.q with a client name or exact agent ID."),
            _ => (400, "input", "The search query must contain at most 512 characters.", "Shorten request.q.")
        };
        return Results.Problem(statusCode: status, title: "MCP discovery was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false, ["summary"] = "MCP discovery was not admitted.",
                ["failure"] = new { code, layer, retryable = false, requiredScopes = new[] { ReadScope },
                    requiredOperation = $"{Tool}/{operation}", safeDetails = details, remediation },
                ["correlationId"] = correlationId
            });
    }
}

internal sealed record ClientSearchCandidate(AgentDirectoryRow Agent, bool Enabled, McpOperatorTargetClassification? Classification, string? TagsJson);

public sealed record McpOperatorTenantDiscovery(int Id, string Name);
