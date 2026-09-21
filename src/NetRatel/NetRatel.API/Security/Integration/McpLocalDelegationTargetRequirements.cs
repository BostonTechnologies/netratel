using NetRatel.Shared.Operations;

namespace NetRatel.API.Security.Integration;

/// <summary>
/// Applies the catalogued target model to the short-lived local exchange
/// assertion. A credential's permission is evaluated only in the target scope
/// that this helper accepts; an unclassified or malformed pairing fails
/// closed before any business route receives the assertion.
/// </summary>
internal static class McpLocalDelegationTargetRequirements
{
    public static bool TryGetAuthorizationTenant(
        McpOperatorDelegation delegation,
        out int? authorizationTenantId)
    {
        authorizationTenantId = null;
        var target = McpOperationTargetCatalog.Find(delegation.Tool, delegation.Operation);
        if (target is null)
            return false;

        switch (target.Model)
        {
            case McpOperationTargetModel.NoBusinessTarget:
                return delegation.TenantId is null && delegation.AgentId is null && delegation.ObjectReference is null;

            case McpOperationTargetModel.Tenant:
                if (delegation.TenantId is not > 0 || delegation.AgentId is not null || delegation.ObjectReference is not null)
                    return false;
                authorizationTenantId = delegation.TenantId;
                return true;

            case McpOperationTargetModel.Agent:
                if (delegation.TenantId is not > 0 || delegation.AgentId is null || delegation.ObjectReference is not null)
                    return false;
                authorizationTenantId = delegation.TenantId;
                return true;

            case McpOperationTargetModel.ObjectDerived:
                if (delegation.TenantId is not > 0 || delegation.AgentId is null)
                    return false;
                if (!delegation.ObjectTargetResolutionEnabled)
                    return delegation.ObjectReference is null;
                if (target.RequiresObjectReference && string.IsNullOrWhiteSpace(delegation.ObjectReference))
                    return false;
                if (delegation.ObjectReference is not null && target.ObjectReferenceKind is null)
                    return false;
                authorizationTenantId = delegation.TenantId;
                return true;

            default:
                return false;
        }
    }
}
