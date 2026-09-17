using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Resolves the server-owned facts needed by V2 MCP operator routes before
/// entering the policy evaluator. Transport adapters supply only already
/// authenticated/delegated identity plus live gateway readiness; tenant,
/// AgentId, classification, tags, and target digest are database-owned facts.
/// </summary>
public sealed class McpOperatorRouteAdmissionService(
    OrchestratorDbContext db,
    IMcpOperatorAuthorization authorization) : IMcpOperatorRouteAdmission
{
    private readonly OrchestratorDbContext _db = db;
    private readonly IMcpOperatorAuthorization _authorization = authorization;

    public async Task<McpOperatorRouteAdmission> EvaluateAsync(
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = McpOperatorOperationCatalog.Find(request.Tool, request.Operation);
        var principal = request.Principal with { ServicePrincipal = request.ServicePrincipal };
        if (descriptor is null)
        {
            var invalid = BuildAccessRequest(request, principal, null, null, null, tenantVisible: false, targetResolved: false, targetEnabled: false);
            return new McpOperatorRouteAdmission(
                McpOperatorDecision.Denied(invalid, "delegated_identity_invalid", McpOperatorAuthorizationLayer.Delegation),
                null);
        }

        if (!request.RequiredScopes.IsSubsetOf(principal.Scopes))
        {
            var missingScope = BuildAccessRequest(request, principal, descriptor, null, null, tenantVisible: false, targetResolved: false, targetEnabled: false);
            return new McpOperatorRouteAdmission(
                McpOperatorDecision.Denied(missingScope, "oauth_scope_missing", McpOperatorAuthorizationLayer.OAuthScope),
                descriptor);
        }

        if (!McpOperationRoleRequirements.IsSatisfied(request.Tool, request.Operation, principal.Roles))
        {
            var missingRole = BuildAccessRequest(request, principal, descriptor, null, null, tenantVisible: false, targetResolved: false, targetEnabled: false);
            return new McpOperatorRouteAdmission(
                McpOperatorDecision.Denied(missingRole, "oauth_role_missing", McpOperatorAuthorizationLayer.OAuthScope),
                descriptor);
        }

        var tenantVisible = await _authorization.HasTenantVisibilityAsync(
            request.Environment,
            principal,
            request.TenantId,
            cancellationToken).ConfigureAwait(false);
        if (!tenantVisible)
        {
            var hidden = BuildAccessRequest(request, principal, descriptor, null, null, tenantVisible: false, targetResolved: false, targetEnabled: false);
            return new McpOperatorRouteAdmission(
                McpOperatorDecision.Denied(hidden, "tenant_not_authorized", McpOperatorAuthorizationLayer.Tenant),
                descriptor);
        }

        var agent = await _db.Agents.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == request.TenantId && candidate.Id == request.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            var missing = BuildAccessRequest(request, principal, descriptor, null, null, tenantVisible: true, targetResolved: false, targetEnabled: false);
            return new McpOperatorRouteAdmission(
                McpOperatorDecision.Denied(missing, "target_not_found", McpOperatorAuthorizationLayer.Target),
                descriptor);
        }

        var profile = await _db.McpOperatorTargetProfiles.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == request.TenantId && candidate.AgentId == request.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null || !TryReadTags(profile.TagsJson, out var tags))
        {
            var unclassified = BuildAccessRequest(request, principal, descriptor, null, null, tenantVisible: true, targetResolved: true, targetEnabled: agent.IsEnabled && agent.Status != AgentStatus.Disabled);
            // A persisted full-Dev grant explicitly includes newly enrolled clients.
            // Other policies still require the authoritative target profile.
            unclassified = unclassified with
            {
                TargetSetDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{request.TenantId}:{request.AgentId:D}:unprofiled")))
            };
            var development = await _authorization.EvaluateAsync(unclassified, cancellationToken).ConfigureAwait(false);
            return new McpOperatorRouteAdmission(
                development.DevelopmentEnvironmentAccess || development.FailureLayer == McpOperatorAuthorizationLayer.Target
                    ? development
                    : McpOperatorDecision.Denied(unclassified, "target_policy_missing", McpOperatorAuthorizationLayer.Policy),
                descriptor);
        }

        var access = BuildAccessRequest(
            request,
            principal,
            descriptor,
            profile.Classification,
            tags,
            tenantVisible: true,
            targetResolved: true,
            targetEnabled: agent.IsEnabled && agent.Status != AgentStatus.Disabled,
            TargetSetDigest(request.TenantId, request.AgentId, profile));
        var decision = await _authorization.EvaluateAsync(access, cancellationToken).ConfigureAwait(false);
        return new McpOperatorRouteAdmission(decision, descriptor);
    }

    public async Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken)
    {
        var admission = await EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!admission.Decision.IsAllowed)
            throw new McpOperatorAdmissionRejectedException(
                admission.Decision.FailureCode ?? "target_policy_missing");

        return await _authorization.RecordAcceptedAsync(
            admission.Decision,
            request.ServicePrincipal,
            cancellationToken).ConfigureAwait(false);
    }

    private static McpOperatorAccessRequest BuildAccessRequest(
        McpOperatorRouteAccessRequest request,
        McpOperatorPrincipal principal,
        McpOperatorOperationDescriptor? descriptor,
        McpOperatorTargetClassification? classification,
        IReadOnlySet<string>? tags,
        bool tenantVisible,
        bool targetResolved,
        bool targetEnabled,
        string? targetSetDigest = null) =>
        new(
            request.Environment,
            principal,
            request.TenantId,
            request.AgentId,
            classification,
            descriptor?.OperationFamily ?? McpOperatorOperationFamily.None,
            descriptor is null ? request.Operation : $"{descriptor.ToolName}/{descriptor.OperationName}",
            request.RequiredScopes,
            descriptor?.ConfirmationClass ?? McpOperatorConfirmationClass.None,
            request.CorrelationId,
            request.RequestId,
            targetSetDigest,
            tags,
            request.McpResource,
            request.McpInstance,
            request.Tool,
            tenantVisible,
            targetResolved,
            targetEnabled,
            request.TargetOnline,
            request.CapabilityAvailable);

    private static bool TryReadTags(string json, out IReadOnlySet<string> tags)
    {
        try
        {
            var values = JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.StringArray);
            if (values is null || values.Any(string.IsNullOrWhiteSpace))
            {
                tags = new HashSet<string>(StringComparer.Ordinal);
                return false;
            }

            tags = values.ToHashSet(StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            tags = new HashSet<string>(StringComparer.Ordinal);
            return false;
        }
    }

    private static string TargetSetDigest(int tenantId, Guid agentId, McpOperatorTargetProfileRecord profile)
    {
        var source = $"{tenantId}:{agentId:D}:{profile.Version}:{(short)profile.Classification}:{profile.TagsJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
