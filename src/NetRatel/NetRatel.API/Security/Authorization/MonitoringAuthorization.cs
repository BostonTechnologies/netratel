using Microsoft.AspNetCore.Authorization;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

public static class MonitoringAuthorization
{
    public const string ReadPolicy = "MonitoringRead";
    public const string ManagePolicy = "MonitoringManage";
    public const string AcknowledgePolicy = "MonitoringAcknowledge";
    public const string ClearPolicy = "MonitoringClear";
    public const string BypassPolicy = "MonitoringBypass";
    public const string ReadOrManagePolicy = "MonitoringReadOrManage";
    public const string PermissionsSummaryPolicy = "MonitoringPermissionsSummary";
    public const string DiscoveryPolicy = "MonitoringTenantDiscovery";

    public static void AddPolicies(AuthorizationOptions options)
    {
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ReadPolicy, NetRatelPermissions.MonitoringRead);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ManagePolicy, NetRatelPermissions.MonitoringManage);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, AcknowledgePolicy, NetRatelPermissions.MonitoringAcknowledge);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, ClearPolicy, NetRatelPermissions.MonitoringClear);
        MonitoringFlowPermissionAuthorization.AddSinglePolicy(options, BypassPolicy, NetRatelPermissions.MonitoringBypass);
        MonitoringFlowPermissionAuthorization.AddAnyPolicy(options, ReadOrManagePolicy,
            NetRatelPermissions.MonitoringRead, NetRatelPermissions.MonitoringManage);
        MonitoringFlowPermissionAuthorization.AddAnyPolicy(options, PermissionsSummaryPolicy,
            NetRatelPermissions.MonitoringRead, NetRatelPermissions.MonitoringManage,
            NetRatelPermissions.MonitoringAcknowledge, NetRatelPermissions.MonitoringClear,
            NetRatelPermissions.MonitoringBypass, NetRatelPermissions.MonitoringAllTargets);
        MonitoringFlowPermissionAuthorization.AddDiscoveryPolicy(options, DiscoveryPolicy, NetRatelPermissions.MonitoringRead);
    }
}
