namespace NetRatel.Application.Operations;

public sealed record McpPolicyTenant(int Id, string Name);
public sealed record McpPolicyClient(Guid Id, int TenantId, string Name);
public sealed record McpPolicyPrincipal(McpOperatorPrincipalSelectorKind Kind, string Id, string Name, string Detail);
public sealed record McpPolicyDirectory(IReadOnlyList<McpPolicyTenant> Tenants, IReadOnlyList<McpPolicyClient> Clients,
    IReadOnlyList<McpPolicyPrincipal> Principals, IReadOnlyList<string> Tags, bool HasMore);
