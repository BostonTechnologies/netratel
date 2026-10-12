using Microsoft.AspNetCore.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

public static class NetclawAuthorization
{
    public const string ReadPolicy = "NetclawRead";
    public const string ManagePolicy = "NetclawManage";
    public const string SecretUsePolicy = "NetclawSecretUse";
    public const string ConnectionAdminPolicy = "NetclawConnectionAdmin";
    public const string DiscoveryPolicy = "NetclawTenantDiscovery";

    public static void AddPolicies(AuthorizationOptions options)
    {
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ReadPolicy, NetRatelPermissions.TelemetryRead);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ManagePolicy, NetRatelPermissions.ClientManagement);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, SecretUsePolicy, NetRatelPermissions.SecretUse);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ConnectionAdminPolicy, NetRatelPermissions.TenantAdministration);
        MonitoringFlowPermissionAuthorization.AddDiscoveryPolicy(options, DiscoveryPolicy, NetRatelPermissions.TelemetryRead);
    }
}
