namespace NetRatel.Web.Components.Shared.ServiceLinks;

public sealed record HelpdeskGrantSelection(int? TenantId, string[] Scopes, string[] ResourceIds, string[] RequestDefinitionIds)
{
    public bool IsValid => TenantId is > 0 && Scopes.Length > 0 && ResourceIds.Length + RequestDefinitionIds.Length > 0 &&
        (!Scopes.Contains("netratel.orchestration.invoke") || ResourceIds.Length > 0 && RequestDefinitionIds.Length > 0);
}
