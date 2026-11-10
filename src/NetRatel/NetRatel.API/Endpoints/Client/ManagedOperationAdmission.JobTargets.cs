using System.Security.Claims;
using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

internal sealed partial class ManagedOperationAdmission
{
    private async Task<ManagedOperationAdmissionResult> AdmitJobDiscoveryAsync(ManagedOperationDescriptor descriptor,
        JsonElement request, ClaimsPrincipal user, McpOperatorDecision tenantDecision, string servicePrincipal,
        HttpContext http, CancellationToken ct)
    {
        var source = tenantDecision.Request;
        if (!source.TenantVisible || !source.TargetResolved)
            return Denied("tenant_not_authorized");
        if (!source.RequiredScopes.IsSubsetOf(source.Principal.Scopes) ||
            !McpOperationRoleRequirements.IsSatisfied(descriptor.Tool, descriptor.Operation, source.Principal.Roles))
            return Denied("oauth_scope_missing");
        var directory = http.RequestServices.GetRequiredService<IJobNodeDirectory>();
        var nodes = await directory.ReadAsync(source.TenantId, null, false, ct).ConfigureAwait(false);
        var route = new McpJobTargetPolicyContext(source.Environment, source.Principal, servicePrincipal,
            source.McpResource!, source.McpInstance!, descriptor.Tool, descriptor.Operation, source.TenantId,
            source.RequiredScopes, source.CorrelationId, source.RequestId);
        var allowed = await http.RequestServices.GetRequiredService<McpJobTargetPolicyReader>().AllowedAsync(route, nodes, ct).ConfigureAwait(false);
        var authorized = allowed.Keys.ToHashSet();
        // Read-only discovery is authorized per node. Keeping the original tenant
        // decision (which can be denied for a narrow agent grant) does not mint an
        // aggregate execution grant or record a fabricated tenant acceptance.
        return new(user, tenantDecision, null, servicePrincipal, null) { AuthorizedJobAgentIds = authorized };
    }
}
